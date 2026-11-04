using System.Text.RegularExpressions;
using LocalMailAI.Api.Mail;

namespace LocalMailAI.Api.Classification;

// Note on language: comments and identifiers are English; the signal LABELS below stay
// in Spanish on purpose. They are user-facing copy rendered by the frontend, which is
// Spanish, and `src/lib/mock.ts` declares them verbatim. Translating them would be a
// product change coordinated with the frontend, not a cleanup.

/// <summary>Triage bucket. Same four values as <c>Bucket</c> in src/lib/mock.ts.</summary>
public enum Bucket
{
    Responder,
    Informativo,
    Automatizado,
    Ruido,
}

public static class BucketNames
{
    /// <summary>The frontend expects these exact strings, lowercase and in Spanish.</summary>
    public static string ToWire(this Bucket b) => b switch
    {
        Bucket.Responder => "responder",
        Bucket.Informativo => "informativo",
        Bucket.Automatizado => "automatizado",
        Bucket.Ruido => "ruido",
        _ => throw new ArgumentOutOfRangeException(nameof(b), b, null),
    };

    public static Bucket Parse(string value) => value switch
    {
        "responder" => Bucket.Responder,
        "informativo" => Bucket.Informativo,
        "automatizado" => Bucket.Automatizado,
        "ruido" => Bucket.Ruido,
        _ => throw new ArgumentException($"unknown bucket: {value}", nameof(value)),
    };
}

/// <summary>
/// Stable identity for each signal in §6.
///
/// It exists so the logic never depends on the label text. The label is UI copy that
/// contains numbers and will be translated; if the classifier compared against it, a
/// rewording would change triage behaviour without anyone noticing.
/// </summary>
public enum SignalKind
{
    RepliedBefore,
    NewSender,
    NeverReplied,
    ToNotCc,
    CcNotTo,
    ThreadParticipant,
    ThirdUnanswered,
    EndsWithQuestion,
    SameOrgDomain,
    ListUnsubscribe,
    NoReplyAddress,
    AutoSubmitted,
    Authenticated,
    ExplicitlyNoAction,
    Acknowledgement,
}

/// <summary>A signal as the frontend consumes it: <c>{ label, sign }</c>.</summary>
public sealed record Signal(string Label, string Sign);

/// <summary>
/// A signal with its kind and weight. Neither leaves through the API: they are used
/// for ordering, for classification, and to build the classifier's feature vector.
/// </summary>
public sealed record WeightedSignal(SignalKind Kind, string Label, int Weight)
{
    public Signal ToSignal() => new(Label, Weight switch
    {
        > 0 => "pos",
        < 0 => "neg",
        _ => "neutral",
    });
}

/// <summary>
/// What has to be known about the user and the history to evaluate the signals.
/// None of this comes from the message itself: it comes from the database and the
/// configured account.
/// </summary>
public sealed record SignalContext
{
    /// <summary>The user's own addresses, lowercase.</summary>
    public required IReadOnlySet<string> OwnAddresses { get; init; }

    /// <summary>Their organisation's domain, without the at sign. Null if unknown.</summary>
    public string? OrgDomain { get; init; }

    /// <summary>Messages received from this sender (sender_stats.received).</summary>
    public int SenderReceived { get; init; }

    /// <summary>Times the user has replied to them (sender_stats.replied).</summary>
    public int SenderReplied { get; init; }

    /// <summary>
    /// Times the user has opened mail from this sender (sender_stats.opened).
    /// This is what separates "automatizado" from "ruido": both are machines, but one
    /// gets read and the other does not.
    /// </summary>
    public int SenderOpened { get; init; }

    /// <summary>
    /// The user has a message of their own in this thread. The spec describes it as
    /// "your address in References", but References only carries Message-IDs; in
    /// practice it is resolved by checking whether any message in the thread came from
    /// the Sent folder.
    /// </summary>
    public bool IParticipatesInThread { get; init; }

    /// <summary>
    /// Consecutive messages from the sender in this thread with no reply from the user.
    /// Three or more is the "Third message with no reply from you" signal.
    /// </summary>
    public int ConsecutiveUnanswered { get; init; }

    /// <summary>Already-normalised body. Direct questions are looked for here, not in the HTML.</summary>
    public string CleanBody { get; init; } = string.Empty;
}

/// <summary>
/// The deterministic signals of BACKEND.md §6: no AI, microseconds, and brutally
/// predictive. Each one carries readable text because the visible explanation is what
/// makes a user trust the triage or correct it.
///
/// Weights come from the §6 table: +++ = 3, ++ = 2, + = 1, and the same for negatives.
/// </summary>
public static partial class DeterministicSignals
{
    private const int MaxSignals = 6;   // §6: at most 6 per message

    // "No requiere accion por vuestra parte", "solo para tu informacion", "FYI".
    // This is the only signal that gives POSITIVE evidence that a message is
    // informational, rather than reaching that bucket by elimination.
    [GeneratedRegex(@"(no requiere (ninguna )?(accion|acción)|no es necesario responder|no hace falta responder|s[oó]lo (para )?(tu|su|vuestra) informaci[oó]n|para que quede constancia|no action (is )?required|no reply (is )?needed|for your information|FYI)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoActionRequired();

    // Acknowledgement of receipt: "hemos recibido su solicitud". It states explicitly
    // that the ball is in the sender's court, not yours.
    [GeneratedRegex(@"(hemos recibido (su|tu)|su (solicitud|peticion|petición|mensaje) ha sido (registrad|recibid)|we have received your|your (request|ticket) has been (received|registered))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AcknowledgementOfReceipt();

    // The full-width question mark shows up in Asian mail.
    [GeneratedRegex(@"[?？]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex EndsWithQuestionMark();

    public static List<WeightedSignal> Compute(MessageFacts facts, SignalContext ctx)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(ctx);

        var signals = new List<WeightedSignal>(10);

        // --- Sender history: the single most predictive signal there is -----------

        if (ctx.SenderReplied > 0)
        {
            signals.Add(new(SignalKind.RepliedBefore, $"You have replied {ctx.SenderReplied} times", 3));
        }
        else if (ctx.SenderReceived == 0)
        {
            // Unknown sender: neither good nor bad evidence, just missing data. Hence
            // neutral, and it also lowers confidence in SignalBaseline.
            signals.Add(new(SignalKind.NewSender, "New sender, no history", 0));
        }
        else if (ctx.SenderReceived >= 5)
        {
            // With few messages received, "you never replied" says nothing: there may
            // simply never have been an occasion.
            signals.Add(new(SignalKind.NeverReplied, $"You have never replied (0 of {ctx.SenderReceived})", -2));
        }

        // --- How you were addressed ------------------------------------------------

        var inTo = facts.To.Any(ctx.OwnAddresses.Contains);
        var inCc = facts.Cc.Any(ctx.OwnAddresses.Contains);

        if (inTo && !inCc)
        {
            signals.Add(new(SignalKind.ToNotCc, "You are on To:, not Cc:", 2));
        }
        else if (inCc && !inTo)
        {
            signals.Add(new(SignalKind.CcNotTo, "You are on Cc:, not To:", -2));
        }

        // --- Thread state ----------------------------------------------------------

        if (ctx.IParticipatesInThread)
        {
            signals.Add(new(SignalKind.ThreadParticipant, "A thread you are already in", 2));
        }

        if (ctx.ConsecutiveUnanswered >= 3)
        {
            signals.Add(new(SignalKind.ThirdUnanswered, "Third message with no reply from you", 3));
        }

        // --- Content ---------------------------------------------------------------

        if (AsksAtTheEnd(ctx.CleanBody))
        {
            signals.Add(new(SignalKind.EndsWithQuestion, "Ends with a direct question", 1));
        }

        // Weight -2: pushes away from "responder" exactly as hard as "you are in To:"
        // pulls towards it. A message addressed to you that says no action is required
        // is a tie, and a tie has to come out with low confidence, not high.
        if (NoActionRequired().IsMatch(ctx.CleanBody))
        {
            signals.Add(new(SignalKind.ExplicitlyNoAction, "Says plainly: no action needed", -2));
        }

        if (AcknowledgementOfReceipt().IsMatch(ctx.CleanBody))
        {
            signals.Add(new(SignalKind.Acknowledgement, "Acknowledgement of receipt: no reply expected", -2));
        }

        if (ctx.OrgDomain is { Length: > 0 } domain &&
            facts.FromEmail.EndsWith("@" + domain, StringComparison.OrdinalIgnoreCase))
        {
            signals.Add(new(SignalKind.SameOrgDomain, "Same domain as your organisation", 1));
        }

        // --- Bulk and machine markers ----------------------------------------------

        if (facts.HasListUnsubscribe)
        {
            signals.Add(new(SignalKind.ListUnsubscribe, "List-Unsubscribe present", -3));
        }

        if (facts.IsNoReplyAddress)
        {
            signals.Add(new(SignalKind.NoReplyAddress, "A no-reply address", -2));
        }

        if (facts.IsAutoSubmitted)
        {
            signals.Add(new(SignalKind.AutoSubmitted, "Auto-Submitted / Precedence: bulk", -2));
        }

        // --- Authentication --------------------------------------------------------

        if (facts.SpfPass == true && facts.DkimPass == true)
        {
            signals.Add(new(SignalKind.Authenticated, "Verified sender (DKIM + SPF)", 1));
        }

        // §6: at most 6, ordered by descending absolute weight. OrderByDescending is
        // stable, so for equal weights insertion order wins, which is the order of the
        // table in the spec. Two runs produce the same result.
        return [.. signals.OrderByDescending(s => Math.Abs(s.Weight)).Take(MaxSignals)];
    }

    /// <summary>
    /// A direct question at the end of the message. Only the end is examined, not the
    /// whole body: a question in the first paragraph of a long message is usually
    /// rhetorical ("Did you know...?" from a newsletter).
    /// </summary>
    private static bool AsksAtTheEnd(string cleanBody)
    {
        if (string.IsNullOrWhiteSpace(cleanBody))
        {
            return false;
        }

        var lines = cleanBody.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // The last two lines: the final one is often a sign-off ("Un saludo").
        foreach (var line in lines.Reverse().Take(2))
        {
            if (EndsWithQuestionMark().IsMatch(line))
            {
                return true;
            }
        }

        return false;
    }
}
