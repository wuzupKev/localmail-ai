using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using LocalMailAI.Api.Api;
using LocalMailAI.Api.Classification;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace LocalMailAI.Api.Mail;

/// <summary>Result of ingesting one message.</summary>
/// <param name="EmailId">Stable message id.</param>
/// <param name="Inserted">False if it was already present (a repeated sync).</param>
/// <param name="Bucket">Provisional classification from the signals.</param>
public sealed record IngestResult(string EmailId, bool Inserted, Bucket Bucket, double Confidence);

/// <summary>A sender_stats row. Dapper does not map named tuples.</summary>
internal sealed record SenderStats(long Received, long Replied, long Opened);

/// <summary>
/// Stages 2 to 6 of the BACKEND.md §5 pipeline: MIME parsing, normalisation,
/// deterministic signals and provisional classification, all written to the database.
///
/// Kept separate from the IMAP sync on purpose. The sync only fetches bytes; what is
/// done with them can be tested without a network, and the same path serves both real
/// ingestion and .eml test files.
/// </summary>
public sealed class EmailIngestor(ILogger<EmailIngestor> log)
{
    /// <summary>
    /// Stable id: a hash of Message-ID plus the account (§4). Stable across syncs,
    /// because an IMAP UID can change and a Message-ID cannot.
    /// </summary>
    public static string ComputeId(string accountId, string messageId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{accountId}|{messageId}"));
        return Convert.ToHexStringLower(hash)[..32];
    }

    public async Task<IngestResult> IngestAsync(
        SqliteConnection cn,
        string accountId,
        MimeMessage message,
        uint uid,
        string folder,
        long rawSize)
    {
        ArgumentNullException.ThrowIfNull(cn);
        ArgumentNullException.ThrowIfNull(message);

        var facts = MessageFacts.From(message);
        var id = ComputeId(accountId, facts.MessageId);

        // One transaction per message: if one of 5,000 is broken, that one is lost and
        // not the whole pass. §5 requires each stage to be resumable.
        using var tx = cn.BeginTransaction();

        var alreadyPresent = await cn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM emails WHERE id = @id", new { id }, tx) > 0;

        if (alreadyPresent)
        {
            tx.Rollback();
            return new IngestResult(id, false, Bucket.Informativo, 0);
        }

        var body = BodyNormalizer.FromMessage(message);

        await cn.ExecuteAsync(
            """
            INSERT INTO emails (
                id, account_id, message_id, thread_id, uid, folder,
                from_name, from_email, to_json, cc_json, subject, date_utc, in_reply_to,
                has_list_unsubscribe, is_auto_submitted, spf_pass, dkim_pass, unread, raw_size)
            VALUES (
                @id, @accountId, @messageId, @threadId, @uid, @folder,
                @fromName, @fromEmail, @toJson, @ccJson, @subject, @dateUtc, @inReplyTo,
                @hasListUnsubscribe, @isAutoSubmitted, @spfPass, @dkimPass, @unread, @rawSize)
            """,
            new
            {
                id,
                accountId,
                messageId = facts.MessageId,
                threadId = facts.ThreadId,
                uid,
                folder,
                fromName = facts.FromName,
                fromEmail = facts.FromEmail,
                toJson = JsonSerializer.Serialize(facts.To),
                ccJson = JsonSerializer.Serialize(facts.Cc),
                subject = facts.Subject,
                dateUtc = facts.DateUtc.ToString("O"),
                inReplyTo = facts.InReplyTo,
                hasListUnsubscribe = facts.HasListUnsubscribe ? 1 : 0,
                isAutoSubmitted = facts.IsAutoSubmitted ? 1 : 0,
                spfPass = facts.SpfPass is null ? (int?)null : facts.SpfPass.Value ? 1 : 0,
                dkimPass = facts.DkimPass is null ? (int?)null : facts.DkimPass.Value ? 1 : 0,
                unread = 1,
                rawSize,
            }, tx);

        await cn.ExecuteAsync(
            """
            INSERT INTO email_text (email_id, body_clean, preview, token_count)
            VALUES (@id, @bodyClean, @preview, @tokenCount)
            """,
            new
            {
                id,
                bodyClean = body.Clean,
                preview = body.Preview,
                // The real token count comes from the embedding stage; this is an
                // estimate so work can be ordered by cost before that happens.
                tokenCount = body.Clean.Length / 4,
            }, tx);

        // email_fts is contentless: its rowid has to be the one from emails (see the
        // comment about VACUUM in 001_initial.sql).
        var rowid = await cn.ExecuteScalarAsync<long>(
            "SELECT rowid FROM emails WHERE id = @id", new { id }, tx);

        await cn.ExecuteAsync(
            """
            INSERT INTO email_fts (rowid, subject, body_clean, from_name, from_email)
            VALUES (@rowid, @subject, @bodyClean, @fromName, @fromEmail)
            """,
            new
            {
                rowid,
                subject = facts.Subject ?? string.Empty,
                bodyClean = body.Clean,
                fromName = facts.FromName ?? string.Empty,
                fromEmail = facts.FromEmail,
            }, tx);

        await cn.ExecuteAsync(
            """
            INSERT INTO sender_stats (account_id, from_email, received, replied, opened, last_seen)
            VALUES (@accountId, @fromEmail, 1, 0, 0, @lastSeen)
            ON CONFLICT(account_id, from_email) DO UPDATE SET
                received = received + 1,
                last_seen = MAX(COALESCE(last_seen, ''), @lastSeen)
            """,
            new { accountId, fromEmail = facts.FromEmail, lastSeen = facts.DateUtc.ToString("O") }, tx);

        var ctx = await BuildContextAsync(cn, tx, accountId, id, facts, body.Clean);
        var signals = DeterministicSignals.Compute(facts, ctx);
        var classification = SignalBaseline.Classify(signals, ctx);

        await cn.ExecuteAsync(
            """
            INSERT INTO classifications (email_id, bucket, confidence, signals_json, model_version, computed_at)
            VALUES (@id, @bucket, @confidence, @signalsJson, @modelVersion, @computedAt)
            """,
            new
            {
                id,
                bucket = classification.Bucket.ToWire(),
                confidence = classification.Confidence,
                signalsJson = JsonSerializer.Serialize(
                    classification.Signals.Select(s => new SignalDto(s.Label, s.Sign))),
                // Version 0 = signals only, no trained classifier. Milestone 6 starts
                // incrementing it.
                modelVersion = 0,
                computedAt = DateTimeOffset.UtcNow.ToString("O"),
            }, tx);

        tx.Commit();

        log.LogDebug("Ingested {Id} from {From}: {Bucket} ({Confidence})",
            id, facts.FromEmail, classification.Bucket.ToWire(), classification.Confidence);

        return new IngestResult(id, true, classification.Bucket, classification.Confidence);
    }

    /// <summary>
    /// Gathers the history the signals need: sender statistics and thread state. All
    /// of it comes from the database, none from the message.
    ///
    /// Every query is scoped by account. The MVP handles a single mailbox, but the
    /// schema allows several and an unscoped query would produce signals bleeding
    /// across inboxes: a bug nobody sees until someone adds a second account.
    /// </summary>
    private static async Task<SignalContext> BuildContextAsync(
        SqliteConnection cn,
        SqliteTransaction tx,
        string accountId,
        string emailId,
        MessageFacts facts,
        string cleanBody)
    {
        var account = await cn.QuerySingleOrDefaultAsync<string?>(
            "SELECT email FROM accounts WHERE id = @accountId", new { accountId }, tx);

        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(account))
        {
            own.Add(account.Trim().ToLowerInvariant());
        }

        var stats = await cn.QuerySingleOrDefaultAsync<SenderStats>(
            """
            SELECT received AS Received, replied AS Replied, opened AS Opened
            FROM sender_stats WHERE account_id = @accountId AND from_email = @fromEmail
            """,
            new { accountId, fromEmail = facts.FromEmail }, tx) ?? new SenderStats(0, 0, 0);

        // The message being ingested has already added 1 to received, and the signals
        // want the history BEFORE it: "you have replied 12 times" should not change
        // just because this message arrived.
        var received = (int)Math.Max(0, stats.Received - 1);

        // Thread participation: some message in the thread came from one of the user's
        // own addresses. This requires the Sent folder to have been ingested, which is
        // exactly what §7 asks for in the cold start.
        string[] mine = own.Count == 0 ? [] : [.. own];

        var participates = mine.Length > 0 && await cn.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*) FROM emails
            WHERE account_id = @accountId
              AND thread_id = @threadId
              AND from_email IN @mine
              AND id <> @id
            """,
            new { accountId, threadId = facts.ThreadId, mine, id = emailId }, tx) > 0;

        // Consecutive messages from the sender since the user's last reply.
        var lastFromMe = mine.Length == 0 ? null : await cn.ExecuteScalarAsync<string?>(
            """
            SELECT MAX(date_utc) FROM emails
            WHERE account_id = @accountId
              AND thread_id = @threadId
              AND from_email IN @mine
            """,
            new { accountId, threadId = facts.ThreadId, mine }, tx);

        var consecutive = (int)await cn.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*) FROM emails
            WHERE account_id = @accountId
              AND thread_id = @threadId
              AND from_email = @fromEmail
              AND date_utc > COALESCE(@lastFromMe, '')
            """,
            new { accountId, threadId = facts.ThreadId, fromEmail = facts.FromEmail, lastFromMe }, tx);

        var domain = own.Count > 0 && own.First().Contains('@')
            ? own.First().Split('@')[1]
            : null;

        return new SignalContext
        {
            OwnAddresses = own,
            OrgDomain = domain,
            SenderReceived = received,
            SenderReplied = (int)stats.Replied,
            SenderOpened = (int)stats.Opened,
            IParticipatesInThread = participates,
            ConsecutiveUnanswered = consecutive,
            CleanBody = cleanBody,
        };
    }
}
