-- 003: evaluation run results (BACKEND.md §9, milestone 5).
--
-- The precision served by /api/model/status has to come from a stored measurement,
-- not from a constant in the code. While this table is empty the endpoint returns
-- null and the frontend shows "not measured", which is the truth.
--
-- Every run is kept, not just the last one, because the question that matters is not
-- "how accurate is it" but "has it improved or regressed since the previous change".
-- A single overwritten row cannot answer that, and avoiding exactly that is why
-- milestone 5 exists: no optimising blind.
CREATE TABLE eval_runs (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  created_at TEXT NOT NULL,

  -- 0 = deterministic signals only. It increases with each retraining of the
  -- classifier, so comparing two rows with different model_version answers the
  -- question in §7: does the model add anything over the heuristics?
  model_version INTEGER NOT NULL,

  -- Set name, so apples are not compared with oranges: 18 synthetic messages are not
  -- the 250 real hand-labelled ones milestone 5 asks for.
  eval_set TEXT NOT NULL,
  set_size INTEGER NOT NULL,

  -- Whether the set represents real mail. 0 for synthetic development sets.
  -- /api/model/status only serves precision from representative runs: a figure
  -- measured over 18 messages invented by whoever wrote the classifier is not the
  -- product's precision, and showing it to the user as such would be lying to them.
  -- Non-representative runs are still stored, because they are what makes it possible
  -- to see whether a change improved or regressed things.
  representative INTEGER NOT NULL DEFAULT 0,

  accuracy REAL NOT NULL,

  -- Macro: the unweighted mean of per-bucket precision. Macro and not micro because
  -- the four buckets have very different sizes and micro would be dominated by the
  -- largest one, hiding the fact that the classifier fails precisely on the small one.
  precision_macro REAL NOT NULL,
  recall_macro REAL NOT NULL,
  f1_macro REAL NOT NULL,

  -- Messages that fell below the threshold: the system did not commit. That is not a
  -- failure, but low coverage with good precision only means the model is right when
  -- it dares, and that has to be read alongside the accuracy.
  coverage REAL NOT NULL,

  per_bucket_json TEXT NOT NULL,
  confusion_json TEXT NOT NULL
);

CREATE INDEX idx_eval_runs_recent ON eval_runs(created_at DESC);
