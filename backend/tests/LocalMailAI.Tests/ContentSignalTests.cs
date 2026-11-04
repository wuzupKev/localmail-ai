using LocalMailAI.Api.Classification;
using LocalMailAI.Api.Mail;
using MimeKit;

namespace LocalMailAI.Tests;

/// <summary>
/// The two content signals that give "fyi" evidence OF ITS OWN, rather than
/// it being reached by elimination. They come from mock.ts (e6 and e15) but were
/// missing from the §6 table.
///
/// The sample bodies are Spanish because the signals match Spanish mail; the product's
/// users write in Spanish.
/// </summary>
public class ContentSignalTests
{
    private static readonly IReadOnlySet<string> Me =
        new HashSet<string> { "kevin@sagetasolutions.com" };

    [Theory]
    [InlineData("No requiere accion por vuestra parte, es solo para que quede constancia.")]
    [InlineData("No requiere acción por vuestra parte, es solo para que quede constancia.")]
    [InlineData("Adjunto el acta. Solo para tu informacion.")]
    [InlineData("FYI, adjunto el acta del comite.")]
    [InlineData("No action required on your side.")]
    public void Detects_that_the_message_says_no_action_is_required(string body)
    {
        var signals = Compute(body);

        Assert.Contains(signals, s => s.Kind == SignalKind.ExplicitlyNoAction);
    }

    [Theory]
    [InlineData("Hemos recibido su solicitud. Un agente la revisara en 48 horas.")]
    [InlineData("Su solicitud ha sido registrada correctamente.")]
    [InlineData("We have received your request.")]
    public void Detects_an_acknowledgement_of_receipt(string body)
    {
        var signals = Compute(body);

        Assert.Contains(signals, s => s.Kind == SignalKind.Acknowledgement);
    }

    [Theory]
    [InlineData("Necesito los datos del Q3 antes del viernes.")]
    [InlineData("Te confirmo la reunion del jueves a las 10.")]
    public void Does_not_invent_them_where_they_are_absent(string body)
    {
        var signals = Compute(body);

        Assert.DoesNotContain(signals, s => s.Kind == SignalKind.ExplicitlyNoAction);
        Assert.DoesNotContain(signals, s => s.Kind == SignalKind.Acknowledgement);
    }

    [Fact]
    public void The_committee_minutes_leave_the_doubt_bucket_thanks_to_the_signal()
    {
        // This is the case the evaluation flagged: the minutes sat at 0.49, below the
        // threshold, because their only signals were weak. With evidence of its own it
        // commits to an answer, and gets it right.
        var facts = Facts(
            """
            From: Marta Ruiz <marta.ruiz@universidadsur.edu>
            To: comite@universidadsur.edu
            Cc: kevin@sagetasolutions.com
            Subject: Acta de la reunion del comite
            Content-Type: text/plain; charset=utf-8

            Cuerpo
            """);

        var ctx = new SignalContext
        {
            OwnAddresses = Me,
            SenderReceived = 0,
            CleanBody = "Hola a todos,\n\nAdjunto el acta de la reunión del comité del 28 de octubre.\n\n"
                      + "No requiere acción por vuestra parte, es solo para que quede constancia.",
        };

        var signals = DeterministicSignals.Compute(facts, ctx);
        var c = SignalBaseline.Classify(signals, ctx);

        Assert.Contains(signals, s => s.Kind == SignalKind.ExplicitlyNoAction);
        Assert.Equal(Bucket.Fyi, c.Bucket);
        Assert.True(c.Confidence >= SignalBaseline.ConfidenceThreshold,
            $"confidence {c.Confidence} should reach the threshold {SignalBaseline.ConfidenceThreshold}");
    }

    private static List<WeightedSignal> Compute(string body)
    {
        var facts = Facts(
            """
            From: Alguien <alguien@ejemplo.com>
            To: kevin@sagetasolutions.com
            Subject: Asunto

            Cuerpo
            """);

        return DeterministicSignals.Compute(facts, new SignalContext
        {
            OwnAddresses = Me,
            SenderReceived = 10,
            CleanBody = body,
        });
    }

    private static MessageFacts Facts(string eml)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(eml.ReplaceLineEndings("\r\n"));
        using var ms = new MemoryStream(bytes);
        return MessageFacts.From(MimeMessage.Load(ms));
    }
}
