-- 003: resultados de las pasadas de evaluacion (BACKEND.md §9, hito 5).
--
-- La precision que sirve /api/model/status tiene que salir de una medicion guardada,
-- no de una constante en el codigo. Mientras esta tabla este vacia, el endpoint
-- devuelve null y el front pinta "sin medir": es la verdad.
--
-- Se guarda cada pasada, no solo la ultima, porque la pregunta que importa no es
-- "cuanto acierta" sino "ha mejorado o empeorado desde el cambio anterior". Con una
-- sola fila sobreescrita no se puede responder, y es exactamente lo que el hito 5
-- existe para evitar: optimizar a ciegas.
CREATE TABLE eval_runs (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  created_at TEXT NOT NULL,

  -- 0 = solo senales deterministas. Sube con cada reentrenamiento del clasificador,
  -- asi que comparar dos filas con model_version distinto responde a la pregunta de
  -- §7: aporta el modelo algo sobre las heuristicas?
  model_version INTEGER NOT NULL,

  -- Nombre del set, para no comparar peras con manzanas: 18 correos sinteticos no son
  -- los 250 reales etiquetados a mano que pide el hito 5.
  eval_set TEXT NOT NULL,
  set_size INTEGER NOT NULL,

  -- Si el set representa correo real. 0 para los sinteticos de desarrollo.
  -- /api/model/status solo sirve la precision de las pasadas representativas: una
  -- cifra medida sobre 18 correos que ha inventado quien escribe el clasificador no
  -- es la precision del producto, y ensenarsela al usuario como tal seria mentirle.
  -- Las no representativas se guardan igual, porque sirven para ver si un cambio
  -- mejora o empeora, que es para lo que existe el hito 5.
  representative INTEGER NOT NULL DEFAULT 0,

  accuracy REAL NOT NULL,

  -- Macro: media sin ponderar de la precision por bucket. Se usa esta y no la
  -- micro porque los cuatro buckets tienen tamanos muy distintos y la micro
  -- quedaria dominada por el mayoritario, escondiendo que el clasificador falla
  -- justo en el cajon pequeno.
  precision_macro REAL NOT NULL,
  recall_macro REAL NOT NULL,
  f1_macro REAL NOT NULL,

  -- Correos que quedaron por debajo del umbral: el sistema no se pronuncio. No es un
  -- fallo, pero una cobertura baja con buena precision solo significa que el modelo
  -- acierta cuando se atreve, y hay que verlo al lado del acierto.
  coverage REAL NOT NULL,

  per_bucket_json TEXT NOT NULL,
  confusion_json TEXT NOT NULL
);

CREATE INDEX idx_eval_runs_recientes ON eval_runs(created_at DESC);
