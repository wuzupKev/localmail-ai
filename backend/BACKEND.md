> **Nota de este repositorio.** Esta es una copia de la especificacion que vive en el
> repositorio principal de LocalMail AI, donde esta el frontend. Se incluye aqui porque
> el codigo la cita constantemente (§3, §4, §6...) y un repositorio que remite a un
> documento invisible no sirve de nada. La copia autoritativa es la del repositorio
> principal; si las dos divergen, manda aquella.
>
> Donde el texto dice "este repo" refiriendose al frontend, se refiere al principal.

# LocalMail AI — Especificación del backend

Documento de trabajo para quien implemente el backend. El frontend ya existe en este repo
(`src/`, Next.js 16 + React 19) y hoy funciona contra datos falsos en `src/lib/mock.ts`.
**Los tipos de `mock.ts` son el contrato: el backend debe devolver exactamente esas formas.**

---

## 1. Qué es esto (y qué no)

LocalMail AI es una **capa de triage de correo**, no un cliente de correo.

- Lee por IMAP, analiza en local, y devuelve una priorización.
- Escribe resultados de vuelta como **etiquetas/carpetas IMAP estándar**, para que se vean en Gmail,
  Apple Mail, Thunderbird o el móvil.
- **No** compone correos, no renderiza HTML enriquecido, no gestiona adjuntos, no hace threading propio
  más allá de agrupar por `Message-ID`/`References`.

**No implementes nada de lo que está fuera de ese alcance.** Un cliente de correo completo es un proyecto
de años y no es esto.

### Funcionalidades acordadas (cerradas entre front y backend)

**Dentro del MVP**

1. Triage en 4 buckets: responder / informativo / automatizado / ruido.
2. Cajón "No estoy seguro": confianza < 0,55. El servidor decide la partición, no el front.
3. Corrección que reentrena en caliente. Requisito del front: respuesta en < 300 ms.
4. Deshacer, con evento inverso. Ver `POST /api/corrections/undo`.
5. Búsqueda híbrida BM25 + vectores con RRF, indicando el origen de cada resultado.
6. Escritura a IMAP **en lote y bajo confirmación explícita**.

**Dentro, pero degradable** — si Ollama no está, estos dos desaparecen y el producto sigue
entero, sin errores. Mucha gente no tendrá GPU.

7. Extracción estructurada con esquema JSON forzado.
8. Resumen de hilo bajo demanda. Nunca en la ingesta.

**Fuera del MVP, explícitamente**

Redactar respuestas, mover correos automáticamente, **multi-cuenta**, OAuth, calendario, móvil,
adjuntos y composición. El esquema admite varias cuentas en los cimientos y las consultas ya
acotan por cuenta —una consulta sin acotar da bugs que no se ven hasta que alguien añade el
segundo buzón—, pero la API no tiene dimensión de cuenta y el MVP gestiona uno solo.

### Reglas no negociables

1. **Cero red fuera de IMAP y localhost.** Nada de telemetría, analytics, crash reporting ni CDNs.
   Hay que escribir un test que parchee el socket y falle si el proceso conecta a cualquier host que no sea
   el servidor IMAP configurado o `127.0.0.1`. Ese test es parte del producto, no un extra.
2. **Credenciales en el almacén del sistema operativo** (Windows Credential Manager / DPAPI).
   Nunca en `appsettings.json`, nunca en la base de datos, nunca en texto plano.
3. **Sugerir, nunca actuar.** El backend calcula etiquetas, pero no las escribe a IMAP hasta que llega una
   llamada explícita del usuario. Ningún correo se mueve solo.
4. **Las correcciones se guardan como eventos, no como etiqueta sobrescrita.** Ver §5.
5. **El LLM nunca está en el camino crítico de la ingesta.** Ver §7.

---

## 2. Stack

| Pieza | Elección | Nota |
|---|---|---|
| Runtime | .NET 9, C# | |
| API | ASP.NET Core Minimal APIs | Puerto fijo `5174` |
| IMAP / MIME | **MailKit + MimeKit** | La mejor librería de correo que existe. No la sustituyas |
| HTML → texto | **AngleSharp** | |
| BD | SQLite vía `Microsoft.Data.Sqlite` + `SQLitePCLRaw.bundle_e_sqlite3` | El bundle trae FTS5 |
| Acceso a datos | **Dapper** | SQL explícito. Nada de EF Core |
| Embeddings | `Microsoft.ML.OnnxRuntime` + `multilingual-e5-small` exportado a ONNX | |
| Tokenizer | `Tokenizers.DotNet` o `Microsoft.ML.Tokenizers` | **Riesgo. Ver §10** |
| Vectores | `System.Numerics.Tensors` (SIMD) en memoria | Sin `sqlite-vec`: evita la dependencia nativa |
| Clasificador | SGD logístico propio (~80 líneas) | Necesitas `partial_fit` real |
| LLM | **Ollama** en `localhost:11434` vía `Microsoft.Extensions.AI` | |

**Prohibido**: Semantic Kernel, LangChain, EF Core, Redis, Celery/Hangfire, PostgreSQL, Docker como
requisito, cualquier servicio en la nube. Si crees que necesitas uno, el diseño está mal.

---

## 3. Contrato HTTP

Base: `http://127.0.0.1:5174`. Todo JSON, camelCase, UTF-8.
El frontend lo consumirá con `NEXT_PUBLIC_API_URL`.

### Tipos compartidos

Copia literal de `src/lib/mock.ts`:

```ts
type Bucket = "responder" | "informativo" | "automatizado" | "ruido";
type Signal = { label: string; sign: "pos" | "neg" | "neutral" };

type Email = {
  id: string;
  from: { name: string; email: string };
  subject: string;
  preview: string;          // 140 chars del cuerpo normalizado
  body: string[];           // párrafos de texto plano, ya sin citas ni firmas
  time: string;             // "9:14" si es hoy, "Ayer", "Lun", o "12 oct"
  bucket: Bucket;
  confidence: number;       // 0..1
  signals: Signal[];        // ordenadas por peso descendente, máx. 6
  threadCount: number;
  unread: boolean;
  extracted?: { accion: string; urgencia: "alta"|"media"|"baja"; fechaLimite?: string }; // ISO yyyy-MM-dd
  summary?: string;
};
```

`UMBRAL_CONFIANZA = 0.55`. **La regla del umbral vive una sola vez, y vive en el backend.** El
servidor reparte los correos en cinco cajones (los 4 buckets + `duda`) y el front solo pide el
cajón que toca. `confidence` se devuelve igualmente porque la UI la muestra, pero el front no
filtra por ella. Un correo con `bucket: "responder"` y `confidence: 0.48` sale en `?bucket=duda`,
no en `?bucket=responder`.

### Endpoints

```
GET  /api/health
  → { "status": "ok", "version": "0.1.0" }

GET  /api/emails?bucket=responder&limit=50&cursor=<opaco>
  → { "items": Email[], "nextCursor": string | null }
  Sin `bucket` devuelve todos. Orden: fecha descendente.
  `bucket` acepta los 4 buckets MÁS `duda`. El umbral de confianza se aplica AQUÍ, en el
  servidor, nunca en el front: si el front filtrara, el número del tile y el de la lista
  dejarían de coincidir, y `limit` se rompería (pides 50, te quedan 43, el cursor avanzó 50).
  Invariante: la suma de los cinco cajones es el total, sin solapes ni huecos.

GET  /api/emails/{id}
  → Email

PATCH /api/emails/{id}
  body: { "unread": false }
  → Email
  El front lo llama SOLO en apertura explícita (clic, Enter, hoja de móvil), nunca al navegar
  con j/k: marcar leído al pasar con el teclado dejaría la bandeja entera leída sin leer nada.
  Efecto secundario deliberado: al pasar de no leído a leído incrementa `sender_stats.opened`
  una vez. Es lo que separa `ruido` de `automatizado`, así que cada correo abierto mejora el triage.

GET  /api/counts
  → { "responder": N, "informativo": N, "automatizado": N, "ruido": N, "duda": N, "unread": N }
  Alimenta los cinco tiles de la pantalla principal y el "N sin leer" de la cabecera.
  Mismo SQL que el filtro por cajón: el número del tile es el número de correos que salen
  al pulsarlo. Sin esto habría que pedir los cinco cajones enteros para pintar cinco números.

POST /api/emails/{id}/classification
  body: { "bucket": Bucket, "source": "user" }
  → { "email": Email, "examples": number }
  Registra un evento de corrección y reentrena de forma incremental ANTES de responder.
  `examples` es el total tras el reentrenamiento: el front lo muestra en el toast.

POST /api/emails/{id}/summary
  → 202 { "jobId": string }        // si tarda > 2 s
  → 200 { "summary": string }      // si ya estaba en caché
  Resumen bajo demanda. Nunca se precalcula en la ingesta.

GET  /api/jobs/{jobId}
  → { "status": "pending"|"running"|"done"|"error", "result": any, "error": string|null }

GET  /api/search?q=<texto>&limit=20
  → { "items": [ { "email": Email, "score": number, "source": "bm25"|"vector"|"both" } ] }
  Híbrida. Ver §8.

POST /api/sync            → 202 { "jobId": string }
GET  /api/sync/status     → { "state": "idle"|"syncing", "fetched": number, "total": number, "lastSync": string }

GET  /api/model/status
  → { "examples": 312, "precision": 0.91, "embeddings": "multilingual-e5-small (ONNX, 118M)",
      "slm": "Qwen3 4B - Q4_K_M", "lastTrained": "2026-11-03T09:12:00Z", "indexedEmails": 18427,
      "ollamaAvailable": true,
      "examplesBySource": { "user": 96, "bootstrapSent": 184, "bootstrapFolder": 32, "rule": 0 },
      "bootstrapRan": true,
      "embeddingsAvailable": true, "embeddingsMissingReason": null }
  `precision` y `lastTrained` son `number|null` / `string|null`, NO opcionales: null significa
  "todavia no medido", y el front necesita recibir el campo para saberlo. Es lo contrario de
  `extracted`/`summary` en Email, que se omiten. La asimetria es deliberada.
  `precision` solo sale de pasadas de evaluacion marcadas como representativas: medir sobre
  fixtures propias y servir ese numero seria ensenarle al usuario una tasa de acierto falsa.
  `embeddingsMissingReason` tambien viaja siempre: es el texto que el front muestra en el aviso
  de "modelo de busqueda ausente". Sin embeddings se pierde la busqueda semantica y el
  clasificador, pero el triage por senales sigue funcionando y la API sirve con normalidad.

POST /api/model/reset     → { "examples": 0 }
GET  /api/export          → descarga un .zip con la BD y las correcciones en JSON

POST /api/accounts
  body: { "email": "...", "imapHost": "imap.gmail.com", "imapPort": 993, "password": "..." }
  → { "id": string, "status": "connected" }
  La contraseña va al almacén del SO y NUNCA vuelve a salir por la API.

POST /api/corrections/undo
  → { "email": Email | null, "examples": number }
  Deshace la última corrección con source='user' no deshecha. LIFO.
  NO borra la fila: inserta un evento inverso con source='undo', que no cuenta como ejemplo
  de entrenamiento — si contara, le estaríamos enseñando al modelo la corrección y su contraria.
  Sin pila: 200 con `email: null`, no error, para que el Ctrl+Z del front no tenga que saberlo.

GET  /api/labels/pending
  → { "count": number, "emailIds": string[] }
  Pendiente = correos cuyo bucket actual no coincide con el último escrito en IMAP.
  Requiere la tabla `label_writes` (migración 002): ni `corrections` ni `classifications`
  pueden expresar "ya escrito" por sí solas.

POST /api/labels/apply
  → { "applied": N, "failed": M,
      "results": [ { "emailId": "...", "ok": true, "folder": "LocalMail/Responder" },
                   { "emailId": "...", "ok": false, "error": "..." } ] }
  ÚNICA ruta que escribe en el servidor IMAP, y solo por confirmación explícita del usuario.
  En lote, no de uno en uno: el front acumula correcciones y las aplica todas tras el diálogo.
  Devuelve resultado POR CORREO porque escribir en IMAP falla a mitad de lote con normalidad
  (conexión caída, límite del servidor, carpeta inexistente), y el usuario tiene que saber
  qué se escribió y qué no para decidir si reintenta.
```

Errores: `{ "error": { "code": "imap_auth_failed", "message": "..." } }` con el status HTTP adecuado.

---

## 4. Esquema de datos

SQLite, un único archivo en `%LOCALAPPDATA%/LocalMailAI/localmail.db`.
Migraciones con SQL versionado y `PRAGMA user_version`. Nada de Alembic ni equivalentes.

```sql
CREATE TABLE accounts (
  id TEXT PRIMARY KEY,
  email TEXT NOT NULL UNIQUE,
  imap_host TEXT NOT NULL,
  imap_port INTEGER NOT NULL,
  credential_key TEXT NOT NULL,   -- clave en el almacén del SO, no la contraseña
  last_uid INTEGER,               -- UIDNEXT para sync incremental
  uid_validity INTEGER,
  created_at TEXT NOT NULL
);

CREATE TABLE emails (
  id TEXT PRIMARY KEY,            -- hash estable de Message-ID + account_id
  account_id TEXT NOT NULL REFERENCES accounts(id),
  message_id TEXT NOT NULL,
  thread_id TEXT NOT NULL,        -- raíz de References/In-Reply-To
  uid INTEGER NOT NULL,
  folder TEXT NOT NULL,
  from_name TEXT, from_email TEXT NOT NULL,
  to_json TEXT, cc_json TEXT,
  subject TEXT,
  date_utc TEXT NOT NULL,
  in_reply_to TEXT,
  has_list_unsubscribe INTEGER NOT NULL DEFAULT 0,
  is_auto_submitted INTEGER NOT NULL DEFAULT 0,
  spf_pass INTEGER, dkim_pass INTEGER,
  unread INTEGER NOT NULL DEFAULT 1,
  raw_size INTEGER,
  UNIQUE(account_id, message_id)
);
CREATE INDEX idx_emails_thread ON emails(thread_id);
CREATE INDEX idx_emails_date ON emails(date_utc DESC);
CREATE INDEX idx_emails_sender ON emails(from_email);

-- Texto ya normalizado: sin HTML, sin citas, sin firma. Es lo que ve el modelo.
CREATE TABLE email_text (
  email_id TEXT PRIMARY KEY REFERENCES emails(id) ON DELETE CASCADE,
  body_clean TEXT NOT NULL,
  preview TEXT NOT NULL,
  token_count INTEGER
);

CREATE VIRTUAL TABLE email_fts USING fts5(
  subject, body_clean, from_name, from_email,
  content='', tokenize='unicode61 remove_diacritics 2'
);

CREATE TABLE embeddings (
  email_id TEXT PRIMARY KEY REFERENCES emails(id) ON DELETE CASCADE,
  model TEXT NOT NULL,            -- permite reindexar al cambiar de modelo
  dim INTEGER NOT NULL,
  vec BLOB NOT NULL               -- float32 little-endian
);

-- Estado actual calculado por el sistema.
CREATE TABLE classifications (
  email_id TEXT PRIMARY KEY REFERENCES emails(id) ON DELETE CASCADE,
  bucket TEXT NOT NULL,
  confidence REAL NOT NULL,
  signals_json TEXT NOT NULL,
  model_version INTEGER NOT NULL,
  computed_at TEXT NOT NULL
);

-- HISTORIAL, no estado. Permite reentrenar desde cero y medir mejora.
-- No sobrescribas nunca: inserta siempre una fila nueva.
CREATE TABLE corrections (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  email_id TEXT NOT NULL REFERENCES emails(id) ON DELETE CASCADE,
  bucket_before TEXT,
  bucket_after TEXT NOT NULL,
  source TEXT NOT NULL,           -- 'user' | 'undo' | 'bootstrap_sent' | 'bootstrap_folder' | 'rule'
                                  -- 'undo' NO cuenta como ejemplo de entrenamiento
  created_at TEXT NOT NULL
);

-- Migración 002. Qué se ha escrito ya en IMAP. Sin esta tabla no se puede saber qué
-- correcciones están pendientes: `corrections` guarda el evento y `classifications` el
-- estado actual, pero "pendiente" no existe en ninguna de las dos.
CREATE TABLE label_writes (
  email_id TEXT PRIMARY KEY REFERENCES emails(id) ON DELETE CASCADE,
  folder TEXT NOT NULL,
  bucket TEXT NOT NULL,
  written_at TEXT NOT NULL
);

CREATE TABLE extractions (
  email_id TEXT PRIMARY KEY REFERENCES emails(id) ON DELETE CASCADE,
  accion TEXT, urgencia TEXT, fecha_limite TEXT,
  model TEXT NOT NULL, created_at TEXT NOT NULL
);

CREATE TABLE summaries (
  thread_id TEXT PRIMARY KEY,
  summary TEXT NOT NULL,
  up_to_message_id TEXT NOT NULL, -- invalida si llegan mensajes nuevos
  model TEXT NOT NULL, created_at TEXT NOT NULL
);

-- Cola de trabajo. No metas un broker: esto basta.
CREATE TABLE jobs (
  id TEXT PRIMARY KEY,
  kind TEXT NOT NULL,             -- 'sync' | 'embed' | 'extract' | 'summarize'
  payload_json TEXT,
  state TEXT NOT NULL,            -- 'pending' | 'running' | 'done' | 'error'
  priority INTEGER NOT NULL DEFAULT 100,
  result_json TEXT, error TEXT,
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL
);
CREATE INDEX idx_jobs_pending ON jobs(state, priority, created_at);

-- Caché de reputación del remitente. Alimenta las señales deterministas.
CREATE TABLE sender_stats (
  account_id TEXT NOT NULL, from_email TEXT NOT NULL,
  received INTEGER NOT NULL DEFAULT 0,
  replied INTEGER NOT NULL DEFAULT 0,
  opened INTEGER NOT NULL DEFAULT 0,
  last_seen TEXT,
  PRIMARY KEY(account_id, from_email)
);
```

---

## 5. Pipeline de ingesta

Ordenado por coste creciente. **Cada etapa escribe en la BD y es reanudable.**

```
1. IMAP fetch (MailKit, IDLE para novedades)
2. Parseo MIME (MimeKit) → cabeceras + cuerpo
3. Normalización  ← la etapa que más afecta a la calidad final
     HTML → texto (AngleSharp)
     quitar citas (">", "El ... escribió:", "-----Original Message-----")
     quitar firmas ("--", bloques de contacto al final)
     colapsar espacios, arreglar encodings rotos
4. Señales deterministas  (§6)  → clasificación provisional disponible YA
5. Embedding (ONNX, batch de 32)
6. Clasificador SGD → bucket + confianza + señales
7. [cola, baja prioridad] Extracción estructurada con el SLM
8. [bajo demanda] Resumen de hilo
```

Las etapas 1-6 son síncronas respecto al sync. Las 7-8 van a la tabla `jobs` y las consume un
`BackgroundService` con `System.Threading.Channels`.

**Por qué la normalización importa tanto**: quitar citas y firmas recorta el 50-70% de los tokens de
entrada. Es la optimización de rendimiento más rentable del proyecto y no cuesta nada. No la trates como
un detalle cosmético.

---

## 6. Señales deterministas

Se calculan sin IA, en microsegundos, y **son brutalmente predictivas**. Cada una produce un `Signal` con
texto legible, porque la explicación visible es lo que hace que el usuario confíe o corrija.

| Señal | Fuente | Peso |
|---|---|---|
| `Le has respondido N veces` | `sender_stats.replied` | +++ |
| `Vas en Para:, no en Cc:` | cabecera To/Cc | ++ |
| `Hilo en el que ya participas` | tu dirección en `References` | ++ |
| `Tercer mensaje sin respuesta tuya` | hilo sin salida tuya | +++ |
| `Termina en pregunta directa` | heurística sobre el texto limpio | + |
| `Mismo dominio que tu organización` | dominio | + |
| `List-Unsubscribe presente` | cabecera | −−− |
| `Dirección no-reply` | patrón del remitente | −− |
| `Auto-Submitted / Precedence: bulk` | cabecera | −− |
| `Nunca lo has respondido (0 de N)` | `sender_stats` | −− |
| `Remitente nuevo, sin historial` | `sender_stats.received == 0` | neutral, baja confianza |
| `Remitente verificado (DKIM + SPF)` | `Authentication-Results` | + |

Máximo 6 señales por correo en la respuesta, ordenadas por peso absoluto descendente.

---

## 7. Clasificador

**No uses el LLM para clasificar.** Un SGD logístico sobre `[embedding 384d ‖ señales]` es más preciso,
miles de veces más rápido y reentrenable en caliente. El LLM sólo hace lo generativo.

- Entrada: vector de 384 (e5-small) concatenado con las señales normalizadas.
- Modelo: regresión logística multiclase (4 buckets) con SGD, `partial_fit` tras cada corrección.
- Salida: probabilidades → `bucket` = argmax, `confidence` = max.
- Persiste los pesos en disco; `model_version` se incrementa en cada reentrenamiento.

### Arranque en frío — resuélvelo con datos que ya existen

El día 1 no hay etiquetas. Tres fuentes gratuitas, **en este orden de valor**:

1. **La carpeta de Enviados.** Cada correo al que el usuario respondió es un ejemplo positivo de
   "requería respuesta"; cada uno ignorado durante semanas, uno negativo. En el primer sync esto ya da
   **miles de ejemplos reales sin pedirle nada al usuario.** Es la mejor propiedad del diseño: úsala.
2. **Las carpetas que el usuario ya creó** ("Clientes", "Facturas", "Newsletters") son etiquetas humanas.
3. **Las señales deterministas**, que no necesitan entrenamiento y funcionan desde el minuto cero.

Registra estos ejemplos en `corrections` con `source = 'bootstrap_sent'` / `'bootstrap_folder'`, para
poder distinguirlos de las correcciones reales del usuario al medir.

### Contrato con el LLM (Ollama)

**Siempre con esquema JSON forzado** (`format` en la API de Ollama). Un modelo de 4B no produce JSON
válido de forma fiable en texto libre; con gramática restringida acierta prácticamente siempre.
Desactiva el modo de razonamiento de Qwen3: añade decenas de segundos sin mejorar el resultado.

```json
{ "requiereRespuesta": true, "urgencia": "alta",
  "accion": "Confirmar asistencia a la reunión del jueves",
  "fechaLimite": "2026-11-06" }
```

Si Ollama no está disponible, **el producto sigue funcionando**: sin `extracted` ni `summary`, pero el
triage y la búsqueda intactos. Degradación elegante, nunca error. Mucha gente no tendrá GPU.

---

## 8. Búsqueda híbrida

Ni sólo vectores ni sólo texto. El correo está lleno de nombres propios, números de factura y referencias
exactas donde la búsqueda semántica falla sola.

1. BM25 sobre `email_fts` → top 50.
2. Coseno sobre `embeddings` (SIMD, en memoria) → top 50.
3. Fusión **RRF**: `score = Σ 1/(60 + rank_i)`.
4. Devuelve top 20 con `source` indicando de dónde vino.

Recuerda el prefijo que exige e5: `query:` para la consulta y `passage:` para los documentos.
**Olvidarlo degrada los resultados en silencio** — es el error más común con estos modelos.

---

## 9. Orden de trabajo

Cada hito tiene criterio de aceptación. No pases al siguiente sin cumplirlo.

| # | Hito | Criterio de aceptación |
|---|---|---|
| 0 | **Paridad de tokenizer** (§10) | 100 correos tokenizan idénticos a la referencia Python |
| 1 | Esquema + migraciones | La BD se crea y migra desde cero |
| 2 | Sync IMAP + parseo | 5.000 correos reales ingeridos sin excepciones |
| 3 | Normalización | Inspección manual de 50 correos: sin HTML, sin citas, sin firmas |
| 4 | Señales + API `/api/emails` | El front funciona contra el backend sin tocar componentes |
| 5 | **Set de evaluación** | 250 correos etiquetados a mano + métricas por bucket |
| 6 | Embeddings + clasificador | Supera al baseline de señales del hito 5 |
| 7 | Correcciones + reentrenamiento | `POST /classification` responde en < 300 ms |
| 8 | Búsqueda híbrida | |
| 9 | Ollama: extracción y resúmenes | Funciona, y funciona también si Ollama está apagado |
| 10 | Escritura IMAP | Las etiquetas aparecen en Gmail/Thunderbird |

El hito 5 no es opcional ni posterior: **sin set de evaluación no puedes saber si el LLM aporta algo sobre
las heurísticas**, y estarás optimizando a ciegas.

---

## 10. El único riesgo serio: el tokenizer

ONNX Runtime en C# ejecuta el modelo sin problema. **El riesgo está en tokenizar igual que el original.**
`multilingual-e5-small` usa el tokenizer de XLM-RoBERTa (SentencePiece). Si los IDs no coinciden con la
referencia de Python, los embeddings salen mal y todo lo demás se degrada en silencio.

**Primera tarea del proyecto, antes de escribir nada más:**

1. En Python: `transformers.AutoTokenizer` sobre 100 correos de ejemplo → volcar IDs a JSON.
2. En C#: tokenizar los mismos 100 → comparar uno a uno.
3. ¿Coinciden al 100%? .NET queda desbloqueado y no se vuelve a pensar en esto.
4. ¿No coinciden? Se sabe en la hora 3 del proyecto, no en el mes 2.

`Tokenizers.DotNet` (bindings a la librería Rust de HuggingFace, lee `tokenizer.json` directamente) es la
opción más segura. `Microsoft.ML.Tokenizers` es oficial pero hay que verificar que cubra XLM-RoBERTa.

Los scripts de Python de este paso son cuaderno de laboratorio: **no se distribuyen**. El producto es .NET puro.

---

## 11. Notas para no perder el tiempo

- Las librerías de quitar citas y firmas están todas semi-abandonadas en todos los lenguajes. Asume que
  escribirás reglas propias. Es trabajo de verdad, presupuéstalo.
- `charset-normalizer` no tiene equivalente directo en .NET; MimeKit ya maneja bastante bien los charsets
  mentirosos, pero vas a encontrar correos rotos. Regístralos, no falles.
- Gmail expone las etiquetas como carpetas IMAP con nombres peculiares (`[Gmail]/Enviados` y variantes por
  idioma). Detecta por atributos especiales (`\Sent`, `\All`), nunca por nombre literal.
- Autenticación del MVP: **contraseña de aplicación IMAP, no OAuth.** Los scopes de correo de Google son
  "restricted" y exigen verificación con coste y plazos. Aísla la capa de auth para poder añadir OAuth
  más adelante sin tocar el resto.
- Para menos de 100.000 correos, la búsqueda vectorial por fuerza bruta con SIMD es instantánea.
  No montes un índice ANN hasta haber medido que hace falta.
