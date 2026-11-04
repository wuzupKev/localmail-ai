# LocalMail AI — backend specification

Working document for whoever implements the backend. The frontend already exists in this
repository (`frontend/`, Next.js 16 + React 19) and today runs against fake data in
`frontend/src/lib/mock.ts`.
**The types in `mock.ts` are the contract: the backend must return exactly those shapes.**

---

## 1. What this is (and what it is not)

LocalMail AI is a **mail triage layer**, not a mail client.

- It reads over IMAP, analyses locally, and returns a prioritisation.
- It writes results back as **standard IMAP labels/folders**, so they show up in Gmail,
  Apple Mail, Thunderbird or on a phone.
- It does **not** compose mail, does not render rich HTML, does not manage attachments, and
  does no threading of its own beyond grouping by `Message-ID`/`References`.

**Do not implement anything outside that scope.** A complete mail client is a multi-year
project and this is not it.

### Agreed features (settled between front and backend)

**In the MVP**

1. Triage into 4 buckets: reply / fyi / automated / noise.
2. A "not sure" drawer: confidence < 0.55. The server decides the partition, not the front.
3. Correction that retrains in place. Frontend requirement: a response in < 300 ms.
4. Undo, via an inverse event. See `POST /api/corrections/undo`.
5. Hybrid BM25 + vector search with RRF, reporting where each result came from.
6. IMAP writes **in batches and under explicit confirmation**.

**In, but degradable** — if Ollama is absent these two disappear and the product stays
whole, with no errors. Plenty of people will not have a GPU.

7. Structured extraction with a forced JSON schema.
8. Thread summary on demand. Never during ingestion.

**Explicitly out of the MVP**

Drafting replies, moving mail automatically, **multiple accounts**, OAuth, calendar, mobile,
attachments and composition. The schema admits several accounts in its foundations and the
queries already scope by account — an unscoped query produces bugs nobody sees until someone
adds a second mailbox — but the API has no account dimension and the MVP manages one.

### Non-negotiable rules

1. **Zero network beyond IMAP and localhost.** No telemetry, analytics, crash reporting or
   CDNs. A test must be written that patches the socket and fails if the process connects to
   any host other than the configured IMAP server or `127.0.0.1`. That test is part of the
   product, not an extra.
2. **Credentials in the operating system's store** (Windows Credential Manager / DPAPI).
   Never in `appsettings.json`, never in the database, never in plain text.
3. **Suggest, never act.** The backend computes labels but does not write them to IMAP until
   an explicit call arrives from the user. No message moves on its own.
4. **Corrections are stored as events, not as an overwritten label.** See §5.
5. **The LLM is never on the critical path of ingestion.** See §7.

---

## 2. Stack

| Piece | Choice | Note |
|---|---|---|
| Runtime | .NET 9, C# | |
| API | ASP.NET Core Minimal APIs | Fixed port `5174` |
| IMAP / MIME | **MailKit + MimeKit** | The best mail library there is. Do not replace it |
| HTML → text | **AngleSharp** | |
| DB | SQLite via `Microsoft.Data.Sqlite` + `SQLitePCLRaw.bundle_e_sqlite3` | The bundle ships FTS5 |
| Data access | **Dapper** | Explicit SQL. No EF Core |
| Embeddings | `Microsoft.ML.OnnxRuntime` + `multilingual-e5-small` exported to ONNX | |
| Tokenizer | `Tokenizers.DotNet` or `Microsoft.ML.Tokenizers` | **Risk. See §10** |
| Vectors | `System.Numerics.Tensors` (SIMD) in memory | No `sqlite-vec`: avoids the native dependency |
| Classifier | Hand-written logistic SGD (~80 lines) | You need a real `partial_fit` |
| LLM | **Ollama** on `localhost:11434` via `Microsoft.Extensions.AI` | |

**Forbidden**: Semantic Kernel, LangChain, EF Core, Redis, Celery/Hangfire, PostgreSQL, Docker
as a requirement, any cloud service. If you think you need one, the design is wrong.

---

## 3. HTTP contract

Base: `http://127.0.0.1:5174`. All JSON, camelCase, UTF-8.
The frontend consumes it through `NEXT_PUBLIC_API_URL`.

### Shared types

A literal copy of `frontend/src/lib/mock.ts`:

```ts
type Bucket = "reply" | "fyi" | "automated" | "noise";
type Signal = { label: string; sign: "pos" | "neg" | "neutral" };

type Email = {
  id: string;
  from: { name: string; email: string };
  subject: string;
  preview: string;          // 140 chars of the normalised body
  body: string[];           // plain-text paragraphs, quotes and signatures already stripped
  time: string;             // "9:14" if today, "Yesterday", "Mon", or "12 Oct"
  bucket: Bucket;
  confidence: number;       // 0..1
  signals: Signal[];        // sorted by descending weight, max. 6
  threadCount: number;
  unread: boolean;
  extracted?: { action: string; urgency: "high"|"medium"|"low"; dueDate?: string }; // ISO yyyy-MM-dd
  summary?: string;
};
```

`CONFIDENCE_THRESHOLD = 0.55`. **The threshold rule lives once, and it lives in the backend.**
The server splits mail into five drawers (the 4 buckets + `unsure`) and the front only asks for
the drawer it needs. `confidence` is returned anyway because the UI shows it, but the front
does not filter on it. A message with `bucket: "reply"` and `confidence: 0.48` comes out
of `?bucket=unsure`, not `?bucket=reply`.

### Endpoints

```
GET  /api/health
  → { "status": "ok", "version": "0.1.0" }

GET  /api/emails?bucket=reply&limit=50&cursor=<opaque>
  → { "items": Email[], "nextCursor": string | null }
  Without `bucket` it returns everything. Order: date descending.
  `bucket` accepts the 4 buckets PLUS `unsure`. The confidence threshold is applied HERE, on
  the server, never on the front: if the front filtered, the tile's number and the list's
  would stop matching, and `limit` would break (you ask for 50, 43 remain, the cursor moved 50).
  Invariant: the five drawers sum to the total, with no overlaps and no gaps.

GET  /api/emails/{id}
  → Email

PATCH /api/emails/{id}
  body: { "unread": false }
  → Email
  The front calls this ONLY on an explicit open (click, Enter, mobile sheet), never when
  navigating with j/k: marking read on keyboard traversal would leave the whole inbox read
  without anything having been read.
  Deliberate side effect: going from unread to read increments `sender_stats.opened` once.
  That is what separates `noise` from `automated`, so every message opened improves triage.

GET  /api/counts
  → { "reply": N, "fyi": N, "automated": N, "noise": N, "unsure": N, "unread": N }
  Feeds the five tiles on the main screen and the "N unread" in the header.
  Same SQL as the drawer filter: the tile's number is the number of messages that come out
  when you press it. Without this you would have to request all five drawers in full to
  paint five numbers.

POST /api/emails/{id}/classification
  body: { "bucket": Bucket, "source": "user" }
  → { "email": Email, "examples": number }
  Records a correction event and retrains incrementally BEFORE responding.
  `examples` is the total after retraining: the front shows it in the toast.

POST /api/emails/{id}/summary
  → 202 { "jobId": string }        // if it takes > 2 s
  → 200 { "summary": string }      // if already cached
  Summary on demand. Never precomputed during ingestion.

GET  /api/jobs/{jobId}
  → { "status": "pending"|"running"|"done"|"error", "result": any, "error": string|null }

GET  /api/search?q=<text>&limit=20
  → { "items": [ { "email": Email, "score": number, "source": "bm25"|"vector"|"both" } ] }
  Hybrid. See §8.

POST /api/sync            → 202 { "jobId": string }
GET  /api/sync/status     → { "state": "idle"|"syncing", "fetched": number, "total": number, "lastSync": string }

GET  /api/model/status
  → { "examples": 312, "precision": 0.91, "embeddings": "multilingual-e5-small (ONNX, 118M)",
      "slm": "Qwen3 4B - Q4_K_M", "lastTrained": "2026-11-03T09:12:00Z", "indexedEmails": 18427,
      "ollamaAvailable": true,
      "examplesBySource": { "user": 96, "bootstrapSent": 184, "bootstrapFolder": 32, "rule": 0 },
      "bootstrapRan": true,
      "embeddingsAvailable": true, "embeddingsMissingReason": null }
  `precision` and `lastTrained` are `number|null` / `string|null`, NOT optional: null means
  "not measured yet", and the front needs to receive the field to know that. It is the
  opposite of `extracted`/`summary` in Email, which are omitted. The asymmetry is deliberate.
  `precision` only comes from evaluation runs marked as representative: measuring against our
  own fixtures and serving that number would be showing the user a fake accuracy rate.
  `embeddingsMissingReason` also always travels: it is the text the front shows in the
  "search model missing" notice. Without embeddings you lose semantic search and the
  classifier, but signal triage keeps working and the API serves normally.

POST /api/model/reset     → { "examples": 0 }
GET  /api/export          → downloads a .zip with the DB and the corrections as JSON

POST /api/accounts
  body: { "email": "...", "imapHost": "imap.gmail.com", "imapPort": 993, "password": "..." }
  → { "id": string, "status": "connected" }
  The password goes to the OS store and NEVER comes back out through the API.

POST /api/corrections/undo
  → { "email": Email | null, "examples": number }
  Undoes the last correction with source='user' that has not been undone. LIFO.
  It does NOT delete the row: it inserts an inverse event with source='undo', which does not
  count as a training example — if it did, we would be teaching the model both the correction
  and its opposite.
  With nothing on the stack: 200 with `email: null`, not an error, so the front's Ctrl+Z does
  not have to know.

GET  /api/labels/pending
  → { "count": number, "emailIds": string[] }
  Pending = messages whose current bucket differs from the last one written to IMAP.
  Requires the `label_writes` table (migration 002): neither `corrections` nor
  `classifications` can express "already written" on their own.

POST /api/labels/apply
  → { "applied": N, "failed": M,
      "results": [ { "emailId": "...", "ok": true, "folder": "LocalMail/Reply" },
                   { "emailId": "...", "ok": false, "error": "..." } ] }
  The ONLY route that writes to the IMAP server, and only on the user's explicit confirmation.
  In batches, not one at a time: the front accumulates corrections and applies them all after
  the dialog. It returns a result PER MESSAGE because writing to IMAP routinely fails halfway
  through a batch (dropped connection, server limit, missing folder), and the user has to know
  what was written and what was not in order to decide whether to retry.
```

Errors: `{ "error": { "code": "imap_auth_failed", "message": "..." } }` with the appropriate
HTTP status.

---

## 4. Data schema

SQLite, a single file at `%LOCALAPPDATA%/LocalMailAI/localmail.db`.
Migrations with versioned SQL and `PRAGMA user_version`. No Alembic or equivalent.

```sql
CREATE TABLE accounts (
  id TEXT PRIMARY KEY,
  email TEXT NOT NULL UNIQUE,
  imap_host TEXT NOT NULL,
  imap_port INTEGER NOT NULL,
  credential_key TEXT NOT NULL,   -- key in the OS store, not the password
  last_uid INTEGER,               -- UIDNEXT for incremental sync
  uid_validity INTEGER,
  created_at TEXT NOT NULL
);

CREATE TABLE emails (
  id TEXT PRIMARY KEY,            -- stable hash of Message-ID + account_id
  account_id TEXT NOT NULL REFERENCES accounts(id),
  message_id TEXT NOT NULL,
  thread_id TEXT NOT NULL,        -- root of References/In-Reply-To
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

-- Already normalised text: no HTML, no quotes, no signature. This is what the model sees.
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

-- Current state as computed by the system.
CREATE TABLE classifications (
  email_id TEXT PRIMARY KEY REFERENCES emails(id) ON DELETE CASCADE,
  bucket TEXT NOT NULL,
  confidence REAL NOT NULL,
  signals_json TEXT NOT NULL,
  model_version INTEGER NOT NULL,
  computed_at TEXT NOT NULL
);

-- HISTORY, not state. Allows retraining from scratch and measuring improvement.
-- Never overwrite: always insert a new row.
CREATE TABLE corrections (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  email_id TEXT NOT NULL REFERENCES emails(id) ON DELETE CASCADE,
  bucket_before TEXT,
  bucket_after TEXT NOT NULL,
  source TEXT NOT NULL,           -- 'user' | 'undo' | 'bootstrap_sent' | 'bootstrap_folder' | 'rule'
                                  -- 'undo' does NOT count as a training example
  created_at TEXT NOT NULL
);

-- Migration 002. What has already been written to IMAP. Without this table there is no way
-- to know which corrections are pending: `corrections` holds the event and `classifications`
-- the current state, but "pending" exists in neither.
CREATE TABLE label_writes (
  email_id TEXT PRIMARY KEY REFERENCES emails(id) ON DELETE CASCADE,
  folder TEXT NOT NULL,
  bucket TEXT NOT NULL,
  written_at TEXT NOT NULL
);

CREATE TABLE extractions (
  email_id TEXT PRIMARY KEY REFERENCES emails(id) ON DELETE CASCADE,
  action TEXT, urgency TEXT, due_date TEXT,
  model TEXT NOT NULL, created_at TEXT NOT NULL
);

CREATE TABLE summaries (
  thread_id TEXT PRIMARY KEY,
  summary TEXT NOT NULL,
  up_to_message_id TEXT NOT NULL, -- invalidates when new messages arrive
  model TEXT NOT NULL, created_at TEXT NOT NULL
);

-- Work queue. Do not add a broker: this is enough.
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
```

---

## 5. Ingestion pipeline

Ordered by increasing cost. **Every stage writes to the DB and is resumable.**

```
1. IMAP fetch (MailKit, IDLE for new mail)
2. MIME parsing (MimeKit) → headers + body
3. Normalisation  ← the stage that most affects final quality
     HTML → text (AngleSharp)
     strip quotes (">", "El ... escribió:", "-----Original Message-----")
     strip signatures ("--", contact blocks at the end)
     collapse whitespace, repair broken encodings
4. Deterministic signals  (§6)  → a provisional classification is available ALREADY
5. Embedding (ONNX, batch of 32)
6. SGD classifier → bucket + confidence + signals
7. [queued, low priority] Structured extraction with the SLM
8. [on demand] Thread summary
```

Stages 1-6 are synchronous with respect to the sync. Stages 7-8 go to the `jobs` table and are
consumed by a `BackgroundService` using `System.Threading.Channels`.

**Why normalisation matters so much**: stripping quotes and signatures cuts 50-70% of the input
tokens. It is the most profitable performance optimisation in the project and it costs nothing.
Do not treat it as a cosmetic detail.

---

## 6. Deterministic signals

They are computed with no AI, in microseconds, and **they are brutally predictive**. Each one
produces a `Signal` with readable text, because the visible explanation is what makes the user
trust it or correct it.

| Signal | Source | Weight |
|---|---|---|
| `You have replied N times` | `sender_stats.replied` | +++ |
| `You are on To:, not Cc:` | To/Cc header | ++ |
| `A thread you are already in` | your address in `References` | ++ |
| `Third message with no reply from you` | thread with no outbound from you | +++ |
| `Ends with a direct question` | heuristic over the clean text | + |
| `Same domain as your organisation` | domain | + |
| `List-Unsubscribe present` | header | −−− |
| `A no-reply address` | sender pattern | −− |
| `Auto-Submitted / Precedence: bulk` | header | −− |
| `You have never replied (0 of N)` | `sender_stats` | −− |
| `New sender, no history` | `sender_stats.received == 0` | neutral, low confidence |
| `Verified sender (DKIM + SPF)` | `Authentication-Results` | + |

At most 6 signals per message in the response, sorted by descending absolute weight.

---

## 7. Classifier

**Do not use the LLM to classify.** A logistic SGD over `[embedding 384d ‖ signals]` is more
accurate, thousands of times faster and retrainable in place. The LLM only does the generative
work.

- Input: the 384-vector (e5-small) concatenated with the normalised signals.
- Model: multiclass logistic regression (4 buckets) with SGD, `partial_fit` after each correction.
- Output: probabilities → `bucket` = argmax, `confidence` = max.
- Persist the weights to disk; `model_version` increments on every retraining.

### Cold start — solve it with data that already exists

On day 1 there are no labels. Three free sources, **in this order of value**:

1. **The Sent folder.** Every message the user replied to is a positive example of "needed a
   reply"; every one ignored for weeks, a negative. On the first sync this already yields
   **thousands of real examples without asking the user for anything.** It is the best property
   of the design: use it.
2. **The folders the user already created** ("Clients", "Invoices", "Newsletters") are human labels.
3. **The deterministic signals**, which need no training and work from minute zero.

Record these examples in `corrections` with `source = 'bootstrap_sent'` / `'bootstrap_folder'`,
so they can be told apart from the user's real corrections when measuring.

### Contract with the LLM (Ollama)

**Always with a forced JSON schema** (`format` in the Ollama API). A 4B model does not reliably
produce valid JSON in free text; with a constrained grammar it gets it right practically always.
Disable Qwen3's reasoning mode: it adds tens of seconds without improving the result.

```json
{ "needsReply": true, "urgency": "high",
  "action": "Confirm attendance at Thursday's meeting",
  "dueDate": "2026-11-06" }
```

If Ollama is unavailable, **the product keeps working**: without `extracted` or `summary`, but
with triage and search intact. Graceful degradation, never an error. Plenty of people will not
have a GPU.

---

## 8. Hybrid search

Neither vectors alone nor text alone. Mail is full of proper nouns, invoice numbers and exact
references where semantic search fails on its own.

1. BM25 over `email_fts` → top 50.
2. Cosine over `embeddings` (SIMD, in memory) → top 50.
3. **RRF** fusion: `score = Σ 1/(60 + rank_i)`.
4. Return the top 20 with `source` indicating where each came from.

Remember the prefix e5 demands: `query:` for the query and `passage:` for the documents.
**Forgetting it degrades results silently** — it is the most common mistake with these models.

---

## 9. Order of work

Every milestone has an acceptance criterion. Do not move to the next without meeting it.

| # | Milestone | Acceptance criterion |
|---|---|---|
| 0 | **Tokenizer parity** (§10) | 100 messages tokenise identically to the Python reference |
| 1 | Schema + migrations | The DB is created and migrated from scratch |
| 2 | IMAP sync + parsing | 5,000 real messages ingested with no exceptions |
| 3 | Normalisation | Manual inspection of 50 messages: no HTML, no quotes, no signatures |
| 4 | Signals + `/api/emails` | The front works against the backend without touching components |
| 5 | **Evaluation set** | 250 hand-labelled messages + per-bucket metrics |
| 6 | Embeddings + classifier | Beats the signal baseline from milestone 5 |
| 7 | Corrections + retraining | `POST /classification` responds in < 300 ms |
| 8 | Hybrid search | |
| 9 | Ollama: extraction and summaries | Works, and also works when Ollama is switched off |
| 10 | IMAP writes | The labels appear in Gmail/Thunderbird |

Milestone 5 is neither optional nor something for later: **without an evaluation set you cannot
know whether the LLM adds anything over the heuristics**, and you will be optimising blind.

---

## 10. The only serious risk: the tokenizer

ONNX Runtime in C# runs the model without trouble. **The risk is tokenising the same way as the
original.** `multilingual-e5-small` uses the XLM-RoBERTa tokenizer (SentencePiece). If the IDs do
not match the Python reference, the embeddings come out wrong and everything else degrades
silently.

**First task of the project, before writing anything else:**

1. In Python: `transformers.AutoTokenizer` over 100 sample messages → dump the IDs to JSON.
2. In C#: tokenise the same 100 → compare one by one.
3. Do they match 100%? .NET is unblocked and this is never thought about again.
4. Do they not match? You know in hour 3 of the project, not in month 2.

`Tokenizers.DotNet` (bindings to HuggingFace's Rust library, reads `tokenizer.json` directly) is
the safest option. `Microsoft.ML.Tokenizers` is official but you have to verify it covers
XLM-RoBERTa.

The Python scripts for this step are a lab notebook: **they are not distributed**. The product is
pure .NET.

---

## 11. Notes to avoid wasting time

- The libraries for stripping quotes and signatures are all semi-abandoned, in every language.
  Assume you will write your own rules. It is real work; budget for it.
- `charset-normalizer` has no direct equivalent in .NET; MimeKit already handles lying charsets
  fairly well, but you will run into broken mail. Log it, do not fail.
- Gmail exposes its labels as IMAP folders with peculiar names (`[Gmail]/Enviados` and variants
  per language). Detect by special attributes (`\Sent`, `\All`), never by literal name.
- MVP authentication: **an IMAP app password, not OAuth.** Google's mail scopes are "restricted"
  and require verification with cost and lead time. Isolate the auth layer so OAuth can be added
  later without touching the rest.
- For fewer than 100,000 messages, brute-force vector search with SIMD is instantaneous.
  Do not build an ANN index until you have measured that you need one.
