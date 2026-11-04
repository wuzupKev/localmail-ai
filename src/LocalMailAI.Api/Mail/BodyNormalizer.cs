using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using MimeKit;

namespace LocalMailAI.Api.Mail;

/// <summary>Result of normalising a message body (BACKEND.md §5, stage 3).</summary>
/// <param name="Clean">Clean text: no HTML, no quotes, no signature.</param>
/// <param name="Preview">140 characters of the clean text, on one line.</param>
/// <param name="Paragraphs">Paragraphs, which is the shape Email.body expects.</param>
/// <param name="QuotedLinesRemoved">Quoted lines discarded.</param>
/// <param name="SignatureLinesRemoved">Signature lines discarded.</param>
/// <param name="SourceFormat">"plain", "html" or "none".</param>
public sealed record NormalizedBody(
    string Clean,
    string Preview,
    string[] Paragraphs,
    int QuotedLinesRemoved,
    int SignatureLinesRemoved,
    string SourceFormat);

/// <summary>
/// Message body normalisation: HTML to text, quotes out, signatures out.
///
/// This is the stage that most affects final quality (BACKEND.md §5): stripping quotes
/// and signatures removes 50-70% of the input tokens, and it stops the classifier
/// learning from text the sender did not write.
///
/// The libraries for this are semi-abandoned in every language (§11), so the rules are
/// our own. The principle while writing them: when in doubt, keep the text. A quoted
/// paragraph that slips through is noise; a real paragraph that gets deleted is
/// information lost, and nobody will notice because it is not visible.
/// </summary>
public static partial class BodyNormalizer
{
    private const int PreviewLength = 140;

    // --- Quote attributions: "El X escribio:", "On X wrote:", Outlook header blocks ---
    // Evaluated over windows of up to 3 joined lines, because clients wrap them.

    [GeneratedRegex(@"^\s*-{2,}\s*(Original Message|Mensaje original|Forwarded message|Mensaje reenviado)\s*-{2,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OriginalMessageSeparator();

    [GeneratedRegex(@"^\s*_{5,}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex OutlookSeparator();

    // "El 2 de noviembre de 2026, 9:14, Ana <a@x.es> escribio:" and its variants.
    [GeneratedRegex(@"^\s*(El|On|Am|Le|A|Il)\b.{0,200}?\b(escribi(o|ó)|wrote|schrieb|a écrit|ha scritto|escreveu)\s*:\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex AttributionWithVerb();

    // The header block Outlook embeds: "De: ... Enviado: ... Para: ..."
    [GeneratedRegex(@"^\s*(De|From|Von|Da)\s*:\s*.{1,200}?(Enviado|Sent|Gesendet|Inviato|Fecha|Date)\s*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex EmbeddedHeaderBlock();

    // --- Signatures ---

    [GeneratedRegex(@"^\s*(Sent from my .{0,40}|Enviado desde mi .{0,40}|Obtener Outlook para .{0,20}|Get Outlook for .{0,20})\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MobileSignature();

    [GeneratedRegex(@"(aviso legal|confidencial|confidential|privileged|clausula de privacidad|proteccion de datos|datos personales.{0,40}LOPD|GDPR|RGPD)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegalDisclaimer();

    [GeneratedRegex(@"(\+?\d[\d\s().-]{7,}\d)", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneNumber();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.CultureInvariant)]
    private static partial Regex EmailAddress();

    [GeneratedRegex(@"\b(https?://|www\.)\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    [GeneratedRegex(@"^\s*(Tel\.?|Telf\.?|Tfno\.?|M(o|ó)vil|Mobile|Phone|Fax|Linkedin|Twitter|Skype)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContactLabel();

    [GeneratedRegex(@"[ \t    ]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedSpaces();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedBlankLines();

    // ---------------------------------------------------------------------------

    /// <summary>Normalises a MIME message body, preferring text/plain.</summary>
    public static NormalizedBody FromMessage(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // text/plain first: it arrives without markup and it is what the sender wrote.
        var plain = message.TextBody;
        if (!string.IsNullOrWhiteSpace(plain))
        {
            return Normalize(plain, "plain");
        }

        var html = message.HtmlBody;
        if (!string.IsNullOrWhiteSpace(html))
        {
            return Normalize(HtmlToText(html), "html");
        }

        // Neither text/plain nor text/html: fall back to any text part at all. Some
        // messages carry a single text/enriched part, or the body as an attachment.
        foreach (var part in message.BodyParts.OfType<TextPart>())
        {
            var text = part.IsHtml ? HtmlToText(part.Text) : part.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                return Normalize(text, part.IsHtml ? "html" : "plain");
            }
        }

        return new NormalizedBody(string.Empty, string.Empty, [], 0, 0, "none");
    }

    /// <summary>
    /// HTML to text. Block elements become line breaks and &lt;blockquote&gt; content
    /// comes out prefixed with "&gt; ", so the quote stripper handles HTML and plain
    /// text mail identically instead of having two paths that drift apart.
    /// </summary>
    public static string HtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var doc = new HtmlParser().ParseDocument(html);

        foreach (var node in doc.QuerySelectorAll("script, style, head, noscript, template"))
        {
            node.Remove();
        }

        var sb = new StringBuilder();
        Walk(doc.Body ?? doc.DocumentElement, sb);

        return sb.ToString();
    }

    private static void Walk(INode? node, StringBuilder sb)
    {
        if (node is null)
        {
            return;
        }

        switch (node)
        {
            case IText t:
                var value = t.Data;
                if (!string.IsNullOrEmpty(value))
                {
                    sb.Append(value);
                }
                return;

            case IElement el:
                var tag = el.LocalName;

                if (tag is "br")
                {
                    sb.Append('\n');
                    return;
                }

                if (tag is "hr")
                {
                    sb.Append("\n\n");
                    return;
                }

                // A <blockquote> (the Gmail-style quote and friends) is rendered
                // separately and prefixed line by line once the lines exist. Prefixing
                // while walking does not work: the first block child emits its own
                // line break and leaves the "> " orphaned on the previous line.
                if (tag is "blockquote")
                {
                    var inner = new StringBuilder();
                    foreach (var child in el.ChildNodes)
                    {
                        Walk(child, inner);
                    }

                    sb.Append('\n');
                    foreach (var line in inner.ToString().Split('\n'))
                    {
                        if (line.Trim().Length == 0)
                        {
                            continue;
                        }
                        sb.Append("> ").Append(line.Trim()).Append('\n');
                    }
                    return;
                }

                var isBlock = tag is "p" or "div" or "li" or "tr" or "table"
                    or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "ul" or "ol"
                    or "pre" or "section" or "article" or "header" or "footer";

                if (isBlock)
                {
                    sb.Append('\n');
                }

                foreach (var child in el.ChildNodes)
                {
                    Walk(child, sb);
                }

                if (tag is "td" or "th")
                {
                    sb.Append('\t');
                }

                if (isBlock)
                {
                    sb.Append('\n');
                }
                return;

            default:
                foreach (var child in node.ChildNodes)
                {
                    Walk(child, sb);
                }
                return;
        }
    }

    /// <summary>Normalises already-plain text.</summary>
    public static NormalizedBody Normalize(string text, string sourceFormat = "plain")
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new NormalizedBody(string.Empty, string.Empty, [], 0, 0, sourceFormat);
        }

        var lines = Prepare(text);

        var (withoutQuotes, quotesRemoved) = StripQuotes(lines);
        var (withoutSignature, signatureRemoved) = StripSignature(withoutQuotes);

        var clean = Collapse(string.Join('\n', withoutSignature));
        var paragraphs = clean
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        return new NormalizedBody(clean, Preview(clean), paragraphs, quotesRemoved, signatureRemoved, sourceFormat);
    }

    /// <summary>Unified line endings, invisible junk removed.</summary>
    private static List<string> Prepare(string text)
    {
        var sb = new StringBuilder(text.Length);

        foreach (var c in text.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            switch (c)
            {
                // Spaces that are not the ordinary space: turned into one, so the
                // regexes and the trimming behave.
                case ' ' or ' ' or ' ' or ' ' or ' ' or ' ':
                    sb.Append(' ');
                    break;

                // Invisible characters that only get in the way. Note that ‍
                // (ZWJ) is NOT here: it is what holds composed emoji together.
                case '​' or '‌' or '﻿' or '­':
                    break;

                default:
                    sb.Append(c);
                    break;
            }
        }

        return [.. sb.ToString().Split('\n').Select(l => l.TrimEnd())];
    }

    /// <summary>
    /// Cuts at the first quote attribution and drops lines starting with "&gt;".
    /// Everything after "El X escribio:" is the previous thread, not new text.
    /// </summary>
    private static (List<string> Lines, int Removed) StripQuotes(List<string> lines)
    {
        var cut = -1;

        for (var i = 0; i < lines.Count; i++)
        {
            if (OriginalMessageSeparator().IsMatch(lines[i]) || OutlookSeparator().IsMatch(lines[i]))
            {
                cut = i;
                break;
            }

            // Windows of 1, 2 and 3 joined lines: clients wrap the attribution, but
            // the pattern ends in ":" and that anchor only matches where the window
            // ends exactly there. Testing only the 3-line window would never match a
            // single-line attribution.
            var found = false;
            for (var k = 1; k <= 3 && i + k <= lines.Count; k++)
            {
                var window = string.Join(' ', lines.GetRange(i, k)).Trim();
                if (window.Length > 0 &&
                    (AttributionWithVerb().IsMatch(window) || EmbeddedHeaderBlock().IsMatch(window)))
                {
                    found = true;
                    break;
                }
            }

            if (found)
            {
                cut = i;
                break;
            }
        }

        var body = cut >= 0 ? lines.Take(cut).ToList() : lines;
        var removed = cut >= 0 ? lines.Count - cut : 0;

        // Stray quoted lines (an inline reply, or a forward with no attribution).
        var result = new List<string>(body.Count);
        foreach (var line in body)
        {
            if (line.TrimStart().StartsWith('>'))
            {
                removed++;
                continue;
            }
            result.Add(line);
        }

        return (result, removed);
    }

    /// <summary>
    /// Strips the signature. In order of reliability: the RFC 3676 "-- " delimiter,
    /// explicit markers, the legal disclaimer, and as a last resort a contact block at
    /// the end. The last one is the only real heuristic, which is why it requires
    /// several signals before deleting anything.
    /// </summary>
    private static (List<string> Lines, int Removed) StripSignature(List<string> lines)
    {
        if (lines.Count == 0)
        {
            return (lines, 0);
        }

        // 1. Standard delimiter.
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].TrimEnd() == "--" || lines[i] == "-- ")
            {
                return (lines.Take(i).ToList(), lines.Count - i);
            }
        }

        // 2. Explicit markers and legal disclaimers, only in the last third: the word
        // "confidencial" in the first paragraph is content, not a signature.
        var lastThird = Math.Max(1, lines.Count * 2 / 3);
        for (var i = lastThird; i < lines.Count; i++)
        {
            if (MobileSignature().IsMatch(lines[i]) || LegalDisclaimer().IsMatch(lines[i]))
            {
                return (lines.Take(i).ToList(), lines.Count - i);
            }
        }

        // 3. Contact block at the end: up to 8 short lines with at least two contact
        // signals (phone, email, link, a label such as "Tel.").
        var start = -1;
        var signals = 0;
        var limit = Math.Max(0, lines.Count - 8);

        for (var i = lines.Count - 1; i >= limit; i--)
        {
            var line = lines[i];

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // A long line is prose, not a signature line.
            if (line.Length > 70)
            {
                break;
            }

            var hits = (PhoneNumber().IsMatch(line) ? 1 : 0)
                + (EmailAddress().IsMatch(line) ? 1 : 0)
                + (Link().IsMatch(line) ? 1 : 0)
                + (ContactLabel().IsMatch(line) ? 1 : 0);

            if (hits > 0)
            {
                signals += hits;
                start = i;
                continue;
            }

            // A short line with no signals could be the name heading the signature,
            // but it is only accepted once signals have been seen below it.
            if (start >= 0 && line.Length <= 40)
            {
                start = i;
                continue;
            }

            break;
        }

        // Require two signals, and never consume the whole message: if the signature
        // started on the first line, what we have is not a signature, it is the mail.
        if (start > 0 && signals >= 2)
        {
            return (lines.Take(start).ToList(), lines.Count - start);
        }

        return (lines, 0);
    }

    private static string Collapse(string text)
    {
        var withoutSpaces = RepeatedSpaces().Replace(text, " ");
        var lines = withoutSpaces.Split('\n').Select(l => l.Trim());
        var joined = string.Join('\n', lines);
        return RepeatedBlankLines().Replace(joined, "\n\n").Trim();
    }

    private static string Preview(string clean)
    {
        if (clean.Length == 0)
        {
            return string.Empty;
        }

        var oneLine = RepeatedSpaces().Replace(clean.Replace('\n', ' '), " ").Trim();

        if (oneLine.Length <= PreviewLength)
        {
            return oneLine;
        }

        // Cut on a word, not on a character, and never split a surrogate pair: an
        // emoji right at the 140 mark would come out as a replacement glyph.
        var cut = oneLine.LastIndexOf(' ', Math.Min(PreviewLength, oneLine.Length - 1));
        if (cut < PreviewLength / 2)
        {
            cut = PreviewLength;
            while (cut > 0 && char.IsHighSurrogate(oneLine[cut - 1]))
            {
                cut--;
            }
        }

        return string.Concat(oneLine.AsSpan(0, cut).TrimEnd(), "...");
    }
}
