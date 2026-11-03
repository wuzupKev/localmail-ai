using MimeKit;

namespace LocalMailAI.Api.Mail;

/// <summary>
/// Lo que se puede saber de un correo leyendo solo sus cabeceras, sin IA y en
/// microsegundos. Alimenta las senales deterministas de BACKEND.md §6 y las columnas
/// de <c>emails</c> de §4.
/// </summary>
public sealed record MessageFacts
{
    public required string MessageId { get; init; }

    /// <summary>Raiz del hilo: el primer References, o In-Reply-To, o el propio Message-ID.</summary>
    public required string ThreadId { get; init; }

    public string? FromName { get; init; }
    public required string FromEmail { get; init; }
    public required string[] To { get; init; }
    public required string[] Cc { get; init; }
    public string? Subject { get; init; }
    public required DateTimeOffset DateUtc { get; init; }
    public string? InReplyTo { get; init; }

    /// <summary>Cadena de References, de la mas antigua a la mas reciente.</summary>
    public required string[] References { get; init; }

    public bool HasListUnsubscribe { get; init; }

    /// <summary>Auto-Submitted distinto de "no", o Precedence: bulk/list/junk.</summary>
    public bool IsAutoSubmitted { get; init; }

    /// <summary>De Authentication-Results. Null = la cabecera no dice nada.</summary>
    public bool? SpfPass { get; init; }
    public bool? DkimPass { get; init; }

    /// <summary>Direccion del tipo no-reply / noreply / do-not-reply.</summary>
    public bool IsNoReplyAddress { get; init; }

    public int RecipientCount => To.Length + Cc.Length;

    public static MessageFacts From(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var remitente = message.From.Mailboxes.FirstOrDefault()
            ?? message.Sender
            ?? message.ReplyTo.Mailboxes.FirstOrDefault();

        var fromEmail = (remitente?.Address ?? string.Empty).Trim().ToLowerInvariant();

        // Hay correo sin Message-ID. Inventarse uno estable a partir de lo que si
        // hay es mejor que descartar el mensaje o generar un GUID distinto en cada
        // sync, que duplicaria la fila en cada pasada.
        var messageId = string.IsNullOrWhiteSpace(message.MessageId)
            ? SinMessageId(fromEmail, message.Subject, message.Date)
            : message.MessageId.Trim();

        var references = message.References?.ToArray() ?? [];
        var inReplyTo = string.IsNullOrWhiteSpace(message.InReplyTo) ? null : message.InReplyTo.Trim();

        // La raiz del hilo es el References mas antiguo. Si no hay References pero si
        // In-Reply-To, ese es lo mas cercano a la raiz que conocemos.
        var threadId = references.Length > 0
            ? references[0]
            : inReplyTo ?? messageId;

        return new MessageFacts
        {
            MessageId = messageId,
            ThreadId = threadId,
            FromName = string.IsNullOrWhiteSpace(remitente?.Name) ? null : remitente.Name.Trim(),
            FromEmail = fromEmail,
            To = Direcciones(message.To),
            Cc = Direcciones(message.Cc),
            Subject = string.IsNullOrWhiteSpace(message.Subject) ? null : message.Subject.Trim(),
            DateUtc = message.Date.ToUniversalTime(),
            InReplyTo = inReplyTo,
            References = references,
            HasListUnsubscribe = message.Headers.Contains(HeaderId.ListUnsubscribe),
            IsAutoSubmitted = EsAutomatico(message),
            SpfPass = ResultadoAutenticacion(message, "spf"),
            DkimPass = ResultadoAutenticacion(message, "dkim"),
            IsNoReplyAddress = EsNoReply(fromEmail),
        };
    }

    private static string[] Direcciones(InternetAddressList lista) =>
        [.. lista.Mailboxes
            .Select(m => (m.Address ?? string.Empty).Trim().ToLowerInvariant())
            .Where(a => a.Length > 0)];

    private static string SinMessageId(string fromEmail, string? subject, DateTimeOffset fecha)
    {
        var semilla = $"{fromEmail}|{subject}|{fecha.UtcDateTime:O}";
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(semilla));
        return $"sin-message-id-{Convert.ToHexStringLower(hash)[..32]}@localmail.local";
    }

    private static bool EsAutomatico(MimeMessage message)
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

        // Cabeceras que solo pone un sistema de envio masivo.
        return message.Headers.Contains("X-Auto-Response-Suppress")
            || message.Headers.Contains("X-Autoreply")
            || message.Headers.Contains("List-Id");
    }

    /// <summary>
    /// Lee Authentication-Results buscando "spf=pass" o "dkim=pass".
    /// Null cuando la cabecera no existe o no menciona ese mecanismo: no es lo mismo
    /// "ha fallado la verificacion" que "nadie la ha hecho".
    /// </summary>
    private static bool? ResultadoAutenticacion(MimeMessage message, string mecanismo)
    {
        var encontrado = false;

        foreach (var cabecera in message.Headers)
        {
            if (!cabecera.Field.Equals("Authentication-Results", StringComparison.OrdinalIgnoreCase) &&
                !cabecera.Field.Equals("ARC-Authentication-Results", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var valor = cabecera.Value;
            var i = valor.IndexOf(mecanismo + "=", StringComparison.OrdinalIgnoreCase);
            if (i < 0)
            {
                continue;
            }

            encontrado = true;
            var resto = valor.AsSpan(i + mecanismo.Length + 1).TrimStart();
            if (resto.StartsWith("pass", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return encontrado ? false : null;
    }

    private static bool EsNoReply(string direccion)
    {
        var local = direccion.Split('@')[0].Replace("-", string.Empty).Replace(".", string.Empty)
            .Replace("_", string.Empty);

        return local.Contains("noreply", StringComparison.OrdinalIgnoreCase)
            || local.Contains("donotreply", StringComparison.OrdinalIgnoreCase)
            || local.Contains("nottobereplied", StringComparison.OrdinalIgnoreCase);
    }
}
