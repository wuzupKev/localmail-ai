using System.Text.Json.Serialization;
using Dapper;
using LocalMailAI.Api.Api;
using LocalMailAI.Api.Classification;
using Microsoft.Data.Sqlite;

namespace LocalMailAI.Api.Data;

/// <summary>Training examples by origin (<c>corrections.source</c>).</summary>
public sealed record ExampleCounts(int User, int BootstrapSent, int BootstrapFolder, int Rule)
{
    /// <summary>
    /// Total currently in effect. This is what the frontend shows in its toast. Not
    /// serialised: it travels separately as `examples`, and duplicating it inside the
    /// breakdown would only create two places where the same number could disagree.
    /// </summary>
    [JsonIgnore]
    public int Total => User + BootstrapSent + BootstrapFolder + Rule;
}

/// <summary>
/// Response of a correction or an undo (§3).
///
/// <c>Email</c> is serialised EVEN WHEN NULL, unlike the rest of the API. On an undo
/// with nothing to undo, null is the answer - "there was nothing" - not a missing
/// field. Omitting it would force the frontend to distinguish `email?: Email` from
/// `email: Email | null`, which are two different checks in TypeScript.
/// </summary>
public sealed record CorrectionResult(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    EmailDto? Email,
    int Examples);

/// <summary>
/// User corrections and undo (BACKEND.md §3 and rule 4 of §1).
///
/// The governing rule: corrections are HISTORY, not state. A row is never overwritten
/// or deleted; undo inserts an inverse event. That is what makes it possible to
/// retrain from scratch and to measure whether the model improves, neither of which
/// works if the history is overwritten.
/// </summary>
public sealed class CorrectionsRepository(Database db, EmailRepository emails, ILogger<CorrectionsRepository> log)
{
    /// <summary>
    /// Records a user correction and moves the message into the chosen bucket.
    ///
    /// Confidence becomes 1.0 deliberately: the user said so. Keeping the model's
    /// confidence would leave a corrected low-confidence message still showing under
    /// "not sure", making the correction look like it did nothing.
    /// </summary>
    public async Task<CorrectionResult?> ApplyAsync(string emailId, Bucket bucket, DateTimeOffset now)
    {
        using var cn = db.Open();
        using var tx = cn.BeginTransaction();

        var current = await cn.QuerySingleOrDefaultAsync<CurrentState>(
            """
            SELECT e.id AS EmailId, c.bucket AS Bucket, c.confidence AS Confidence
            FROM emails e
            LEFT JOIN classifications c ON c.email_id = e.id
            WHERE e.id = @emailId
            """,
            new { emailId }, tx);

        if (current is null)
        {
            tx.Rollback();
            return null;
        }

        await cn.ExecuteAsync(
            """
            INSERT INTO corrections (email_id, bucket_before, bucket_after, source, created_at, confidence_before)
            VALUES (@emailId, @bucketBefore, @bucketAfter, 'user', @now, @confidenceBefore)
            """,
            new
            {
                emailId,
                bucketBefore = current.Bucket,
                bucketAfter = bucket.ToWire(),
                now = now.ToString("O"),
                confidenceBefore = current.Confidence,
            }, tx);

        await WriteClassificationAsync(cn, tx, emailId, bucket.ToWire(), 1.0, now);

        var examples = await CountAsync(cn, tx);
        tx.Commit();

        log.LogInformation("Correction of {EmailId}: {Before} -> {After} ({Examples} examples)",
            emailId, current.Bucket ?? "unclassified", bucket.ToWire(), examples.Total);

        return new CorrectionResult(await emails.GetAsync(emailId, now), examples.Total);
    }

    /// <summary>
    /// Undoes the most recent user correction still in effect.
    ///
    /// Returns <c>Email = null</c> with the example total when there is nothing to
    /// undo, rather than an error, so the frontend's Ctrl+Z does not have to track
    /// whether a stack remains.
    /// </summary>
    public async Task<CorrectionResult> UndoAsync(DateTimeOffset now)
    {
        using var cn = db.Open();
        using var tx = cn.BeginTransaction();

        // The log is walked newest first, carrying a count of pending undos. Counting
        // undo rows and subtracting does not work: if the user undoes twice and then
        // corrects another message, the next undo must affect that new correction, not
        // an already-cancelled one.
        var events = await cn.QueryAsync<CorrectionEvent>(
            """
            SELECT id AS Id, email_id AS EmailId, bucket_before AS BucketBefore,
                   bucket_after AS BucketAfter, source AS Source, confidence_before AS ConfidenceBefore
            FROM corrections
            WHERE source IN ('user', 'undo')
            ORDER BY id DESC
            """, tx);

        CorrectionEvent? target = null;
        var pendingUndos = 0;

        foreach (var e in events)
        {
            if (e.Source == "undo")
            {
                pendingUndos++;
                continue;
            }

            if (pendingUndos > 0)
            {
                pendingUndos--;
                continue;
            }

            target = e;
            break;
        }

        if (target is null)
        {
            var nothing = await CountAsync(cn, tx);
            tx.Rollback();
            return new CorrectionResult(null, nothing.Total);
        }

        // An inverse event, not a deletion (§1, rule 4). Tagged source='undo' so it
        // does not count as a training example: if it did, we would be teaching the
        // model both the correction and its opposite.
        await cn.ExecuteAsync(
            """
            INSERT INTO corrections (email_id, bucket_before, bucket_after, source, created_at, confidence_before)
            VALUES (@emailId, @bucketBefore, @bucketAfter, 'undo', @now, @confidenceBefore)
            """,
            new
            {
                emailId = target.EmailId,
                bucketBefore = target.BucketAfter,
                bucketAfter = target.BucketBefore,
                now = now.ToString("O"),
                confidenceBefore = 1.0,
            }, tx);

        // Back to where it was, confidence included. If the message had no
        // classification before the correction, it goes back to having none and the
        // pipeline will pick it up.
        if (target.BucketBefore is null)
        {
            await cn.ExecuteAsync(
                "DELETE FROM classifications WHERE email_id = @emailId",
                new { emailId = target.EmailId }, tx);
        }
        else
        {
            await WriteClassificationAsync(
                cn, tx, target.EmailId, target.BucketBefore,
                target.ConfidenceBefore ?? 0, now);
        }

        var examples = await CountAsync(cn, tx);
        tx.Commit();

        log.LogInformation("Undid correction {Id} of {EmailId}: back to {Bucket} ({Examples} examples)",
            target.Id, target.EmailId, target.BucketBefore ?? "unclassified", examples.Total);

        return new CorrectionResult(await emails.GetAsync(target.EmailId, now), examples.Total);
    }

    /// <summary>
    /// Examples in effect, by origin. Undos are subtracted from the user's
    /// corrections: without that, undoing would RAISE the counter the frontend shows
    /// in its toast immediately after the user pressed Undo.
    /// </summary>
    public async Task<ExampleCounts> CountAsync()
    {
        using var cn = db.Open();
        return await CountAsync(cn, null);
    }

    private static async Task<ExampleCounts> CountAsync(SqliteConnection cn, SqliteTransaction? tx)
    {
        // COUNT and not SUM: SUM over zero rows returns NULL, and zero rows is the
        // state of every fresh installation.
        var row = await cn.QuerySingleAsync<OriginRow>(
            """
            SELECT
              COUNT(CASE WHEN source = 'user'             THEN 1 END) AS [User],
              COUNT(CASE WHEN source = 'bootstrap_sent'   THEN 1 END) AS BootstrapSent,
              COUNT(CASE WHEN source = 'bootstrap_folder' THEN 1 END) AS BootstrapFolder,
              COUNT(CASE WHEN source = 'rule'             THEN 1 END) AS Rule,
              COUNT(CASE WHEN source = 'undo'             THEN 1 END) AS Undo
            FROM corrections
            """, transaction: tx);

        return new ExampleCounts(
            (int)Math.Max(0, row.User - row.Undo),
            (int)row.BootstrapSent,
            (int)row.BootstrapFolder,
            (int)row.Rule);
    }

    private static Task WriteClassificationAsync(
        SqliteConnection cn,
        SqliteTransaction tx,
        string emailId,
        string bucket,
        double confidence,
        DateTimeOffset now)
    {
        // The signals are NOT touched. They remain true facts about the message and
        // they are the explanation of why the system believed something else; erasing
        // them on correction would leave the user unable to see what the model got
        // wrong.
        return cn.ExecuteAsync(
            """
            INSERT INTO classifications (email_id, bucket, confidence, signals_json, model_version, computed_at)
            VALUES (@emailId, @bucket, @confidence, '[]', 0, @now)
            ON CONFLICT(email_id) DO UPDATE SET
                bucket = @bucket,
                confidence = @confidence,
                computed_at = @now
            """,
            new { emailId, bucket, confidence, now = now.ToString("O") }, tx);
    }

    private sealed record CurrentState(string EmailId, string? Bucket, double? Confidence);

    private sealed record CorrectionEvent(
        long Id,
        string EmailId,
        string? BucketBefore,
        string BucketAfter,
        string Source,
        double? ConfidenceBefore);

    private sealed record OriginRow(long User, long BootstrapSent, long BootstrapFolder, long Rule, long Undo);
}
