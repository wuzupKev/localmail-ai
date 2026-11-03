<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/logo-lockup-dark.png">
    <source media="(prefers-color-scheme: light)" srcset="docs/logo-lockup-light.png">
    <img src="docs/logo-lockup-dark.png" alt="LocalMail AI" width="400">
  </picture>
</p>

<p align="center">
  <strong>El backend de LocalMail AI.</strong><br>
  Triage de correo que funciona entero en tu ordenador: .NET 10, SQLite, ONNX Runtime.
</p>

<p align="center">
  <a href="#como-se-ejecuta">Como se ejecuta</a> ·
  <a href="BACKEND.md">Especificacion</a> ·
  <a href="#estado-por-hitos-9-de-la-spec">Estado</a>
</p>

---

Implementacion de [`BACKEND.md`](BACKEND.md). El contrato de datos lo fija
`src/lib/mock.ts` del frontend, que vive en el repositorio principal: este backend
devuelve esas formas exactas.

El frontend esta en el repositorio principal de LocalMail AI. Este servicio escucha
**solo** en `127.0.0.1:5174` y no necesita el frontend para funcionar: se puede usar
entero con `curl`.

## Estado por hitos (§9 de la spec)

| # | Hito | Estado | Criterio de aceptacion |
|---|---|---|---|
| 0 | Paridad de tokenizer | **superado** | 100/100 textos con IDs identicos a la referencia de Python |
| 1 | Esquema + migraciones | **superado** | BD creada desde cero, `user_version = 1`, segundo arranque sin cambios, FTS5 operativo |
| 2 | Sync IMAP + parseo | parcial | parseo MIME y cabeceras listos y probados; falta IMAP y los 5.000 correos reales |
| 3 | Normalizacion | codigo listo | 26 tests en verde; el criterio pide inspeccion manual de 50 correos reales |
| 5 | Set de evaluacion | maquinaria lista | `tools/Evaluate` mide precision/recall/F1 por bucket, cobertura y matriz de confusion, y anota la pasada en `eval_runs`. Falta el set de 250 correos reales |
| 4 | Senales + API `/api/emails` | **superado** | `/api/emails`, `/api/emails/{id}`, `/api/counts` y `PATCH` de leido. Forma exacta de `Email`, filtro por cajon, cursor y errores de §3 |
| 6 | Embeddings + clasificador | parcial | embeddings verificados contra la referencia de Python (coseno minimo 0.99999988). Falta el clasificador SGD |
| 7 | Correcciones + reentrenamiento | parcial | `POST /classification` y `/api/corrections/undo` hechos y en 7 ms (requisito: 300 ms). El reentrenamiento incremental espera al hito 6: todavia no hay modelo que entrenar |
| 8 | Busqueda hibrida | pendiente | |
| 9 | Ollama: extraccion y resumenes | pendiente | |
| 10 | Escritura IMAP | pendiente | |

## Desviaciones conscientes de la spec

1. **`net10.0` en lugar de `net9.0`.** En esta maquina solo hay SDK 10.0.301 y runtime
   10.0.9. Compilar contra net9.0 sin su runtime no serviria de nada. Nada del codigo
   depende de una version concreta: cambiar el `TargetFramework` basta si hace falta.
2. **Ollama no esta instalado.** Por eso la degradacion elegante de §7 (el producto
   funciona sin LLM, solo sin `extracted` ni `summary`) se prueba desde el principio en
   vez de darse por supuesta.

## Estructura

```
backend/
  src/LocalMailAI.Api/      servicio ASP.NET Core en 127.0.0.1:5174
    Data/                   conexion SQLite y migrador (PRAGMA user_version)
    Migrations/*.sql        esquema versionado, embebido en el ensamblado (001, 002)
    Mail/                   normalizacion de cuerpos y lectura de cabeceras
    Classification/         senales deterministas (§6) y baseline provisional
  tests/LocalMailAI.Tests/  xunit
  tools/TokenizerParity/    comprobacion del hito 0
  lab/                      scripts de Python del hito 0. NO se distribuyen
  models/                   pesos y tokenizer. Fuera de git
```

## Como se ejecuta

```bash
# Medir el clasificador (hito 5)
dotnet run --project tools/Evaluate -- --emails fixtures --labels eval/fixtures-18.json

# Poblar la bandeja sin cuenta IMAP (18 .eml de ejemplo)
python lab/build_fixtures.py
dotnet run --project src/LocalMailAI.Api -- --seed fixtures

# API (crea y migra la BD al arrancar)
dotnet run --project src/LocalMailAI.Api
curl http://127.0.0.1:5174/api/health     # {"status":"ok","version":"0.1.0"}

# Tests
dotnet test tests/LocalMailAI.Tests

# Hito 6: paridad de embeddings (necesita el modelo ONNX descargado)
python lab/dump_embeddings.py
dotnet run --project tools/EmbeddingParity

# Hito 0: paridad de tokenizer (necesita Python con transformers)
python lab/build_corpus.py
python lab/dump_reference.py              # descarga el tokenizer a models/
dotnet run --project tools/TokenizerParity
```

La base de datos vive en `%LOCALAPPDATA%/LocalMailAI/localmail.db`. Para no tocar la
del usuario, los tests y las pruebas pasan `Database__Path`.

## El hallazgo del hito 5: los cajones no estan en un solo eje

Es la conclusion de medir, y condiciona todo lo que queda.

`responder` frente al resto es una pregunta **objetiva del correo**: alguien espera algo
de ti, o no. Se resuelve con cuatro cabeceras, y por eso sale 1.00 de precision y 1.00
de recall el dia 1, sin una sola linea de IA.

`informativo`, `automatizado` y `ruido` son todos "correo que no respondes". Lo unico
que los separa es **cuanto le importa al usuario**, y eso no esta en ninguna cabecera:
es una propiedad de la relacion entre esa persona y ese remitente. Las tres llevan
List-Unsubscribe, las tres vienen de maquinas, y el aviso de despliegue se lee mientras
la promocion no.

El primer eje es del correo; el segundo es del usuario. Por eso uno funciona desde el
primer minuto y el otro no puede.

Dos consecuencias que no son opinables:

1. **El bootstrap desde Enviados no es una mejora deseable, es requisito** para que
   tres de los cinco cajones signifiquen algo. Hasta que exista, el producto que
   funciona de verdad es "requiere respuesta frente a todo lo demas".
2. **`sender_stats.opened` es la unica fuente del segundo eje que no depende del
   pasado**, y se alimenta del `PATCH` de leido: el usuario leyendo su correo entrena
   el sistema sin hacer nada extra. Eso ata la curva de mejora al uso, no al
   entrenamiento. Es bueno para el diseno, pero significa que la primera semana es la
   peor, y es justo cuando el usuario decide si se queda.

## Lo que hay que saber antes de seguir

- **No hay un metodo `Embed(texto)`: hay `EmbedPassage` y `EmbedQuery`.** e5 exige el
  prefijo "passage: " para los documentos y "query: " para las consultas, y olvidarlo
  degrada la busqueda EN SILENCIO (§8 lo llama el error mas comun con estos modelos).
  Si la unica forma de llamar al embedder es eligiendo cual de los dos, no se puede
  olvidar. No anadas una sobrecarga sin prefijo "por comodidad".
- **El modelo de embeddings puede faltar y el producto arranca igual.** `TryLoad`
  devuelve null y se registra un aviso: se pierden la busqueda vectorial y el
  clasificador entrenado, pero el triage por senales y BM25 siguen. Misma degradacion
  elegante que con Ollama.
- **El tokenizer `Encode()` ya anade `<s>` y `</s>`.** Lo dice el informe del hito 0.
  No hay que volver a envolver la secuencia al preparar el batch del ONNX.
- **`email_fts` es contentless (`content=''`).** El enlace con `emails` es
  `emails.rowid`, y `VACUUM` puede renumerar esos rowid y desincronizar el indice en
  silencio. El producto no ejecuta `VACUUM`; si alguna vez lo hiciera, hay que
  reconstruir `email_fts` despues. Esta escrito tambien en `001_initial.sql`.
- **Las senales se identifican por `SignalKind`, no por su etiqueta.** La etiqueta es
  texto de interfaz, lleva numeros dentro y se va a traducir; si la logica comparase
  por ella, reescribir una frase cambiaria el triage sin que nadie lo notara.
- **`automatizado` y `ruido` se separan por `sender_stats.opened`.** Las dos son
  maquinas y llevan las mismas cabeceras: el aviso de despliegue se lee, la promocion
  no. Sin esa columna no hay forma determinista de distinguirlas.
- **El cajon "duda" es un cajon derivado, no una columna.** Un correo por debajo de
  UMBRAL_CONFIANZA (0.55) sale de su bucket y cuenta en `duda`. La regla esta escrita
  UNA sola vez en `EmailRepository` y la usan tanto el filtro por cajon como
  `/api/counts`. Tiene que seguir asi: los cinco tiles son la navegacion del front, y
  si la regla se aplicara en dos sitios, el numero del tile no cuadraria con la lista
  que ese tile abre. Hay un test que fija la invariante
  (`Los_recuentos_cuadran_con_las_listas_que_abren`): la suma de los cinco cajones es
  el total de correos, sin solapes ni huecos.
- **Marcar leido alimenta el triage.** `PATCH /api/emails/{id}` con `{unread:false}`
  incrementa `sender_stats.opened` la primera vez, y esa columna es lo unico que
  distingue `automatizado` de `ruido`. No es contabilidad de interfaz.
- **Calibracion del baseline (medido con las fixtures, 18 correos en arranque en frio):**
  responder 6, automatizado 3, informativo 0, ruido 0, **duda 9**. Dos cosas que esto
  ensena y que el hito 5 tiene que medir en serio:
  - Sin historial no hay forma de separar `ruido` de `automatizado`: las dos llevan
    las mismas cabeceras y lo que las distingue es `sender_stats.opened`, que en el
    primer sync esta a cero para todos. Lo arregla el arranque en frio de §7
    (bootstrap desde Enviados y carpetas), no un ajuste de pesos.
  - La mitad de la bandeja cae en "duda" el dia 1, y eso es correcto pero no es
    gratis. El remitente nuevo RESTA confianza (-0.12); no pone un techo. La primera
    version si ponia un techo por debajo del umbral y el efecto era inaceptable: como
    el dia 1 todos los remitentes son nuevos, la bandeja entera caia en "duda" y los
    otros cuatro cajones salian a cero. Eso contradecia a §6, que dice que las senales
    deterministas funcionan desde el minuto cero: un boletin de un remitente
    desconocido sigue siendo claramente un boletin.
- **La hora se formatea en el huso local de la maquina.** El backend corre en el
  ordenador del propio usuario y solo escucha en localhost, asi que su hora local es
  la suya. Los tests construyen los instantes en hora local para no depender del huso.
- **Corregir NO entrena todavia, y hay que decirlo.** `POST /classification` registra
  el evento, deja el correo en el bucket elegido con confianza 1.0 y devuelve el total
  de ejemplos, que es lo que el front ensena en el toast. Pero `model_version` sigue en
  0: no hay clasificador entrenado, asi que la correccion afecta a ESE correo y a nada
  mas. El "aprende de verdad" llega con el hito 6. Los ejemplos se van acumulando
  mientras tanto, que es justo para lo que sirve guardarlos como historial.
- **Una correccion pone la confianza a 1.0.** Lo ha dicho el usuario. Si se quedara con
  la del modelo, un correo dudoso corregido seguiria apareciendo en "No estoy seguro" y
  la correccion pareceria no haber funcionado.
- **Deshacer restaura tambien la confianza**, no solo el bucket. Para eso existe
  `corrections.confidence_before` en la migracion 002: devolver solo el bucket haria
  que un correo que estaba en "No estoy seguro" reapareciera afirmado en un cajon, que
  es lo contrario de deshacer. No se puede recalcular, porque `signals_json` guarda la
  etiqueta y el signo de cada senal pero no su peso.
- **El deshacer recorre el registro, no cuenta deshaceres.** Contar los 'undo' y
  restarlos falla en un caso real: corriges A, corriges B, deshaces B, corriges C; el
  siguiente deshacer tiene que afectar a C, no a A. Hay un test de ese caso exacto.
- **LINEA BASE MEDIDA (solo senales, `model_version` 0, set `fixtures-18`).** Esto es
  lo que el clasificador del hito 6 tiene que superar:

  | metrica | solo §6 | + senales de contenido |
  |---|---|---|
  | cobertura (se pronuncia) | 50% | 56% |
  | acierto sobre lo cubierto | 89% | 90% |
  | precision macro | 0.417 | 0.667 |
  | F1 macro | 0.450 | 0.700 |

  La segunda columna anade dos senales de contenido que mock.ts ya describia (e6, e15)
  pero que no estaban en la tabla de §6: "dice explicitamente: no requiere accion" y
  "acuse de recibo". Dan evidencia PROPIA de `informativo` en lugar de llegar a ese
  cajon por descarte, y lo sacaron de 0.00 a 1.00 de precision.

  Y el desglose dice mucho mas que el total:
  - `responder`: precision 1.00, recall 1.00. Las senales deterministas aciertan de
    pleno en el unico cajon que de verdad importa. §6 tenia razon.
  - `informativo`: era 0.00 y ahora es 1.00 de precision. El problema NO eran los
    pesos: era que el cajon se alcanzaba solo por descarte. En cuanto tiene una senal
    propia ("no requiere accion"), se pronuncia y acierta. Los otros tres informativos
    siguen bajo el umbral, y esos si necesitan historial: un resumen semanal y un
    aviso del banco no se distinguen de `automatizado` por las cabeceras, solo por si
    el usuario los abre.
  - `ruido`: 0.00. Inalcanzable sin historial de aperturas, como ya se habia visto.
  - Un unico fallo rotundo: la newsletter de Zentrix sale `automatizado` en vez de
    `ruido`, con confianza 0.55 exacta, justo en el filo del umbral.

  Conclusion para el hito 6: el modelo no tiene que mejorar `responder`, tiene que
  hacer que `informativo` y `ruido` existan.
- **Una pasada de evaluacion no representativa NO llega a la interfaz.** `eval_runs`
  tiene una columna `representative`, y `/api/model/status` solo sirve la precision de
  las pasadas representativas. Las de desarrollo se guardan igual, porque sirven para
  comparar versiones, pero una cifra medida sobre 18 correos que ha inventado quien
  escribe el clasificador no es la precision del producto. El valor por defecto es
  `false` y hay que declararlo a proposito: si fuera `true`, un set de pruebas acabaria
  en la cara del usuario por olvido.
- **Lo que falta para los hitos de datos reales:** una contrasena de aplicacion IMAP de
  una cuenta de verdad. El hito 2 pide 5.000 correos ingeridos y el hito 5 pide 250
  etiquetados a mano; ninguno de los dos se puede cerrar con datos inventados.
