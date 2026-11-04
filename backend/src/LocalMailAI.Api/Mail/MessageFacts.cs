using MimeKit;

namespace LocalMailAI.Api.Mail;

/// <summary>
/// Everything that can be known about a message by reading its headers alone, with no
/// AI and in microseconds. Feeds the deterministic signals of BACKEND.md §6 and the
/// columns of the <c>emails</c> table in §4.
/// </summary>
public sealed record MessageFacts
{
    public required string MessageId { get; init; }

    /// <summary>Thread root: the first References entry, or In-Reply-To, or its own Message-ID.</summary>
    public required string ThreadId { get; init; }

    public string? FromName { get; init; }
    public required string FromEmail { get; init; }
    public required string[] To { get; init; }
    public required string[] Cc { get; init; }
    public string? Subject { get; init; }
    public required DateTimeOffset DateUtc { get; init; }
    public string? InReplyTo { get; init; }

    /// <summary>The References chain, oldest first.</summary>
    public required string[] References { get; init; }

    public bool HasListUnsubscribe { get; init; }

    /// <summary>Auto-Submitted other than "no", or Precedence: bulk/list/junk.</summary>
    public bool IsAutoSubmitted { get; init; }

    /// <summary>From Authentication-Results. Null means the header says nothing.</summary>
    public bool? SpfPass { get; init; }
    public bool? DkimPass { get; init; }

    /// <summary>A no-reply / noreply / do-not-reply style address.</summary>
    public bool IsNoReplyAddress { get; init; }

    public int RecipientCount => To.Length + Cc.Length;

    public static MessageFacts From(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var sender = message.From.Mailboxes.FirstOrDefault()
            ?? message.Sender
            ?? message.ReplyTo.Mailboxes.FirstOrDefault();

        var fromEmail = (sender?.Address ?? string.Empty).Trim().ToLowerInvariant();

        // Some mail has no Message-ID. Deriving a stable one from what is there beats
        // dropping the message or generating a fresh GUID on every sync, which would
        // duplicate the row on each pass.
        var messageId = string.IsNullOrWhiteSpace(message.MessageId)
            ? SyntheticMessageId(fromEmail, message.Subject, message.Date)
            : message.MessageId.Trim();

        var references = message.References?.ToArray() ?? [];
        var inReplyTo = string.IsNullOrWhiteSpace(message.InReplyTo) ? null : message.InReplyTo.Trim();

        // The thread root is the oldest References entry. With no References but an
        // In-Reply-To, that is the closest thing to a root we know about.
        var threadId = references.Length > 0
            ? references[0]
            : inReplyTo ?? messageId;

        return new MessageFacts
        {
            MessageId = messageId,
            ThreadId = threadId,
            FromName = string.IsNullOrWhiteSpace(sender?.Name) ? null : sender.Name.Trim(),
            FromEmail = fromEmail,
            To = Addresses(message.To),
            Cc = Addresses(message.Cc),
            Subject = string.IsNullOrWhiteSpace(message.Subject) ? null : message.Subject.Trim(),
            DateUtc = message.Date.ToUniversalTime(),
            InReplyTo = inReplyTo,
            References = references,
            HasListUnsubscribe = message.Headers.Contains(HeaderId.ListUnsubscribe),
            IsAutoSubmitted = IsAutomated(message),
            SpfPass = AuthenticationResult(message, "spf"),
            DkimPass = AuthenticationResult(message, "dkim"),
            IsNoReplyAddress = IsNoReply(fromEmail),
        };
    }

    private static string[] Addresses(InternetAddressList list) =>
        [.. list.Mailboxes
            .Select(m => (m.Address ?? string.Empty).Trim().ToLowerInvariant())
            .Where(a => a.Length > 0)];

    private static string SyntheticMessageId(string fromEmail, string? subject, DateTimeOffset date)
    {
        var seed = $"{fromEmail}|{subject}|{date.UtcDateTime:O}";
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(seed));
        return $"sin-message-id-{Convert.ToHexStringLower(hash)[..32]}@localmail.local";
    }

    private static bool IsAutomated(MimeMessage message)
    {
        var autoSubmitted = message.Headers["Auto-Submitted"];
        if (!string.IsNullOrWhiteSpace(autoSubmitted) &&
            !autoSubmitted.Trim().StartsWith("no", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var precedence = message.Headers["Precedence"]?.Trim();
        if (precedence is not null &&
            (precedence.Equals("bulk", StringComparison.OrdinalIgnoreCase) ||
             precedence.Equals("list", StringComparison.OrdinalIgnoreCase) ||
             precedence.Equals("junk", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Headers only a bulk sending system sets.
        return message.Headers.Contains("X-Auto-Response-Suppress")
            || message.Headers.Contains("X-Autoreply")
            || message.Headers.Contains("List-Id");
    }

    /// <summary>
    /// Reads Authentication-Results looking for "spf=pass" or "dkim=pass".
    /// Null when the header is absent or does not mention that mechanism: "the check
    /// failed" and "nobody ran the check" are not the same thing.
    /// </summary>
    private static bool? AuthenticationResult(MimeMessage message, string mechanism)
    {
        var found = false;

        foreach (var header in message.Headers)
        {
            if (!header.Field.Equals("Authentication-Results", StringComparison.OrdinalIgnoreCase) &&
                !header.Field.Equals("ARC-Authentication-Results", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = header.Value;
            var i = value.IndexOf(mechanism + "=", StringComparison.OrdinalIgnoreCase);
            if (i < 0)
            {
                continue;
            }

            found = true;
            var rest = value.AsSpan(i + mechanism.Length + 1).TrimStart();
            if (rest.StartsWith("pass", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return found ? false : null;
    }

    private static bool IsNoReply(string address)
    {
        var local = address.Split('@')[0].Replace("-", string.Empty).Replace(".", string.Empty)
            .Replace("_", string.Empty);

        return local.Contains("noreply", StringComparison.OrdinalIgnoreCase)
            || local.Contains("donotreply", StringComparison.OrdinalIgnoreCase)
            || local.Contains("nottobereplied", StringComparison.OrdinalIgnoreCase);
    }
}
