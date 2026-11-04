using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using LocalMailAI.Api.Api;
using LocalMailAI.Api.Classification;

namespace LocalMailAI.Api.Data;

/// <summary>
/// Read side of the API (BACKEND.md §3). Explicit SQL with Dapper, no ORM deciding on
/// its own which query to run.
///
/// A message below UMBRAL_CONFIANZA does not belong to its bucket: it belongs to the
/// "unsure" bucket. That rule is written ONCE and used by both the bucket filter and the
/// counts. If each applied it separately, the tile numbers would not match the lists
/// those tiles open, and paging would break on top of that: a page of 50 would come
/// back as 43 after filtering client-side.
/// </summary>
public sealed class EmailRepository(Database db)
{
    private const int MaxLimit = 200;

    // One query with LEFT JOINs: extraction, summary and classification may not exist
    // yet - the pipeline fills them in later (§5) - and the message still has to be
    // servable with whatever is available.
    private const string Selection = """
        SELECT  e.id            AS Id,
                e.from_name     AS FromName,
                e.from_email    AS FromEmail,
                e.subject       AS Subject,
                e.date_utc      AS DateUtc,
                e.unread        AS Unread,
                e.thread_id     AS ThreadId,
                t.body_clean    AS BodyClean,
                t.preview       AS Preview,
                c.bucket        AS Bucket,
                c.confidence    AS Confidence,
                c.signals_json  AS SignalsJson,
                x.action        AS Action,
                x.urgency      AS Urgency,
                x.due_date  AS DueDate,
                s.summary       AS Summary,
                (SELECT COUNT(*) FROM emails e2
                  WHERE e2.thread_id = e.thread_id
                    AND e2.account_id = e.account_id) AS ThreadCount   -- scoped by account: a thread reaching two mailboxes is not counted twice
        FROM emails e
        LEFT JOIN email_text      t ON t.email_id  = e.id
        LEFT JOIN classifications c ON c.email_id  = e.id
        LEFT JOIN extractions     x ON x.email_id  = e.id
        LEFT JOIN summaries       s ON s.thread_id = e.thread_id
        """;

    private sealed class Row
    {
        public string Id { get; init; } = string.Empty;
        public string? FromName { get; init; }
        public string FromEmail { get; init; } = string.Empty;
        public string? Subject { get; init; }
        public string DateUtc { get; init; } = string.Empty;
        public long Unread { get; init; }
        public string ThreadId { get; init; } = string.Empty;
        public string? BodyClean { get; init; }
        public string? Preview { get; init; }
        public string? Bucket { get; init; }
        public double? Confidence { get; init; }
        public string? SignalsJson { get; init; }
        public string? Action { get; init; }
        public string? Urgency { get; init; }
        public string? DueDate { get; init; }
        public string? Summary { get; init; }
        public long ThreadCount { get; init; }
    }

    /// <summary>
    /// Counts for the tiles on the main screen. A single query: the frontend asks for
    /// them on every load, and fetching five whole buckets to paint five numbers would
    /// be absurd.
    ///
    /// A message below the threshold counts in "unsure" and NOT in its bucket. Counting
    /// it in both would mean the tiles do not sum to the total and the user sees
    /// numbers that do not add up.
    /// </summary>
    public async Task<CountsDto> CountsAsync()
    {
        using var cn = db.Open();

        // SUM() in SQLite is INTEGER and arrives as long, so it is read into a row
        // type of its own and converted, rather than declaring the contract DTO long.
        var row = await cn.QuerySingleAsync<CountsRow>(
            """
            -- COUNT and not SUM: SUM over zero rows returns NULL, and an empty inbox
            -- is the state of every fresh installation.
            SELECT
              COUNT(CASE WHEN c.confidence >= @threshold AND c.bucket = 'reply'    THEN 1 END) AS Reply,
              COUNT(CASE WHEN c.confidence >= @threshold AND c.bucket = 'fyi'  THEN 1 END) AS Fyi,
              COUNT(CASE WHEN c.confidence >= @threshold AND c.bucket = 'automated' THEN 1 END) AS Automated,
              COUNT(CASE WHEN c.confidence >= @threshold AND c.bucket = 'noise'        THEN 1 END) AS Noise,
              -- Not yet classified also counts as doubt: the pipeline has not reached
              -- that message and asserting a bucket would be making it up.
              COUNT(CASE WHEN c.bucket IS NULL OR c.confidence < @threshold            THEN 1 END) AS Unsure,
              COUNT(CASE WHEN e.unread <> 0                                            THEN 1 END) AS Unread
            FROM emails e
            LEFT JOIN classifications c ON c.email_id = e.id
            """,
            new { threshold = SignalBaseline.ConfidenceThreshold });

        return new CountsDto(
            (int)row.Reply,
            (int)row.Fyi,
            (int)row.Automated,
            (int)row.Noise,
            (int)row.Unsure,
            (int)row.Unread);
    }

    private sealed record CountsRow(
        long Reply,
        long Fyi,
        long Automated,
        long Noise,
        long Unsure,
        long Unread);

    /// <summary>
    /// Marks a message read or unread. Returns the updated message, which is what the
    /// frontend needs to repaint the row without asking again.
    ///
    /// Marking read for the first time increments <c>sender_stats.opened</c>, and that
    /// is NOT bookkeeping: it is the signal separating "automated" from "noise"
    /// (§6). Both are machines carrying the same headers; what tells them apart is
    /// whether the user opens them. Every message opened improves the triage.
    /// </summary>
    public async Task<EmailDto?> SetUnreadAsync(string id, bool unread, DateTimeOffset now)
    {
        using var cn = db.Open();
        using var tx = cn.BeginTransaction();

        var previousState = await cn.QuerySingleOrDefaultAsync<long?>(
            "SELECT unread FROM emails WHERE id = @id", new { id }, tx);

        if (previousState is null)
        {
            tx.Rollback();
            return null;
        }

        var wasUnread = previousState.Value != 0;

        await cn.ExecuteAsync(
            "UPDATE emails SET unread = @value WHERE id = @id",
            new { id, value = unread ? 1 : 0 }, tx);

        // Only on the transition from unread to read, and only once: opening the same
        // message ten times does not make it ten times more interesting.
        if (wasUnread && !unread)
        {
            await cn.ExecuteAsync(
                """
                UPDATE sender_stats
                SET opened = opened + 1
                WHERE (account_id, from_email) IN (
                    SELECT account_id, from_email FROM emails WHERE id = @id
                )
                """,
                new { id }, tx);
        }

        tx.Commit();

        return await GetAsync(id, now);
    }

    public async Task<EmailPageDto> ListAsync(string? bucket, int limit, string? cursor, DateTimeOffset now)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);

        var (cursorDate, cursorId) = DecodeCursor(cursor);

        // One row more than needed is requested: if it arrives there is a next page,
        // which avoids a separate COUNT over the whole table.
        var sql = $"""
            {Selection}
            WHERE (
                        @bucket IS NULL
                     OR (@bucket = 'unsure' AND (c.bucket IS NULL OR c.confidence < @threshold))
                     OR (@bucket <> 'unsure' AND c.bucket = @bucket AND c.confidence >= @threshold)
                  )
              AND (@cursorDate IS NULL OR (e.date_utc, e.id) < (@cursorDate, @cursorId))
            ORDER BY e.date_utc DESC, e.id DESC
            LIMIT @limit
            """;

        using var cn = db.Open();
        var rows = (await cn.QueryAsync<Row>(sql, new
        {
            bucket,
            cursorDate,
            cursorId,
            limit = limit + 1,
            threshold = SignalBaseline.ConfidenceThreshold,
        })).ToList();

        string? next = null;
        if (rows.Count > limit)
        {
            var last = rows[limit - 1];
            next = EncodeCursor(last.DateUtc, last.Id);
            rows.RemoveRange(limit, rows.Count - limit);
        }

        return new EmailPageDto([.. rows.Select(r => Map(r, now))], next);
    }

    public async Task<EmailDto?> GetAsync(string id, DateTimeOffset now)
    {
        using var cn = db.Open();
        var row = await cn.QuerySingleOrDefaultAsync<Row>(
            $"{Selection}\nWHERE e.id = @id", new { id });

        return row is null ? null : Map(row, now);
    }

    private static EmailDto Map(Row r, DateTimeOffset now)
    {
        var date = DateTimeOffset.Parse(r.DateUtc, null, DateTimeStyles.RoundtripKind);

        var signals = string.IsNullOrWhiteSpace(r.SignalsJson)
            ? []
            : JsonSerializer.Deserialize<SignalDto[]>(r.SignalsJson) ?? [];

        var body = string.IsNullOrWhiteSpace(r.BodyClean)
            ? []
            : r.BodyClean.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new EmailDto
        {
            Id = r.Id,
            From = new MailboxDto(r.FromName ?? r.FromEmail, r.FromEmail),
            Subject = r.Subject ?? "(sin asunto)",
            Preview = r.Preview ?? string.Empty,
            Body = body,
            Time = RelativeTime.Format(date, now),

            // A message that is not classified yet comes out as informativo with
            // confidence 0: the frontend routes it to "not sure" because it is below
            // the threshold, which is exactly what we mean while the pipeline has not
            // reached it.
            Bucket = r.Bucket ?? "fyi",
            Confidence = r.Confidence ?? 0,
            Signals = signals,

            ThreadCount = (int)r.ThreadCount,
            Unread = r.Unread != 0,
            Extracted = r.Action is null ? null : new ExtractedDto(r.Action, r.Urgency ?? "medium", r.DueDate),

            // §5: a summary only makes sense on long threads.
            Summary = r.ThreadCount >= 3 ? r.Summary : null,
        };
    }

    // The cursor is opaque to the client (§3) but readable to us: the date and id of
    // the last row served. Without the id, two messages sharing a date would make
    // paging skip one or repeat another.
    private static string EncodeCursor(string dateUtc, string id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{dateUtc}|{id}"));

    private static (string? Date, string? Id) DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return (null, null);
        }

        try
        {
            var plain = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var split = plain.IndexOf('|');
            return split <= 0 ? (null, null) : (plain[..split], plain[(split + 1)..]);
        }
        catch (FormatException)
        {
            // A tampered cursor, or one from another version: serving the first page is
            // more useful than an error the frontend cannot act on.
            return (null, null);
        }
    }
}
