namespace LocalMailAI.Api.Classification;

/// <summary>Result of a classification: what ends up in the <c>classifications</c> table.</summary>
public sealed record Classification(Bucket Bucket, double Confidence, IReadOnlyList<Signal> Signals);

/// <summary>
/// Provisional classifier built from the deterministic signals.
///
/// It serves two distinct purposes and both matter:
///
/// 1. During ingestion (BACKEND.md §5, stage 4) it provides a classification available
///    NOW, before embeddings are computed. The user sees a sorted inbox from the first
///    synchronised message, not when the whole pipeline finishes.
/// 2. It is the BASELINE for milestone 5. Without it there is no way to know whether
///    the trained classifier adds anything, and we would be optimising blind.
///
/// There are no real probabilities here: <c>Confidence</c> is a monotonic function of
/// signal strength, calibrated so that weak evidence falls below UMBRAL_CONFIANZA
/// (0.55) and the frontend routes it to "not sure" instead of guessing. The milestone 6
/// classifier replaces this with real probabilities.
/// </summary>
public static class SignalBaseline
{
    /// <summary>Same value as UMBRAL_CONFIANZA in src/lib/mock.ts.</summary>
    public const double ConfidenceThreshold = 0.55;

    public static Classification Classify(IReadOnlyList<WeightedSignal> signals, SignalContext ctx)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(ctx);

        var score = signals.Sum(s => s.Weight);
        var publicSignals = signals.Select(s => s.ToSignal()).ToList();

        var isMachine = signals.Any(s => s.Kind
            is SignalKind.ListUnsubscribe
            or SignalKind.NoReplyAddress
            or SignalKind.AutoSubmitted);

        // Signals that only appear when the message is aimed at this person
        // specifically. A GitHub notification carries machine markers and still needs
        // action, so personal evidence outranks machine evidence.
        var isPersonal = signals.Any(s => s.Kind
            is SignalKind.RepliedBefore
            or SignalKind.ThreadParticipant
            or SignalKind.ThirdUnanswered);

        Bucket bucket;

        if (isMachine && !isPersonal)
        {
            // "automated" and "noise" are both machines. What separates them is not
            // the headers - both carry the same ones - but whether the user opens
            // them: the deploy notice gets read, the promotion does not. That is why
            // sender_stats.opened is needed.
            var neverOpened = ctx.SenderReceived >= 5 && ctx.SenderOpened == 0;
            bucket = neverOpened ? Bucket.Noise : Bucket.Automated;
        }
        else if (score >= 2)
        {
            bucket = Bucket.Reply;
        }
        else
        {
            bucket = Bucket.Fyi;
        }

        return new Classification(bucket, Confidence(score, ctx), publicSignals);
    }

    /// <summary>
    /// Confidence in [0.30, 0.95]. It grows with signal strength and drops when there
    /// is no history for the sender.
    ///
    /// A new sender SUBTRACTS; it does not impose a ceiling below the threshold. The
    /// difference matters on day one, when every sender is new: with a ceiling, the
    /// entire inbox would fall into "not sure" and the other four buckets would read as
    /// zero - which is exactly what §6 says should not happen, since the deterministic
    /// signals "work from minute zero". A newsletter from an unknown sender is still
    /// obviously a newsletter: List-Unsubscribe, Precedence: bulk and a no-reply
    /// address are conclusive without any history.
    ///
    /// What still falls below the threshold is the case that should: a new sender with
    /// weak signals. A payment reminder from an unknown supplier comes out at 0.49,
    /// which is what mock.ts describes for that same message.
    /// </summary>
    private static double Confidence(int score, SignalContext ctx)
    {
        var strength = Math.Min(Math.Abs(score), 8);
        var confidence = 0.5 + 0.055 * strength;

        if (ctx.SenderReceived == 0)
        {
            confidence -= 0.12;
        }

        return Math.Round(Math.Clamp(confidence, 0.30, 0.95), 2);
    }
}
