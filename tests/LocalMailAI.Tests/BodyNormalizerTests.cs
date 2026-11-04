using LocalMailAI.Api.Mail;
using MimeKit;

namespace LocalMailAI.Tests;

/// <summary>
/// Body normalisation (BACKEND.md §5, stage 3).
///
/// The cases come from real clients: Gmail wraps the attribution line, Outlook embeds
/// a header block, Apple Mail appends "Enviado desde mi iPhone". Each test below is a
/// different client doing a different thing.
///
/// The sample text is Spanish because that is the mail the product normalises; the
/// rules match Spanish wording and translating the fixtures would stop testing them.
/// </summary>
public class BodyNormalizerTests
{
    // --- Quotes ----------------------------------------------------------------

    [Fact]
    public void Cuts_at_the_Spanish_attribution_line()
    {
        var r = BodyNormalizer.Normalize(
            """
            Hola Ana,

            Te confirmo el jueves a las 10.

            El 2 de noviembre de 2026, 9:14, Ana Molina <ana.molina@estudiolegal.es> escribió:
            > Te viene bien a las 10 en lugar de las 9?
            > Un saludo
            """);

        Assert.Equal("Hola Ana,\n\nTe confirmo el jueves a las 10.", r.Clean);
        Assert.True(r.QuotedLinesRemoved > 0);
    }

    [Fact]
    public void Cuts_even_when_the_client_wraps_the_attribution_across_lines()
    {
        // Gmail does exactly this when the name and address do not fit.
        var r = BodyNormalizer.Normalize(
            """
            Confirmado.

            El 2 de noviembre de 2026, 9:14, Ana Molina
            <ana.molina@estudiolegal.es> escribió:
            > Te viene bien a las 10?
            """);

        Assert.Equal("Confirmado.", r.Clean);
    }

    [Fact]
    public void Cuts_at_the_English_attribution_line()
    {
        var r = BodyNormalizer.Normalize(
            """
            Thanks, will do.

            On Mon, Nov 2, 2026 at 8:02 AM Pablo Esteban <pablo@clientemayor.com> wrote:
            >> Any news on the incident?
            """);

        Assert.Equal("Thanks, will do.", r.Clean);
    }

    [Fact]
    public void Cuts_at_the_original_message_separator()
    {
        var r = BodyNormalizer.Normalize(
            """
            Te paso lo que me llego.

            -----Mensaje original-----
            De: Diego Ferran
            Asunto: Propuesta

            Necesito los numeros del Q3.
            """);

        Assert.Equal("Te paso lo que me llego.", r.Clean);
    }

    [Fact]
    public void Cuts_at_the_header_block_Outlook_embeds()
    {
        var r = BodyNormalizer.Normalize(
            """
            Lo reviso y te digo.

            De: Lucia Herrera <lucia@herrera-asesoria.es>
            Enviado: lunes, 2 de noviembre de 2026 8:47
            Para: Kevin Chavarria
            Asunto: Documentacion trimestral

            Faltan los justificantes de octubre.
            """);

        Assert.Equal("Lo reviso y te digo.", r.Clean);
    }

    [Fact]
    public void Removes_quoted_lines_from_an_inline_reply()
    {
        // No attribution: the user replied between the other person's lines.
        var r = BodyNormalizer.Normalize(
            """
            > Puedes el jueves a las 10?
            Si, me vale.
            > Y el viernes?
            El viernes no.
            """);

        Assert.Equal("Si, me vale.\nEl viernes no.", r.Clean);
        Assert.Equal(2, r.QuotedLinesRemoved);
    }

    // --- Signatures --------------------------------------------------------------

    [Fact]
    public void Cuts_at_the_standard_delimiter()
    {
        var r = BodyNormalizer.Normalize(
            """
            Gracias,
            Diego

            --
            Diego Ferrán | Sageta Solutions
            Tel. +34 611 22 33 44 | d.ferran@sagetasolutions.com
            """);

        Assert.Equal("Gracias,\nDiego", r.Clean);
        Assert.True(r.SignatureLinesRemoved > 0);
    }

    [Fact]
    public void Cuts_a_trailing_contact_block_with_no_delimiter()
    {
        var r = BodyNormalizer.Normalize(
            """
            Necesito los datos de consumo del Q3 antes del viernes para cerrar la propuesta.

            Un saludo,
            Diego Ferrán
            Sageta Solutions
            Tel. +34 611 22 33 44
            d.ferran@sagetasolutions.com
            """);

        Assert.Contains("Necesito los datos de consumo del Q3", r.Clean);
        Assert.DoesNotContain("611 22 33 44", r.Clean);
        Assert.DoesNotContain("d.ferran@sagetasolutions.com", r.Clean);
    }

    [Fact]
    public void Cuts_the_mobile_signature()
    {
        var r = BodyNormalizer.Normalize(
            """
            Voy de camino, llego en veinte minutos.
            Lo vemos alli con el resto del equipo.

            Enviado desde mi iPhone
            """);

        Assert.Equal("Voy de camino, llego en veinte minutos.\nLo vemos alli con el resto del equipo.", r.Clean);
    }

    [Fact]
    public void Cuts_the_legal_disclaimer()
    {
        var r = BodyNormalizer.Normalize(
            """
            Adjunto la factura F-2291 con el vencimiento corregido.
            Si necesitas el desglose por lineas, dimelo.

            Aviso legal: este mensaje y sus anexos son confidenciales y van dirigidos
            unicamente a su destinatario.
            """);

        Assert.Contains("Adjunto la factura F-2291", r.Clean);
        Assert.DoesNotContain("confidenciales", r.Clean);
    }

    [Fact]
    public void Does_not_mistake_content_for_a_disclaimer_because_of_one_word()
    {
        // "confidencial" in the first paragraph is the subject of the message, not a
        // signature.
        var r = BodyNormalizer.Normalize(
            """
            El informe es confidencial, no lo reenvies fuera del equipo.
            Te lo paso en cuanto lo firme Lucia.
            Avisame si lo necesitas antes del jueves.
            """);

        Assert.Contains("El informe es confidencial", r.Clean);
    }

    [Fact]
    public void Does_not_eat_the_whole_message_when_the_message_is_a_contact_detail()
    {
        // Worst possible failure of the heuristic: leaving the body empty. When in
        // doubt, keep the text.
        var r = BodyNormalizer.Normalize("Mi telefono es +34 611 22 33 44, llamame cuando puedas.");

        Assert.Contains("611 22 33 44", r.Clean);
        Assert.Equal(0, r.SignatureLinesRemoved);
    }

    // --- HTML --------------------------------------------------------------------

    [Fact]
    public void Converts_html_to_text_and_decodes_entities()
    {
        var r = BodyNormalizer.Normalize(
            BodyNormalizer.HtmlToText(
                "<div><p>Hola <b>Kevin</b>,</p><p>&iquest;Te viene bien a las 10?</p></div>"),
            "html");

        Assert.Equal("Hola Kevin,\n\n¿Te viene bien a las 10?", r.Clean);
    }

    [Fact]
    public void The_Gmail_blockquote_is_treated_as_a_quote_and_disappears()
    {
        var text = BodyNormalizer.HtmlToText(
            """
            <div>Confirmado, nos vemos el jueves.</div>
            <blockquote class="gmail_quote">
              <div>Te viene bien a las 10 en lugar de las 9?</div>
              <div>Un saludo, Ana</div>
            </blockquote>
            """);

        var r = BodyNormalizer.Normalize(text, "html");

        Assert.Equal("Confirmado, nos vemos el jueves.", r.Clean);
        Assert.True(r.QuotedLinesRemoved > 0);
    }

    [Fact]
    public void Discards_script_and_style()
    {
        var text = BodyNormalizer.HtmlToText(
            "<style>.x{color:red}</style><p>Oferta del mes</p><script>alert(1)</script>");

        Assert.DoesNotContain("color:red", text);
        Assert.DoesNotContain("alert", text);
        Assert.Contains("Oferta del mes", text);
    }

    [Fact]
    public void A_br_separates_lines()
    {
        var r = BodyNormalizer.Normalize(
            BodyNormalizer.HtmlToText("Entregas cerradas: 7<br>Incidencias abiertas: 3<br/>Demo: 12 nov"),
            "html");

        Assert.Equal("Entregas cerradas: 7\nIncidencias abiertas: 3\nDemo: 12 nov", r.Clean);
    }

    // --- Invisible junk ------------------------------------------------------------

    [Fact]
    public void Unusual_spaces_become_ordinary_spaces()
    {
        var r = BodyNormalizer.Normalize("Importe: 348,00 € y nada​mas");

        Assert.Equal("Importe: 348,00 € y nadamas", r.Clean);
        Assert.DoesNotContain(' ', r.Clean);
        Assert.DoesNotContain('​', r.Clean);
    }

    [Fact]
    public void Composed_emoji_survive()
    {
        // The ZWJ (‍) is what holds the family emoji together. If it were
        // stripped as "invisible junk", the emoji would split into four separate
        // people.
        const string family = "\U0001f468‍\U0001f469‍\U0001f467";
        var r = BodyNormalizer.Normalize($"Enhorabuena {family} por el nacimiento");

        Assert.Contains(family, r.Clean);
    }

    [Fact]
    public void Repeated_blank_lines_are_collapsed()
    {
        var r = BodyNormalizer.Normalize("Primero\n\n\n\n\nSegundo");

        Assert.Equal("Primero\n\nSegundo", r.Clean);
        Assert.Equal(2, r.Paragraphs.Length);
    }

    // --- Preview and paragraphs ------------------------------------------------------

    [Fact]
    public void The_preview_cuts_on_a_word_and_stays_under_144()
    {
        var longText = string.Join(' ', Enumerable.Repeat("palabra", 60));
        var r = BodyNormalizer.Normalize(longText);

        Assert.True(r.Preview.Length <= 144, $"preview is {r.Preview.Length} characters");
        Assert.EndsWith("...", r.Preview);
        Assert.DoesNotContain("palabr...", r.Preview);  // does not cut mid-word
    }

    [Fact]
    public void The_preview_does_not_split_an_emoji()
    {
        // Filler that leaves an emoji right at the 140 mark.
        var r = BodyNormalizer.Normalize(new string('a', 139) + "\U0001f525" + new string('b', 50));

        Assert.False(char.IsHighSurrogate(r.Preview[^4]), "the preview ends in a lone surrogate");
    }

    [Fact]
    public void The_preview_is_a_single_line()
    {
        var r = BodyNormalizer.Normalize("Primera linea\n\nSegunda linea");

        Assert.DoesNotContain('\n', r.Preview);
        Assert.Equal("Primera linea Segunda linea", r.Preview);
    }

    // --- Whole MIME messages ---------------------------------------------------------

    [Fact]
    public void Prefers_text_plain_over_text_html()
    {
        var msg = Load(
            """
            From: Ana Molina <ana@estudiolegal.es>
            To: kevin@sageta.com
            Subject: Reunion
            Content-Type: multipart/alternative; boundary="b1"

            --b1
            Content-Type: text/plain; charset=utf-8

            Version en texto plano.
            --b1
            Content-Type: text/html; charset=utf-8

            <p>Version en HTML</p>
            --b1--
            """);

        var r = BodyNormalizer.FromMessage(msg);

        Assert.Equal("plain", r.SourceFormat);
        Assert.Equal("Version en texto plano.", r.Clean);
    }

    [Fact]
    public void Falls_back_to_html_when_there_is_no_plain_text()
    {
        var msg = Load(
            """
            From: Marketing Zentrix <newsletter@zentrix.io>
            To: kevin@sageta.com
            Subject: Oferta
            Content-Type: text/html; charset=utf-8

            <html><body><p>7 formas de multiplicar tu productividad</p></body></html>
            """);

        var r = BodyNormalizer.FromMessage(msg);

        Assert.Equal("html", r.SourceFormat);
        Assert.Equal("7 formas de multiplicar tu productividad", r.Clean);
    }

    [Fact]
    public void A_message_with_no_body_does_not_blow_up()
    {
        var msg = Load(
            """
            From: Vercel <no-reply@vercel.com>
            To: kevin@sageta.com
            Subject: Despliegue correcto
            Content-Type: text/plain; charset=utf-8


            """);

        var r = BodyNormalizer.FromMessage(msg);

        Assert.Equal(string.Empty, r.Clean);
        Assert.Empty(r.Paragraphs);
    }

    [Fact]
    public void A_lying_charset_is_tolerated_rather_than_fatal()
    {
        // §11: MimeKit copes with a lot, but broken mail exists. Never fail.
        var msg = Load(
            """
            From: Facturacion <facturacion@nordia-cloud.com>
            To: kevin@sageta.com
            Subject: Factura
            Content-Type: text/plain; charset=iso-8859-1

            FacturaciÃ³n pendiente
            """);

        var r = BodyNormalizer.FromMessage(msg);

        Assert.False(string.IsNullOrEmpty(r.Clean));
    }

    private static MimeMessage Load(string eml)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(eml.ReplaceLineEndings("\r\n"));
        using var ms = new MemoryStream(bytes);
        return MimeMessage.Load(ms);
    }
}
