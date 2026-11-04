-- 001_initial: initial schema. Transcribed from BACKEND.md §4.
--
-- Note on email_fts and rowids, which is the trap in this schema: the FTS5 table is
-- declared with content='' (contentless), so it stores no column values and a query
-- can only return the rowid. Since emails.id is TEXT, the link between the two is
-- emails.rowid (implicit: emails is NOT a WITHOUT ROWID table, so it has one).
--
-- Operational consequence: VACUUM can renumber the rowids of a table whose primary key
-- is not INTEGER, which would silently desynchronise the index. For that reason the
-- product NEVER runs VACUUM; if it ever had to, email_fts must be rebuilt afterwards.
-- Written here so nobody discovers it through search results that go missing with no
-- explanation.

CREATE TABLE accounts (
  id TEXT PRIMARY KEY,
  email TEXT NOT NULL UNIQUE,
  imap_host TEXT NOT NULL,
  imap_port INTEGER NOT NULL,
  credential_key TEXT NOT NULL,   -- key into the OS credential store, not the password
  last_uid INTEGER,               -- UIDNEXT, for incremental sync
  uid_validity INTEGER,
  created_at TEXT NOT NULL
);

CREATE TABLE emails (
  id TEXT PRIMARY KEY,            -- stable hash of Message-ID + account_id
  account_id TEXT NOT NULL REFERENCES accounts(id),
  message_id TEXT NOT NULL,
  thread_id TEXT NOT NULL,        -- root of References / In-Reply-To
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

-- Already-normalised text: no HTML, no quotes, no signature. This is what the model
-- sees.
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
  model TEXT NOT NULL,            -- allows reindexing when the model changes
  dim INTEGER NOT NULL,
  vec BLOB NOT NULL               -- float32 little-endian
);

-- The current state computed by the system.
CREATE TABLE classifications (
  email_id TEXT PRIMARY KEY REFERENCES emails(id) ON DELETE CASCADE,
  bucket TEXT NOT NULL,
  confidence REAL NOT NULL,
  signals_json TEXT NOT NULL,
  model_version INTEGER NOT NULL,
  computed_at TEXT NOT NULL
);

-- HISTORY, not state. This is what makes retraining from scratch and measuring
-- improvement possible. Never overwrite: always insert a new row.
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
  action TEXT, urgency TEXT, due_date TEXT,
  model TEXT NOT NULL, created_at TEXT NOT NULL
);

CREATE TABLE summaries (
  thread_id TEXT PRIMARY KEY,
  summary TEXT NOT NULL,
  up_to_message_id TEXT NOT NULL, -- invalidated when new messages arrive
  model TEXT NOT NULL, created_at TEXT NOT NULL
);

-- Work queue. Do not introduce a broker: this is enough.
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

-- Sender reputation cache. Feeds the deterministic signals.
CREATE TABLE sender_stats (
  account_id TEXT NOT NULL, from_email TEXT NOT NULL,
  received INTEGER NOT NULL DEFAULT 0,
  replied INTEGER NOT NULL DEFAULT 0,
  opened INTEGER NOT NULL DEFAULT 0,
  last_seen TEXT,
  PRIMARY KEY(account_id, from_email)
);
