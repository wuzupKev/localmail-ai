using LocalMailAI.Api.Mail;
using MimeKit;

namespace LocalMailAI.Tests;

/// <summary>
/// Normalizacion de cuerpos (BACKEND.md §5, etapa 3).
///
/// Los casos salen de clientes reales: Gmail parte la atribucion por el ancho,
/// Outlook incrusta un bloque de cabeceras, Apple Mail mete "Enviado desde mi
/// iPhone". Cada test de abajo es un cliente haciendo algo distinto.
/// </summary>
public class BodyNormalizerTests
{
    // --- Citas -----------------------------------------------------------------

    [Fact]
    public void Corta_en_la_atribucion_en_espanol()
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
    public void Corta_aunque_el_cliente_parta_la_atribucion_en_varias_lineas()
    {
        // Gmail hace exactamente esto cuando el nombre y la direccion no caben.
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
    public void Corta_en_la_atribucion_en_ingles()
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
    public void Corta_en_el_separador_de_mensaje_original()
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
    public void Corta_en_el_bloque_de_cabeceras_que_incrusta_Outlook()
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
    public void Quita_las_lineas_citadas_de_una_respuesta_intercalada()
    {
        // Sin atribucion: el usuario ha respondido entre las lineas del otro.
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

    // --- Firmas ----------------------------------------------------------------

    [Fact]
    public void Corta_en_el_delimitador_estandar()
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
    public void Corta_el_bloque_de_contacto_del_final_sin_delimitador()
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
    public void Corta_la_firma_del_movil()
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
    public void Corta_el_descargo_legal()
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
    public void No_confunde_contenido_con_descargo_por_la_palabra_confidencial()
    {
        // "confidencial" en el primer parrafo es el asunto del correo, no una firma.
        var r = BodyNormalizer.Normalize(
            """
            El informe es confidencial, no lo reenvies fuera del equipo.
            Te lo paso en cuanto lo firme Lucia.
            Avisame si lo necesitas antes del jueves.
            """);

        Assert.Contains("El informe es confidencial", r.Clean);
    }

    [Fact]
    public void No_se_come_el_mensaje_entero_cuando_el_mensaje_es_un_dato_de_contacto()
    {
        // Peor fallo posible del heuristico: dejar el cuerpo vacio. Ante la duda, conservar.
        var r = BodyNormalizer.Normalize("Mi telefono es +34 611 22 33 44, llamame cuando puedas.");

        Assert.Contains("611 22 33 44", r.Clean);
        Assert.Equal(0, r.SignatureLinesRemoved);
    }

    // --- HTML ------------------------------------------------------------------

    [Fact]
    public void Convierte_html_a_texto_y_decodifica_entidades()
    {
        var r = BodyNormalizer.Normalize(
            BodyNormalizer.HtmlToText(
                "<div><p>Hola <b>Kevin</b>,</p><p>&iquest;Te viene bien a las 10?</p></div>"),
            "html");

        Assert.Equal("Hola Kevin,\n\n¿Te viene bien a las 10?", r.Clean);
    }

    [Fact]
    public void El_blockquote_de_gmail_se_trata_como_cita_y_desaparece()
    {
        var texto = BodyNormalizer.HtmlToText(
            """
            <div>Confirmado, nos vemos el jueves.</div>
            <blockquote class="gmail_quote">
              <div>Te viene bien a las 10 en lugar de las 9?</div>
              <div>Un saludo, Ana</div>
            </blockquote>
            """);

        var r = BodyNormalizer.Normalize(texto, "html");

        Assert.Equal("Confirmado, nos vemos el jueves.", r.Clean);
        Assert.True(r.QuotedLinesRemoved > 0);
    }

    [Fact]
    public void Descarta_script_y_style()
    {
        var texto = BodyNormalizer.HtmlToText(
            "<style>.x{color:red}</style><p>Oferta del mes</p><script>alert(1)</script>");

        Assert.DoesNotContain("color:red", texto);
        Assert.DoesNotContain("alert", texto);
        Assert.Contains("Oferta del mes", texto);
    }

    [Fact]
    public void El_br_separa_lineas()
    {
        var r = BodyNormalizer.Normalize(
            BodyNormalizer.HtmlToText("Entregas cerradas: 7<br>Incidencias abiertas: 3<br/>Demo: 12 nov"),
            "html");

        Assert.Equal("Entregas cerradas: 7\nIncidencias abiertas: 3\nDemo: 12 nov", r.Clean);
    }

    // --- Basura invisible ------------------------------------------------------

    [Fact]
    public void Los_espacios_raros_pasan_a_espacio_normal()
    {
        var r = BodyNormalizer.Normalize("Importe: 348,00 € y nada​mas");

        Assert.Equal("Importe: 348,00 € y nadamas", r.Clean);
        Assert.DoesNotContain(' ', r.Clean);
        Assert.DoesNotContain('​', r.Clean);
    }

    [Fact]
    public void Conserva_los_emoji_compuestos()
    {
        // El ZWJ (‍) mantiene unido el emoji de familia. Si se limpiara como
        // "invisible que estorba", el emoji se partiria en cuatro personas sueltas.
        const string familia = "\U0001f468‍\U0001f469‍\U0001f467";
        var r = BodyNormalizer.Normalize($"Enhorabuena {familia} por el nacimiento");

        Assert.Contains(familia, r.Clean);
    }

    [Fact]
    public void Colapsa_lineas_vacias_repetidas()
    {
        var r = BodyNormalizer.Normalize("Primero\n\n\n\n\nSegundo");

        Assert.Equal("Primero\n\nSegundo", r.Clean);
        Assert.Equal(2, r.Paragraphs.Length);
    }

    // --- Preview y parrafos ----------------------------------------------------

    [Fact]
    public void El_preview_se_corta_por_palabra_y_no_pasa_de_144()
    {
        var largo = string.Join(' ', Enumerable.Repeat("palabra", 60));
        var r = BodyNormalizer.Normalize(largo);

        Assert.True(r.Preview.Length <= 144, $"preview de {r.Preview.Length} caracteres");
        Assert.EndsWith("...", r.Preview);
        Assert.DoesNotContain("palabr...", r.Preview);  // no corta a mitad de palabra
    }

    [Fact]
    public void El_preview_no_parte_un_emoji_por_la_mitad()
    {
        // Relleno que deja un emoji justo en el limite de los 140.
        var r = BodyNormalizer.Normalize(new string('a', 139) + "\U0001f525" + new string('b', 50));

        Assert.False(char.IsHighSurrogate(r.Preview[^4]), "el preview termina en un subrogado suelto");
    }

    [Fact]
    public void El_preview_va_en_una_sola_linea()
    {
        var r = BodyNormalizer.Normalize("Primera linea\n\nSegunda linea");

        Assert.DoesNotContain('\n', r.Preview);
        Assert.Equal("Primera linea Segunda linea", r.Preview);
    }

    // --- Mensajes MIME completos -----------------------------------------------

    [Fact]
    public void Prefiere_text_plain_sobre_text_html()
    {
        var msg = Cargar(
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
    public void Usa_el_html_cuando_no_hay_texto_plano()
    {
        var msg = Cargar(
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
    public void Un_mensaje_sin_cuerpo_no_revienta()
    {
        var msg = Cargar(
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
    public void Un_charset_mentiroso_se_registra_pero_no_falla()
    {
        // §11: MimeKit aguanta bastante, pero hay correo roto. No fallar nunca.
        var msg = Cargar(
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

    private static MimeMessage Cargar(string eml)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(eml.ReplaceLineEndings("\r\n"));
        using var ms = new MemoryStream(bytes);
        return MimeMessage.Load(ms);
    }
}
