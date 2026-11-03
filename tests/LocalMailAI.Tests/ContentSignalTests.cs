using LocalMailAI.Api.Classification;
using LocalMailAI.Api.Mail;
using MimeKit;

namespace LocalMailAI.Tests;

/// <summary>
/// Las dos senales de contenido que dan evidencia PROPIA de "informativo", en vez de
/// llegar a ese cajon por descarte. Salen de mock.ts (e6 y e15) pero no estaban en la
/// tabla de §6.
/// </summary>
public class ContentSignalTests
{
    private static readonly IReadOnlySet<string> Yo =
        new HashSet<string> { "kevin@sagetasolutions.com" };

    [Theory]
    [InlineData("No requiere accion por vuestra parte, es solo para que quede constancia.")]
    [InlineData("No requiere acción por vuestra parte, es solo para que quede constancia.")]
    [InlineData("Adjunto el acta. Solo para tu informacion.")]
    [InlineData("FYI, adjunto el acta del comite.")]
    [InlineData("No action required on your side.")]
    public void Detecta_que_el_correo_dice_no_requerir_accion(string cuerpo)
    {
        var senales = Calcular(cuerpo);

        Assert.Contains(senales, s => s.Kind == SignalKind.ExplicitlyNoAction);
    }

    [Theory]
    [InlineData("Hemos recibido su solicitud. Un agente la revisara en 48 horas.")]
    [InlineData("Su solicitud ha sido registrada correctamente.")]
    [InlineData("We have received your request.")]
    public void Detecta_un_acuse_de_recibo(string cuerpo)
    {
        var senales = Calcular(cuerpo);

        Assert.Contains(senales, s => s.Kind == SignalKind.Acknowledgement);
    }

    [Theory]
    [InlineData("Necesito los datos del Q3 antes del viernes.")]
    [InlineData("Te confirmo la reunion del jueves a las 10.")]
    public void No_las_inventa_donde_no_estan(string cuerpo)
    {
        var senales = Calcular(cuerpo);

        Assert.DoesNotContain(senales, s => s.Kind == SignalKind.ExplicitlyNoAction);
        Assert.DoesNotContain(senales, s => s.Kind == SignalKind.Acknowledgement);
    }

    [Fact]
    public void El_acta_del_comite_sale_de_duda_gracias_a_la_senal()
    {
        // Este es el caso que la evaluacion senalaba: el acta se quedaba en 0.49, bajo
        // el umbral, porque sus unicas senales eran debiles. Con evidencia propia pasa
        // a pronunciarse, y acierta.
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
            OwnAddresses = Yo,
            SenderReceived = 0,
            CleanBody = "Hola a todos,\n\nAdjunto el acta de la reunión del comité del 28 de octubre.\n\n"
                      + "No requiere acción por vuestra parte, es solo para que quede constancia.",
        };

        var senales = DeterministicSignals.Compute(facts, ctx);
        var c = SignalBaseline.Classify(senales, ctx);

        Assert.Contains(senales, s => s.Kind == SignalKind.ExplicitlyNoAction);
        Assert.Equal(Bucket.Informativo, c.Bucket);
        Assert.True(c.Confidence >= SignalBaseline.ConfidenceThreshold,
            $"confianza {c.Confidence}, deberia llegar al umbral {SignalBaseline.ConfidenceThreshold}");
    }

    private static List<WeightedSignal> Calcular(string cuerpo)
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
            OwnAddresses = Yo,
            SenderReceived = 10,
            CleanBody = cuerpo,
        });
    }

    private static MessageFacts Facts(string eml)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(eml.ReplaceLineEndings("\r\n"));
        using var ms = new MemoryStream(bytes);
        return MessageFacts.From(MimeMessage.Load(ms));
    }
}
