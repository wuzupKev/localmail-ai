using System.Text;
using Dapper;
using LocalMailAI.Api.Data;

namespace LocalMailAI.Api.Model;

/// <summary>
/// Thread summaries (BACKEND.md §5). Runs on demand only: never on sync, never in the
/// background. A summary costs seconds of CPU and the user may never look at the
/// thread, so it is computed when it is asked for and cached afterwards.
/// </summary>
public sealed class SummaryService(Database db, OllamaClient ollama, ILogger<SummaryService> log)
{
    // Enough for the three lines the UI shows. Qwen3 with thinking off writes the
    // answer directly, so this is not padding for reasoning.
    private const int MaxTokens = 220;

    // A whole thread does not fit in a sensible prompt and the tail is what matters:
    // the last messages carry the open question. Characters, not tokens, because the
    // cut only needs to be safe, not exact.
    private const int MaxCharsPerMessage = 1800;
    private const int MaxMessages = 8;

    private const string System = """
        You summarise email threads. Answer in the language the thread is written in.
        Write at most three short sentences, as plain prose with no bullet points, no
        headings and no preamble. Say what the thread is about and what, if anything,
        is pending from the reader. State only what the messages say: never invent
        names, dates, figures or commitments.
        """;

    /// <summary>
    /// Returns the cached summary of the thread the message belongs to, or generates
    /// one. The cache is invalidated by <c>up_to_message_id</c>: when a new message
    /// arrives the stored summary no longer describes the thread.
    /// </summary>
    /// <returns>The summary, or null if the message does not exist.</returns>
    /// <exception cref="OllamaUnavailableException">Ollama is not available.</exception>
    public async Task<string?> ForEmailAsync(string emailId, bool force, CancellationToken ct = default)
    {
        using var cn = db.Open();

        var thread = await cn.QuerySingleOrDefaultAsync<ThreadRow>(
            """
            SELECT thread_id AS ThreadId, account_id AS AccountId
            FROM emails WHERE id = @emailId
            """,
            new { emailId });

        if (thread is null)
        {
            return null;
        }

        // Scoped by account for the same reason ThreadCount is: the same thread
        // reaching two mailboxes is two threads as far as the user is concerned.
        var messages = (await cn.QueryAsync<MessageRow>(
            """
            SELECT e.id AS Id, e.from_name AS FromName, e.from_email AS FromEmail,
                   e.subject AS Subject, e.date_utc AS DateUtc, t.body_clean AS BodyClean
            FROM emails e
            LEFT JOIN email_text t ON t.email_id = e.id
            WHERE e.thread_id = @threadId AND e.account_id = @accountId
            ORDER BY e.date_utc
            """,
            new { threadId = thread.ThreadId, accountId = thread.AccountId })).ToList();

        if (messages.Count == 0)
        {
            return null;
        }

        var lastMessageId = messages[^1].Id;

        if (!force)
        {
            var cached = await cn.QuerySingleOrDefaultAsync<string>(
                """
                SELECT summary FROM summaries
                WHERE thread_id = @threadId AND up_to_message_id = @lastMessageId
                """,
                new { threadId = thread.ThreadId, lastMessageId });

            if (!string.IsNullOrWhiteSpace(cached))
            {
                return cached;
            }
        }

        var summary = await ollama.GenerateAsync(System, BuildPrompt(messages), MaxTokens, ct);

        // Replaces any stale row for this thread, which is the point of the primary
        // key on thread_id: one summary per thread, the current one.
        await cn.ExecuteAsync(
            """
            INSERT INTO summaries (thread_id, summary, up_to_message_id, model, created_at)
            VALUES (@threadId, @summary, @lastMessageId, @model, @now)
            ON CONFLICT(thread_id) DO UPDATE SET
                summary = excluded.summary,
                up_to_message_id = excluded.up_to_message_id,
                model = excluded.model,
                created_at = excluded.created_at
            """,
            new
            {
                threadId = thread.ThreadId,
                summary,
                lastMessageId,
                model = OllamaClient.Model,
                now = DateTimeOffset.UtcNow.ToString("O"),
            });

        log.LogInformation(
            "Summary generated for thread {ThreadId} ({Count} messages)", thread.ThreadId, messages.Count);

        return summary;
    }

    private static string BuildPrompt(List<MessageRow> messages)
    {
        var kept = messages.Count <= MaxMessages
            ? messages
            : messages.GetRange(messages.Count - MaxMessages, MaxMessages);

        var sb = new StringBuilder();
        sb.Append("Subject: ").AppendLine(messages[0].Subject ?? "(no subject)");

        if (kept.Count < messages.Count)
        {
            sb.AppendLine($"({messages.Count - kept.Count} earlier messages omitted.)");
        }

        foreach (var m in kept)
        {
            var body = m.BodyClean ?? string.Empty;
            if (body.Length > MaxCharsPerMessage)
            {
                body = body[..MaxCharsPerMessage] + " […]";
            }

            sb.AppendLine()
              .Append("--- ")
              .Append(string.IsNullOrWhiteSpace(m.FromName) ? m.FromEmail : $"{m.FromName} <{m.FromEmail}>")
              .Append(" · ")
              .Append(m.DateUtc)
              .AppendLine(" ---")
              .AppendLine(body);
        }

        return sb.ToString();
    }

    private sealed record ThreadRow(string ThreadId, string AccountId);

    private sealed record MessageRow(
        string Id, string? FromName, string FromEmail, string? Subject, string DateUtc, string? BodyClean);
}
