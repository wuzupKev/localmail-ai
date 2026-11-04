<img src="../frontend/public/brand/logo-lockup-dark.png" alt="LocalMail AI" width="320">

# LocalMail AI backend

Local email triage service: .NET 10, SQLite, ONNX Runtime.

Listens on `127.0.0.1:5174` only. The frontend lives in `frontend/`; this service
does not need it and can be driven entirely with `curl`.

The data contract is fixed by `frontend/src/lib/mock.ts`: this backend returns those
exact shapes.

## What it solves

Server-side mail rules can only match what you can express as a rule, and hosted AI
assistants require uploading the mailbox to a third party. This classifies locally:
nothing leaves the machine except IMAP traffic to your own server.

Every classification carries its reasons in plain language, so a wrong answer is
correctable rather than opaque, and a correction retrains the model in place.

## Running it

```bash
# API. Creates and migrates the database on startup.
dotnet run --project src/LocalMailAI.Api
curl http://127.0.0.1:5174/api/health          # {"status":"ok","version":"0.1.0"}

# Register a mail account. Interactive; password is not echoed.
dotnet run --project src/LocalMailAI.Api -- --add-account

# Populate an inbox without a mail account (18 sample .eml files)
python lab/build_fixtures.py
dotnet run --project src/LocalMailAI.Api -- --seed fixtures

# Tests
dotnet test tests/LocalMailAI.Tests

# Measure the classifier against a labelled set
dotnet run --project tools/Evaluate -- --emails fixtures --labels eval/fixtures-18.json

# Verify the embedding model is correctly installed
python lab/dump_embeddings.py
dotnet run --project tools/EmbeddingParity
dotnet run --project tools/EmbeddingParity -- --rank "overdue supplier invoice"

# Verify tokenizer parity against the Python reference
python lab/build_corpus.py
python lab/dump_reference.py                   # downloads the tokenizer into models/
dotnet run --project tools/TokenizerParity
```

The database is at `%LOCALAPPDATA%/LocalMailAI/localmail.db`. Tests and experiments
pass `Database__Path` so they never touch it.

## Layout

```
src/LocalMailAI.Api/      ASP.NET Core service
  Accounts/               interactive IMAP account setup
  Api/                    DTOs matching the frontend contract
  Classification/         deterministic signals and the baseline classifier
  Data/                   SQLite access, migrations, repositories
  Embeddings/             ONNX embedder and pooling
  Mail/                   MIME parsing, body normalisation, ingestion
  Migrations/*.sql        versioned schema, embedded in the assembly
  Model/                  model status
  Security/               DPAPI credential store
tests/LocalMailAI.Tests/  xunit, 119 tests
tools/TokenizerParity/    tokenizer parity against the Python reference
tools/EmbeddingParity/    embedding parity, and semantic ranking demo
tools/Evaluate/           per-bucket precision, recall, F1, confusion matrix
lab/                      Python scripts. Not shipped
eval/                     labelled evaluation sets
models/                   weights and tokenizer. Not in git
```

## Milestone status

| # | Milestone | State | Acceptance |
|---|---|---|---|
| 0 | Tokenizer parity | **done** | 100/100 texts produce IDs identical to the Python reference |
| 1 | Schema and migrations | **done** | database created from scratch, idempotent on restart, FTS5 working |
| 2 | IMAP sync and parsing | partial | MIME and header parsing done and tested; sync pending |
| 3 | Normalisation | code done | 26 tests green; acceptance requires manual inspection of 50 real messages |
| 4 | Signals and `/api/emails` | **done** | full contract shape, bucket filter, cursor paging, §3 error envelope |
| 5 | Evaluation set | tooling done | `tools/Evaluate` reports per-bucket metrics and records runs; the 250-message real set is missing |
| 6 | Embeddings and classifier | partial | embeddings verified against the reference (min cosine 0.99999988); SGD classifier pending |
| 7 | Corrections and retraining | partial | correction and undo endpoints done, 7 ms against a 300 ms budget; incremental retraining waits on milestone 6 |
| 8 | Hybrid search | pending | |
| 9 | Ollama extraction and summaries | pending | |
| 10 | IMAP write-back | pending | |

## Deliberate deviations from the spec

1. **`net10.0` instead of `net9.0`.** Only SDK 10.0.301 and runtime 10.0.9 are present
   on this machine; building against net9.0 without its runtime would be pointless.
   Nothing in the code depends on the version — changing `TargetFramework` is enough.
2. **Ollama is not installed here.** The graceful degradation required by §7 (the
   product works without the LLM, just without `extracted` and `summary`) is therefore
   tested from the start rather than assumed.

## Measured baseline

Deterministic signals only, `model_version` 0, set `fixtures-18`. This is what the
milestone 6 classifier has to beat:

| Metric | §6 signals only | + content signals |
|---|---|---|
| coverage (commits to an answer) | 50% | 56% |
| accuracy over covered | 89% | 90% |
| macro precision | 0.417 | 0.667 |
| macro F1 | 0.450 | 0.700 |

The second column adds two content signals that `mock.ts` already described but that
were missing from the §6 table: "explicitly says no action required" and
"acknowledgement of receipt". They give `informativo` its own positive evidence instead
of it being reached by elimination, which moved it from 0.00 to 1.00 precision.

The breakdown says more than the total:

- `responder`: precision 1.00, recall 1.00. Header signals nail the one bucket that
  matters most, with no model involved.
- `informativo`: 0.00 before, 1.00 precision now. The problem was never the weights; it
  was that the bucket was only reachable by elimination. The remaining three
  `informativo` messages stay below the threshold and do need history: a weekly digest
  and a bank notice are indistinguishable from `automatizado` by headers alone.
- `ruido`: 0.00. Unreachable without open history.
- One outright error: a newsletter classified `automatizado` instead of `ruido`, at
  exactly 0.55 confidence, right on the threshold.

Conclusion for milestone 6: the model does not need to improve `responder`. It needs to
make `informativo` and `ruido` exist.

## The buckets are not on one axis

This is the conclusion of measuring, and it shapes everything that is left.

`responder` versus the rest is an **objective property of the message**: someone is
waiting on you, or they are not. Four headers answer it, which is why it scores 1.00
precision and 1.00 recall on day one with no AI at all.

`informativo`, `automatizado` and `ruido` are all "mail you do not reply to". The only
thing separating them is **how much the user cares**, which is in no header: it is a
property of the relationship between that person and that sender. All three carry
List-Unsubscribe, all three come from machines, and the deploy notice gets read while
the promotion does not.

The first axis belongs to the message; the second belongs to the user. That is why one
works immediately and the other cannot.

Two consequences:

1. **Bootstrapping from the Sent folder is a requirement, not a nice-to-have**, for
   three of the five buckets to mean anything. Until it exists, the product that
   actually works is "needs a reply versus everything else".
2. **`sender_stats.opened` is the only source of the second axis that does not depend on
   the past.** It is fed by the read-state `PATCH`: the user reading their mail trains
   the system without doing anything extra. That ties the improvement curve to usage
   rather than training — good for the design, but it means the first week is the worst,
   and that is exactly when a user decides whether to keep it.

## Things to know before changing this code

- **There is no `Embed(text)` method, only `EmbedPassage` and `EmbedQuery`.** e5
  requires the `passage: ` prefix for documents and `query: ` for queries, and omitting
  it degrades search **silently** — §8 calls it the most common mistake with these
  models. Making the prefix impossible to forget is the point. Do not add a
  prefix-less overload for convenience.
- **The embedding model may be absent and the service still starts.** `TryLoad` returns
  null and logs a warning: vector search and the trained classifier are lost, signal
  triage and BM25 keep working. `/api/model/status` reports `embeddingsAvailable` and
  the reason, so the UI can say what is missing instead of silently doing less.
- **`Encode()` already adds `<s>` and `</s>`.** Do not wrap the sequence again when
  building the ONNX batch.
- **`email_fts` is contentless (`content=''`).** The link to `emails` is `emails.rowid`,
  and `VACUUM` can renumber those rowids and desynchronise the index silently. The
  product never runs `VACUUM`; if it ever does, `email_fts` must be rebuilt afterwards.
- **Signals are identified by `SignalKind`, not by their label.** The label is UI text
  that contains numbers and will be translated; comparing against it would mean a
  rewording silently changes triage behaviour.
- **`automatizado` and `ruido` are separated by `sender_stats.opened`.** Both are
  machines carrying the same headers. Without that column there is no deterministic way
  to tell them apart.
- **The "duda" bucket is derived, not a column.** A message below the 0.55 threshold
  leaves its bucket and counts as `duda`. That rule is written **once**, in
  `EmailRepository`, and used by both the bucket filter and `/api/counts`. It must stay
  that way: the five tiles are the UI's navigation, and if the rule were applied in two
  places a tile's number would not match the list it opens. A test pins the invariant —
  the five buckets sum to the total, with no overlaps or gaps.
- **Marking a message read feeds triage.** `PATCH /api/emails/{id}` with
  `{unread:false}` increments `sender_stats.opened` the first time. That column is the
  only thing distinguishing `automatizado` from `ruido`; it is not UI bookkeeping.
- **A new sender subtracts confidence (-0.12); it does not cap it.** The first version
  did cap it below the threshold, and the effect was unacceptable: since every sender is
  new on day one, the whole inbox fell into `duda` and the other four buckets read as
  zero. That contradicts §6 — deterministic signals work from minute zero, and a
  newsletter from an unknown sender is still obviously a newsletter.
- **Timestamps are formatted in the machine's local timezone.** The service runs on the
  user's own computer and binds to localhost, so its local time is theirs. Tests build
  instants in local time so they do not depend on the timezone.
- **Correcting does not train anything yet.** `POST /classification` records the event,
  moves that message, and returns the example count the UI shows. But `model_version` is
  still 0: there is no trained classifier, so a correction affects that one message and
  nothing else. Real learning arrives with milestone 6; examples accumulate meanwhile,
  which is what storing them as history is for.
- **A correction sets confidence to 1.0.** The user said so. Keeping the model's
  confidence would leave a corrected low-confidence message still showing as "not sure",
  making the correction look like it did nothing.
- **Undo restores confidence as well as the bucket.** That is why
  `corrections.confidence_before` exists in migration 002. Restoring only the bucket
  would make a message that was in "not sure" reappear asserted in a bucket, which is
  the opposite of undoing. It cannot be recomputed: `signals_json` stores each signal's
  label and sign, not its weight.
- **Undo walks the event log; it does not count undos.** Counting `undo` rows and
  subtracting fails in a real sequence: correct A, correct B, undo B, correct C — the
  next undo must affect C, not A. There is a test for that exact case.
- **A non-representative evaluation run never reaches the UI.** `eval_runs` has a
  `representative` column, and `/api/model/status` only serves precision from
  representative runs. Development runs are still recorded, because comparing versions
  is the point, but a number measured over 18 messages invented by whoever wrote the
  classifier is not the product's precision. The default is `false` and must be declared
  deliberately; defaulting to `true` would let a test set reach a user by omission.
- **Pending: the §1 network test.** The spec requires a test that patches the socket and
  fails if the process connects to any host other than the configured IMAP server or
  127.0.0.1, and states that the test "is part of the product, not an extra". It is not
  written. Until it is, zero-network is a design rule followed by hand, not a verified
  guarantee, and the documentation must not claim otherwise.
- **What the data milestones need:** an app password for a real mailbox. Milestone 2
  requires 5,000 ingested messages and milestone 5 requires 250 hand-labelled ones.
  Neither can be closed with invented data.
