# Changelog

Versions are `0.x` while the product is pre-release: the minor number moves on a
feature, the patch number on fixes alone. Backend and frontend carry the same number
and are released together, because the TypeScript types in `frontend/src/lib/mock.ts`
are the API contract and the two cannot drift apart.

## [0.2.1] — 2026-11-06

### Added

- **A licence.** The project was public with none, which by default means all rights
  reserved: nobody could legally use, fork or contribute to it. It is now MIT.

## [0.2.0] — 2026-11-06

First version run end to end against a real mailbox rather than the fixtures. That is
where most of this came from.

### Added

- **Thread summaries** against a local model through Ollama, on demand at
  `POST /api/emails/{id}/summary`, cached per thread and invalidated when a new
  message arrives. The "Summarise" button was previously a mock: a 1400 ms
  `setTimeout` followed by a field the backend never filled.
- `OllamaClient`, talking to `127.0.0.1:11434` only, which is the one destination §1
  allows besides the user's own IMAP server.

### Fixed

- **A sync could not reach any real account after seeding fixtures.** The `--seed`
  placeholder account has no credential, and the pass threw on the first account
  without one, so it died before reaching a real mailbox.
- **One failing account no longer cancels the rest.** A revoked app password is a
  per-account problem: it is recorded and the other accounts still sync. A pass fails
  only when every account fails.
- **Macro precision, recall and F1 always printed `0.000`.** The evaluator still held
  the Spanish bucket names from before the rename, so nothing matched and every bucket
  had support 0. The real figure on the 18 fixtures is macro F1 0.700.
- **Summaries came back as the model thinking out loud.** `/api/generate` glues Qwen3's
  reasoning to the front of the answer and `"think": false` does not prevent it. The
  client now uses `/api/chat`, which separates the two.
- **Long threads returned an empty summary.** `num_predict` budgets reasoning and
  answer together, and the model spent it reasoning. The ceiling is now 3000.
- A timeout no longer reports itself as "Ollama is not installed".

### Documentation

- `README.md` claimed IMAP sync and label write-back were unbuilt; both work.
- It also claimed a correction "retrains the classifier in place, in about 7 ms", and
  that classification was "deterministic signals + logistic regression". Neither is
  true: `Classification/` holds the signals and nothing else, and every message is
  classified at `model_version` 0. Corrections are stored, but there is nothing yet
  that consumes them.

## [0.1.0] — 2026-11-04

Triage pipeline, the HTTP API, corrections with undo, full-text search, IMAP
synchronisation and label write-back, DPAPI credential storage, and the Next.js
interface.
