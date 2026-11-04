-- 002: batch label writes and undo.
--
-- Two things the §4 schema could not express and that the flow agreed with the
-- frontend needs.

-- 1. What has already been written to the IMAP server.
--
-- Without this there is no way to answer "how many labels are still pending": the
-- event is in `corrections` and the current state is in `classifications`, but neither
-- knows what reached the server. The "Apply N labels" button would lie, or we would
-- rewrite already-applied labels on every pass.
--
-- Pending = messages whose current bucket differs from the last one written.
CREATE TABLE label_writes (
  email_id TEXT PRIMARY KEY REFERENCES emails(id) ON DELETE CASCADE,
  folder TEXT NOT NULL,           -- the real IMAP folder/label, e.g. 'LocalMail/Responder'
  bucket TEXT NOT NULL,           -- the bucket that was written, to compare with the current one
  written_at TEXT NOT NULL
);

-- 2. The confidence before a correction, so undo can actually undo.
--
-- A user correction sets confidence to 1.0: the user said so, there is no doubt. But
-- undoing has to put the message back where it was, and that includes its original
-- confidence; otherwise a message that was in "not sure" would reappear asserted in a
-- bucket, which is the opposite of undoing.
--
-- It cannot be recomputed: `signals_json` stores each signal's label and sign, not its
-- weight, so the original score is not reconstructable from the row.
ALTER TABLE corrections ADD COLUMN confidence_before REAL;

-- Index for undo: the log is walked backwards from the most recent entry.
CREATE INDEX idx_corrections_recent ON corrections(id DESC);
