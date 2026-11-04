<img src="public/brand/logo-lockup-dark.png" alt="LocalMail AI" width="320">

# LocalMail AI

Email triage that runs entirely on your machine. It reads your mailbox over IMAP,
sorts it into four buckets, and writes the result back as ordinary IMAP labels.

## What it solves

A mailbox mixes three kinds of message that need completely different handling:
messages someone is waiting on you for, messages worth reading once, and machine
traffic. Separating them costs time every day, and the cost scales with volume.

The existing options have a trade-off this avoids:

- **Server-side filters** (Gmail rules, Sieve) only match what you can express as a
  rule. They cannot distinguish a client waiting for a reply from a newsletter sent
  by the same domain.
- **Hosted AI assistants** classify well but require uploading your mailbox to a third
  party. For legal, medical or client correspondence that is frequently not an option.

LocalMail AI classifies locally. No message, header or body leaves the machine; the
only network traffic is to your own IMAP server.

It also reports *why* for every decision. Each classification carries its reasons in
plain language — "you have replied to this sender 12 times", "you are in Cc, not To",
"third message with no reply from you" — so a wrong answer can be corrected instead of
being opaque.

## What it does

| Bucket | Contents |
|---|---|
| `responder` | Someone is waiting for a reply |
| `informativo` | Worth reading, no reply needed |
| `automatizado` | Machine mail you do read |
| `ruido` | Marketing and newsletters |

Messages the classifier is not confident about (confidence below 0.55) are not assigned
to a bucket. They are reported separately, so the system says "I don't know" rather
than guessing.

Corrections are stored as events and retrain the classifier incrementally. Labels are
written to your IMAP server only on an explicit request; nothing moves on its own.

## What it does not do

It is not a mail client. It does not compose, reply, send or delete, and it does not
handle attachments, contacts or calendars. It computes labels and proposes them.

## How it works

Two local processes:

```
Next.js UI  ──HTTP──>  .NET service  ──IMAP──>  your mail server
localhost:3000         127.0.0.1:5174
                            │
                            └──> SQLite, one file in %LOCALAPPDATA%
```

Ingestion pipeline, ordered by increasing cost. Each stage writes to the database and
is resumable:

1. IMAP fetch
2. MIME parse
3. Normalisation — HTML to text, quotes and signatures removed
4. Deterministic signals — a provisional classification is available at this point
5. Embedding
6. Classifier
7. Structured extraction (queued, low priority)
8. Thread summary (on demand)

| Component | Implementation |
|---|---|
| Classification | Deterministic header signals + logistic regression (SGD) |
| Embeddings | `multilingual-e5-small`, ONNX Runtime, 384 dimensions |
| Search | BM25 (SQLite FTS5) + vector similarity, fused with RRF |
| Summaries, extraction | Qwen3 4B via Ollama — optional |
| Storage | SQLite, single file |
| Mail | MailKit / MimeKit |

The classifier is not the language model. It is a logistic regression over the
embedding concatenated with the signal vector: more accurate for this task, orders of
magnitude faster, and retrainable in place after each correction. The language model
only does generative work, and the product runs without it.

## Repositories

| Repository | Contents |
|---|---|
| this one | Next.js frontend, specification, documentation |
| [localmail-backend](https://github.com/wuzupKev/localmail-backend) | .NET service, API, classification, tools |

The backend is standalone and usable without the frontend over HTTP.

## Requirements

- Windows 10 or 11 — credential storage uses DPAPI
- .NET SDK 10.0 or later
- Node.js 20 or later
- Python 3.10 or later, only to fetch the model, once
- About 2 GB of disk
- A mailbox with IMAP enabled

## Install

```bash
git clone <this-repo> localmail-ai
cd localmail-ai
git clone https://github.com/wuzupKev/localmail-backend.git backend

# Embedding model, 470 MB, once
pip install transformers onnxruntime
python backend/lab/dump_reference.py
curl -L -o backend/models/multilingual-e5-small/onnx/model.onnx \
  https://huggingface.co/intfloat/multilingual-e5-small/resolve/main/onnx/model.onnx

# Mail account - needs an app password, not your normal password
cd backend
dotnet run --project src/LocalMailAI.Api -- --add-account

# Run
dotnet run --project src/LocalMailAI.Api      # 127.0.0.1:5174
cd .. && npm install && npm run dev           # localhost:3000
```

Step by step, including app password setup per provider and troubleshooting:
[docs/INSTALL.md](docs/INSTALL.md).

## API

Base URL `http://127.0.0.1:5174`. JSON, camelCase, UTF-8.

| Endpoint | Purpose |
|---|---|
| `GET /api/health` | Liveness |
| `GET /api/emails?bucket=&limit=&cursor=` | Inbox, cursor-paginated |
| `GET /api/emails/{id}` | Single message |
| `PATCH /api/emails/{id}` | Mark read or unread |
| `GET /api/counts` | Per-bucket counts |
| `POST /api/emails/{id}/classification` | Correct a classification |
| `POST /api/corrections/undo` | Undo the last correction |
| `GET /api/model/status` | Example counts, precision, model availability |

`bucket` accepts the four bucket names plus `duda` for below-threshold messages. The
threshold rule lives in the backend only, so bucket counts always match the lists those
counts refer to.

Errors are `{"error": {"code", "message"}}` with the matching HTTP status.

## Security model

- Network egress is limited to the configured IMAP server and localhost. No telemetry,
  analytics or CDNs. The automated test enforcing this is not written yet, so this is
  currently a design rule, not a verified guarantee.
- IMAP passwords are encrypted with DPAPI at user scope and stored outside the
  database. The database holds only a lookup key, which does not contain the address.
- Account setup is a console command rather than an HTTP endpoint, so the password
  never travels over HTTP or reaches a request log.
- The service binds to `127.0.0.1` only.

## Status

Working: signal-based triage, the HTTP API above, corrections with undo, full-text
search, local embeddings, and account setup with live IMAP verification.

Not yet: IMAP synchronisation (account setup verifies the connection but does not
download messages), label write-back, summaries and extraction, vector search fused
with BM25, multiple accounts.

Per-milestone detail, including what is measured and what is not, is in the backend
repository's README.

## Licence

Undecided.
