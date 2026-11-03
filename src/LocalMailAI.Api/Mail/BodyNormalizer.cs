using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using MimeKit;

namespace LocalMailAI.Api.Mail;

/// <summary>Resultado de normalizar un cuerpo de correo (BACKEND.md §5, etapa 3).</summary>
/// <param name="Clean">Texto limpio: sin HTML, sin citas, sin firma.</param>
/// <param name="Preview">140 caracteres del texto limpio, en una linea.</param>
/// <param name="Paragraphs">Parrafos, que es la forma que espera Email.body del front.</param>
/// <param name="QuotedLinesRemoved">Lineas de cita descartadas.</param>
/// <param name="SignatureLinesRemoved">Lineas de firma descartadas.</param>
/// <param name="SourceFormat">"plain", "html" o "none".</param>
public sealed record NormalizedBody(
    string Clean,
    string Preview,
    string[] Paragraphs,
    int QuotedLinesRemoved,
    int SignatureLinesRemoved,
    string SourceFormat);

/// <summary>
/// Normalizacion de cuerpos de correo: HTML a texto, fuera citas, fuera firmas.
///
/// Es la etapa que mas afecta a la calidad final (BACKEND.md §5): recortar citas y
/// firmas quita entre el 50 y el 70% de los tokens de entrada, y ademas evita que el
/// clasificador aprenda de texto que el remitente no escribio.
///
/// Las librerias de esto estan semi-abandonadas en todos los lenguajes (§11), asi que
/// las reglas son propias. Criterio al escribirlas: ante la duda, conservar texto. Un
/// parrafo de cita que se cuela estorba; un parrafo real que se borra es informacion
/// perdida que nadie va a echar en falta porque no se ve.
/// </summary>
public static partial class BodyNormalizer
{
    private const int PreviewLength = 140;

    // --- Atribuciones de cita: "El X escribio:", "On X wrote:", cabeceras de Outlook ---
    // Se evaluan sobre hasta 3 lineas unidas, porque los clientes las parten.

    [GeneratedRegex(@"^\s*-{2,}\s*(Original Message|Mensaje original|Forwarded message|Mensaje reenviado)\s*-{2,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeparadorOriginal();

    [GeneratedRegex(@"^\s*_{5,}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SeparadorOutlook();

    // "El 2 de noviembre de 2026, 9:14, Ana <a@x.es> escribio:" y sus variantes por idioma.
    [GeneratedRegex(@"^\s*(El|On|Am|Le|A|Il)\b.{0,200}?\b(escribi(o|ó)|wrote|schrieb|a écrit|ha scritto|escreveu)\s*:\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex AtribucionConVerbo();

    // Bloque de cabeceras que Outlook incrusta: "De: ... Enviado: ... Para: ..."
    [GeneratedRegex(@"^\s*(De|From|Von|Da)\s*:\s*.{1,200}?(Enviado|Sent|Gesendet|Inviato|Fecha|Date)\s*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex CabeceraIncrustada();

    // --- Firmas ---

    [GeneratedRegex(@"^\s*(Sent from my .{0,40}|Enviado desde mi .{0,40}|Obtener Outlook para .{0,20}|Get Outlook for .{0,20})\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FirmaMovil();

    [GeneratedRegex(@"(aviso legal|confidencial|confidential|privileged|clausula de privacidad|proteccion de datos|datos personales.{0,40}LOPD|GDPR|RGPD)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Descargo();

    [GeneratedRegex(@"(\+?\d[\d\s().-]{7,}\d)", RegexOptions.CultureInvariant)]
    private static partial Regex Telefono();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.CultureInvariant)]
    private static partial Regex Correo();

    [GeneratedRegex(@"\b(https?://|www\.)\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Enlace();

    [GeneratedRegex(@"^\s*(Tel\.?|Telf\.?|Tfno\.?|M(o|ó)vil|Mobile|Phone|Fax|Linkedin|Twitter|Skype)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EtiquetaContacto();

    [GeneratedRegex(@"[ \t\u00a0\u2007\u2009\u202f]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EspaciosSeguidos();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex LineasVaciasSeguidas();

    // ---------------------------------------------------------------------------

    /// <summary>Normaliza el cuerpo de un mensaje MIME, prefiriendo text/plain.</summary>
    public static NormalizedBody FromMessage(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // text/plain primero: ya viene sin markup y es lo que el remitente escribio.
        var plano = message.TextBody;
        if (!string.IsNullOrWhiteSpace(plano))
        {
            return Normalize(plano, "plain");
        }

        var html = message.HtmlBody;
        if (!string.IsNullOrWhiteSpace(html))
        {
            return Normalize(HtmlToText(html), "html");
        }

        // Sin text/plain ni text/html: queda rebuscar cualquier parte de texto
        // (hay correos con un unico text/enriched o con el cuerpo en un adjunto).
        foreach (var parte in message.BodyParts.OfType<TextPart>())
        {
            var texto = parte.IsHtml ? HtmlToText(parte.Text) : parte.Text;
            if (!string.IsNullOrWhiteSpace(texto))
            {
                return Normalize(texto, parte.IsHtml ? "html" : "plain");
            }
        }

        return new NormalizedBody(string.Empty, string.Empty, [], 0, 0, "none");
    }

    /// <summary>
    /// HTML a texto. Los elementos de bloque pasan a salto de linea y los
    /// &lt;blockquote&gt; salen con "&gt; " delante: asi el quitador de citas trata igual
    /// el correo HTML y el de texto plano, en vez de tener dos caminos que divergen.
    /// </summary>
    public static string HtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var doc = new HtmlParser().ParseDocument(html);

        foreach (var nodo in doc.QuerySelectorAll("script, style, head, noscript, template"))
        {
            nodo.Remove();
        }

        var sb = new StringBuilder();
        Recorrer(doc.Body ?? doc.DocumentElement, sb);

        return sb.ToString();
    }

    private static void Recorrer(INode? nodo, StringBuilder sb)
    {
        if (nodo is null)
        {
            return;
        }

        switch (nodo)
        {
            case IText t:
                var valor = t.Data;
                if (!string.IsNullOrEmpty(valor))
                {
                    sb.Append(valor);
                }
                return;

            case IElement el:
                var etiqueta = el.LocalName;

                if (etiqueta is "br")
                {
                    sb.Append('\n');
                    return;
                }

                if (etiqueta is "hr")
                {
                    sb.Append("\n\n");
                    return;
                }

                // Un <blockquote> (la cita de Gmail y compania) se renderiza aparte y
                // se prefija linea a linea cuando ya estan formadas. Prefijar mientras
                // se recorre no funciona: el primer hijo de bloque mete su propio
                // salto de linea y deja el "> " huerfano en la linea anterior.
                if (etiqueta is "blockquote")
                {
                    var interno = new StringBuilder();
                    foreach (var hijo in el.ChildNodes)
                    {
                        Recorrer(hijo, interno);
                    }

                    sb.Append('\n');
                    foreach (var linea in interno.ToString().Split('\n'))
                    {
                        if (linea.Trim().Length == 0)
                        {
                            continue;
                        }
                        sb.Append("> ").Append(linea.Trim()).Append('\n');
                    }
                    return;
                }

                var esBloque = etiqueta is "p" or "div" or "li" or "tr" or "table"
                    or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "ul" or "ol"
                    or "pre" or "section" or "article" or "header" or "footer";

                if (esBloque)
                {
                    sb.Append('\n');
                }

                foreach (var hijo in el.ChildNodes)
                {
                    Recorrer(hijo, sb);
                }

                if (etiqueta is "td" or "th")
                {
                    sb.Append('\t');
                }

                if (esBloque)
                {
                    sb.Append('\n');
                }
                return;

            default:
                foreach (var hijo in nodo.ChildNodes)
                {
                    Recorrer(hijo, sb);
                }
                return;
        }
    }

    /// <summary>Normaliza texto ya plano.</summary>
    public static NormalizedBody Normalize(string texto, string sourceFormat = "plain")
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            return new NormalizedBody(string.Empty, string.Empty, [], 0, 0, sourceFormat);
        }

        var lineas = Preparar(texto);

        var (sinCitas, citasFuera) = QuitarCitas(lineas);
        var (sinFirma, firmaFuera) = QuitarFirma(sinCitas);

        var limpio = Colapsar(string.Join('\n', sinFirma));
        var parrafos = limpio
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        return new NormalizedBody(limpio, Previsualizar(limpio), parrafos, citasFuera, firmaFuera, sourceFormat);
    }

    /// <summary>Saltos de linea unificados y basura invisible fuera.</summary>
    private static List<string> Preparar(string texto)
    {
        var sb = new StringBuilder(texto.Length);

        foreach (var c in texto.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            switch (c)
            {
                // Espacios que no son el espacio normal: pasan a espacio para que
                // las expresiones regulares y el recorte funcionen.
                case '\u00a0' or '\u2007' or '\u2009' or '\u202f' or '\u2002' or '\u2003':
                    sb.Append(' ');
                    break;

                // Invisibles que solo estorban. Ojo: ‍ (ZWJ) NO entra aqui,
                // porque es lo que mantiene unidos los emoji compuestos.
                case '\u200b' or '\u200c' or '\ufeff' or '\u00ad':
                    break;

                default:
                    sb.Append(c);
                    break;
            }
        }

        return [.. sb.ToString().Split('\n').Select(l => l.TrimEnd())];
    }

    /// <summary>
    /// Corta en la primera atribucion de cita y descarta las lineas con "&gt;".
    /// Todo lo que va despues de "El X escribio:" es el hilo anterior, no texto nuevo.
    /// </summary>
    private static (List<string> Lineas, int Quitadas) QuitarCitas(List<string> lineas)
    {
        var corte = -1;

        for (var i = 0; i < lineas.Count; i++)
        {
            if (SeparadorOriginal().IsMatch(lineas[i]) || SeparadorOutlook().IsMatch(lineas[i]))
            {
                corte = i;
                break;
            }

            // Ventanas de 1, 2 y 3 lineas unidas: los clientes parten la atribucion
            // por el ancho, pero el patron termina en ":" y ese ancla solo cae donde
            // la ventana acaba justo ahi. Probar solo la de 3 no casaria nunca con
            // una atribucion que ocupa una sola linea.
            var encontrada = false;
            for (var k = 1; k <= 3 && i + k <= lineas.Count; k++)
            {
                var ventana = string.Join(' ', lineas.GetRange(i, k)).Trim();
                if (ventana.Length > 0 &&
                    (AtribucionConVerbo().IsMatch(ventana) || CabeceraIncrustada().IsMatch(ventana)))
                {
                    encontrada = true;
                    break;
                }
            }

            if (encontrada)
            {
                corte = i;
                break;
            }
        }

        var cuerpo = corte >= 0 ? lineas.Take(corte).ToList() : lineas;
        var quitadas = corte >= 0 ? lineas.Count - corte : 0;

        // Lineas citadas sueltas (respuesta intercalada, o un reenvio sin atribucion).
        var resultado = new List<string>(cuerpo.Count);
        foreach (var linea in cuerpo)
        {
            if (linea.TrimStart().StartsWith('>'))
            {
                quitadas++;
                continue;
            }
            resultado.Add(linea);
        }

        return (resultado, quitadas);
    }

    /// <summary>
    /// Quita la firma. Por orden de fiabilidad: el delimitador estandar "-- " de la
    /// RFC 3676, los marcadores explicitos, el descargo legal, y como ultimo recurso
    /// un bloque de contacto al final. El ultimo es el unico heuristico de verdad, y
    /// por eso exige varias senales antes de borrar nada.
    /// </summary>
    private static (List<string> Lineas, int Quitadas) QuitarFirma(List<string> lineas)
    {
        if (lineas.Count == 0)
        {
            return (lineas, 0);
        }

        // 1. Delimitador estandar.
        for (var i = 0; i < lineas.Count; i++)
        {
            if (lineas[i].TrimEnd() == "--" || lineas[i] == "-- ")
            {
                return (lineas.Take(i).ToList(), lineas.Count - i);
            }
        }

        // 2. Marcadores explicitos y descargos legales, solo en el ultimo tercio:
        // la palabra "confidencial" en el primer parrafo es contenido, no firma.
        var desdeTercio = Math.Max(1, lineas.Count * 2 / 3);
        for (var i = desdeTercio; i < lineas.Count; i++)
        {
            if (FirmaMovil().IsMatch(lineas[i]) || Descargo().IsMatch(lineas[i]))
            {
                return (lineas.Take(i).ToList(), lineas.Count - i);
            }
        }

        // 3. Bloque de contacto al final: hasta 8 lineas cortas con al menos dos
        // senales de contacto (telefono, correo, enlace, etiqueta tipo "Tel.").
        var inicio = -1;
        var senales = 0;
        var limite = Math.Max(0, lineas.Count - 8);

        for (var i = lineas.Count - 1; i >= limite; i--)
        {
            var linea = lineas[i];

            if (string.IsNullOrWhiteSpace(linea))
            {
                continue;
            }

            // Una linea larga es prosa, no una linea de firma.
            if (linea.Length > 70)
            {
                break;
            }

            var tiene = (Telefono().IsMatch(linea) ? 1 : 0)
                + (Correo().IsMatch(linea) ? 1 : 0)
                + (Enlace().IsMatch(linea) ? 1 : 0)
                + (EtiquetaContacto().IsMatch(linea) ? 1 : 0);

            if (tiene > 0)
            {
                senales += tiene;
                inicio = i;
                continue;
            }

            // Linea corta sin senales: puede ser el nombre que encabeza la firma,
            // pero solo la aceptamos si ya hemos visto senales por debajo.
            if (inicio >= 0 && linea.Length <= 40)
            {
                inicio = i;
                continue;
            }

            break;
        }

        // Exigir dos senales y no quedarnos sin mensaje: si la firma empezara en la
        // primera linea, lo que tenemos no es una firma, es el correo entero.
        if (inicio > 0 && senales >= 2)
        {
            return (lineas.Take(inicio).ToList(), lineas.Count - inicio);
        }

        return (lineas, 0);
    }

    private static string Colapsar(string texto)
    {
        var sinEspacios = EspaciosSeguidos().Replace(texto, " ");
        var lineas = sinEspacios.Split('\n').Select(l => l.Trim());
        var unido = string.Join('\n', lineas);
        return LineasVaciasSeguidas().Replace(unido, "\n\n").Trim();
    }

    private static string Previsualizar(string limpio)
    {
        if (limpio.Length == 0)
        {
            return string.Empty;
        }

        var unaLinea = EspaciosSeguidos().Replace(limpio.Replace('\n', ' '), " ").Trim();

        if (unaLinea.Length <= PreviewLength)
        {
            return unaLinea;
        }

        // Cortar por palabra, no por carácter, y sin partir un par subrogado
        // (un emoji al filo de los 140 saldria como un rombo con interrogacion).
        var corte = unaLinea.LastIndexOf(' ', Math.Min(PreviewLength, unaLinea.Length - 1));
        if (corte < PreviewLength / 2)
        {
            corte = PreviewLength;
            while (corte > 0 && char.IsHighSurrogate(unaLinea[corte - 1]))
            {
                corte--;
            }
        }

        return string.Concat(unaLinea.AsSpan(0, corte).TrimEnd(), "...");
    }
}
