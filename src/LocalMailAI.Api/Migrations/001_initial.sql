-- 001_initial: esquema inicial. Transcripcion de BACKEND.md §4.
--
-- Nota sobre email_fts y los rowid, que es la trampa de este esquema:
-- la tabla FTS5 se declara con content='' (sin contenido), asi que no guarda los
-- valores de las columnas y una consulta solo puede devolver el rowid. Como
-- emails.id es TEXT, el enlace entre ambas es emails.rowid (implicito: emails NO
-- es WITHOUT ROWID, luego lo tiene).
--
-- Consecuencia operativa: VACUUM puede renumerar los rowid de una tabla cuya
-- clave primaria no es INTEGER, y eso desincronizaria el indice en silencio. Por
-- eso el producto NUNCA ejecuta VACUUM; si alguna vez hiciera falta, hay que
-- reconstruir email_fts despues. Queda escrito aqui para que nadie lo descubra
-- a base de resultados de busqueda que faltan sin explicacion.

CREATE TABLE accounts (
  id TEXT PRIMARY KEY,
  email TEXT NOT NULL UNIQUE,
  imap_host TEXT NOT NULL,
  imap_port INTEGER NOT NULL,
  credential_key TEXT NOT NULL,   -- clave en el almacen del SO, no la contrasena
  last_uid INTEGER,               -- UIDNEXT para sync incremental
  uid_validity INTEGER,
  created_at TEXT NOT NULL
);

CREATE TABLE emails (
  id TEXT PRIMARY KEY,            -- hash estable de Message-ID + account_id
  account_id TEXT NOT NULL REFERENCES accounts(id),
  message_id TEXT NOT NULL,
  thread_id TEXT NOT NULL,        -- raiz de References/In-Reply-To
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
  source TEXT NOT NULL,           -- 'user' | 'bootstrap_sent' | 'bootstrap_folder' | 'rule'
  created_at TEXT NOT NULL
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

-- Cache de reputacion del remitente. Alimenta las senales deterministas.
CREATE TABLE sender_stats (
  account_id TEXT NOT NULL, from_email TEXT NOT NULL,
  received INTEGER NOT NULL DEFAULT 0,
  replied INTEGER NOT NULL DEFAULT 0,
  opened INTEGER NOT NULL DEFAULT 0,
  last_seen TEXT,
  PRIMARY KEY(account_id, from_email)
);
