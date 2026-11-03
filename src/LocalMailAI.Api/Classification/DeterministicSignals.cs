using System.Text.RegularExpressions;
using LocalMailAI.Api.Mail;

namespace LocalMailAI.Api.Classification;

/// <summary>Cubo de triage. Mismos cuatro valores que <c>Bucket</c> en src/lib/mock.ts.</summary>
public enum Bucket
{
    Responder,
    Informativo,
    Automatizado,
    Ruido,
}

public static class BucketNames
{
    /// <summary>El front espera estas cadenas exactas, en minusculas y en espanol.</summary>
    public static string ToWire(this Bucket b) => b switch
    {
        Bucket.Responder => "responder",
        Bucket.Informativo => "informativo",
        Bucket.Automatizado => "automatizado",
        Bucket.Ruido => "ruido",
        _ => throw new ArgumentOutOfRangeException(nameof(b), b, null),
    };

    public static Bucket Parse(string valor) => valor switch
    {
        "responder" => Bucket.Responder,
        "informativo" => Bucket.Informativo,
        "automatizado" => Bucket.Automatizado,
        "ruido" => Bucket.Ruido,
        _ => throw new ArgumentException($"bucket desconocido: {valor}", nameof(valor)),
    };
}

/// <summary>
/// Identidad estable de cada senal de §6.
///
/// Existe para que la logica no dependa del texto de la etiqueta. La etiqueta es una
/// cadena de interfaz y se va a reescribir -lleva numeros dentro y hay que traducirla-;
/// si el clasificador comparase por ella, un cambio de redaccion alteraria el triage
/// sin que nadie se diera cuenta.
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

/// <summary>Senal tal y como la consume el front: <c>{ label, sign }</c>.</summary>
public sealed record Signal(string Label, string Sign);

/// <summary>
/// Senal con su clave y su peso. Ni la clave ni el peso salen a la API: sirven para
/// ordenar, para clasificar y para construir el vector del clasificador del hito 6.
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
/// Lo que hay que saber del usuario y del historial para evaluar las senales.
/// Nada de esto sale del correo en si: viene de la BD y de la cuenta configurada.
/// </summary>
public sealed record SignalContext
{
    /// <summary>Direcciones del propio usuario, en minusculas.</summary>
    public required IReadOnlySet<string> OwnAddresses { get; init; }

    /// <summary>Dominio de su organizacion, sin arroba. Null si no se sabe.</summary>
    public string? OrgDomain { get; init; }

    /// <summary>Correos recibidos de este remitente (sender_stats.received).</summary>
    public int SenderReceived { get; init; }

    /// <summary>Veces que el usuario le ha respondido (sender_stats.replied).</summary>
    public int SenderReplied { get; init; }

    /// <summary>
    /// Veces que el usuario ha abierto correo de este remitente (sender_stats.opened).
    /// Es lo que separa "automatizado" de "ruido": las dos son maquinas, pero una la
    /// lees y la otra no.
    /// </summary>
    public int SenderOpened { get; init; }

    /// <summary>
    /// El usuario tiene algun mensaje propio en este hilo. La spec lo describe como
    /// "tu direccion en References", pero References solo lleva Message-IDs; en la
    /// practica se resuelve mirando si algun mensaje del hilo salio de Enviados.
    /// </summary>
    public bool IParticipatesInThread { get; init; }

    /// <summary>
    /// Mensajes seguidos del remitente en este hilo sin respuesta del usuario.
    /// A partir de 3 es la senal "Tercer mensaje sin respuesta tuya".
    /// </summary>
    public int ConsecutiveUnanswered { get; init; }

    /// <summary>Cuerpo ya normalizado. La pregunta directa se busca aqui, no en el HTML.</summary>
    public string CleanBody { get; init; } = string.Empty;
}

/// <summary>
/// Las senales deterministas de BACKEND.md §6: sin IA, en microsegundos, y
/// brutalmente predictivas. Cada una lleva texto legible porque la explicacion
/// visible es lo que hace que el usuario confie en el triage o lo corrija.
///
/// Los pesos son los de la tabla de §6: +++ = 3, ++ = 2, + = 1, y negativos igual.
/// </summary>
public static partial class DeterministicSignals
{
    private const int MaxSignals = 6;   // §6: maximo 6 por correo

    // "No requiere accion por vuestra parte", "solo para tu informacion", "FYI".
    // Es la unica senal que da evidencia POSITIVA de que un correo es informativo, en
    // vez de llegar a ese cajon por descarte.
    [GeneratedRegex(@"(no requiere (ninguna )?(accion|acción)|no es necesario responder|no hace falta responder|s[oó]lo (para )?(tu|su|vuestra) informaci[oó]n|para que quede constancia|no action (is )?required|no reply (is )?needed|for your information|FYI)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SinAccionRequerida();

    // Acuse de recibo: "hemos recibido su solicitud". Dice explicitamente que la
    // pelota esta en el tejado del remitente, no en el tuyo.
    [GeneratedRegex(@"(hemos recibido (su|tu)|su (solicitud|peticion|petición|mensaje) ha sido (registrad|recibid)|we have received your|your (request|ticket) has been (received|registered))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AcuseDeRecibo();

    // El signo de interrogacion de ancho completo aparece en correo asiatico.
    [GeneratedRegex(@"[?？]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TerminaEnPregunta();

    public static List<WeightedSignal> Compute(MessageFacts facts, SignalContext ctx)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(ctx);

        var senales = new List<WeightedSignal>(10);

        // --- Historial con el remitente: la senal mas predictiva que hay ---------

        if (ctx.SenderReplied > 0)
        {
            senales.Add(new(SignalKind.RepliedBefore, $"Le has respondido {ctx.SenderReplied} veces", 3));
        }
        else if (ctx.SenderReceived == 0)
        {
            // Remitente desconocido: no es buena ni mala senal, es falta de datos.
            // Por eso es neutral y ademas baja la confianza en SignalBaseline.
            senales.Add(new(SignalKind.NewSender, "Remitente nuevo, sin historial", 0));
        }
        else if (ctx.SenderReceived >= 5)
        {
            // Con pocos correos recibidos, "nunca has respondido" no dice nada:
            // puede que simplemente no haya habido ocasion.
            senales.Add(new(SignalKind.NeverReplied, $"Nunca lo has respondido (0 de {ctx.SenderReceived})", -2));
        }

        // --- Como te han puesto en el correo ------------------------------------

        var enPara = facts.To.Any(ctx.OwnAddresses.Contains);
        var enCc = facts.Cc.Any(ctx.OwnAddresses.Contains);

        if (enPara && !enCc)
        {
            senales.Add(new(SignalKind.ToNotCc, "Vas en Para:, no en Cc:", 2));
        }
        else if (enCc && !enPara)
        {
            senales.Add(new(SignalKind.CcNotTo, "Vas en Cc:, no en Para:", -2));
        }

        // --- Estado del hilo ----------------------------------------------------

        if (ctx.IParticipatesInThread)
        {
            senales.Add(new(SignalKind.ThreadParticipant, "Hilo en el que ya participas", 2));
        }

        if (ctx.ConsecutiveUnanswered >= 3)
        {
            senales.Add(new(SignalKind.ThirdUnanswered, "Tercer mensaje sin respuesta tuya", 3));
        }

        // --- Contenido ----------------------------------------------------------

        if (AcabaPreguntando(ctx.CleanBody))
        {
            senales.Add(new(SignalKind.EndsWithQuestion, "Termina en pregunta directa", 1));
        }

        // Peso -2: aleja de "responder" con la misma fuerza con la que "vas en Para:"
        // acerca. Un correo dirigido a ti que dice que no requiere accion es un
        // empate, y un empate tiene que salir con poca confianza, no con mucha.
        if (SinAccionRequerida().IsMatch(ctx.CleanBody))
        {
            senales.Add(new(SignalKind.ExplicitlyNoAction, "Dice explicitamente: no requiere accion", -2));
        }

        if (AcuseDeRecibo().IsMatch(ctx.CleanBody))
        {
            senales.Add(new(SignalKind.Acknowledgement, "Acuse de recibo: no espera respuesta", -2));
        }

        if (ctx.OrgDomain is { Length: > 0 } dominio &&
            facts.FromEmail.EndsWith("@" + dominio, StringComparison.OrdinalIgnoreCase))
        {
            senales.Add(new(SignalKind.SameOrgDomain, "Mismo dominio que tu organizacion", 1));
        }

        // --- Marcas de envio masivo o automatico --------------------------------

        if (facts.HasListUnsubscribe)
        {
            senales.Add(new(SignalKind.ListUnsubscribe, "List-Unsubscribe presente", -3));
        }

        if (facts.IsNoReplyAddress)
        {
            senales.Add(new(SignalKind.NoReplyAddress, "Direccion no-reply", -2));
        }

        if (facts.IsAutoSubmitted)
        {
            senales.Add(new(SignalKind.AutoSubmitted, "Auto-Submitted / Precedence: bulk", -2));
        }

        // --- Autenticacion ------------------------------------------------------

        if (facts.SpfPass == true && facts.DkimPass == true)
        {
            senales.Add(new(SignalKind.Authenticated, "Remitente verificado (DKIM + SPF)", 1));
        }

        // §6: maximo 6, ordenadas por peso absoluto descendente. OrderByDescending
        // es estable, asi que a igual peso manda el orden en que se han anadido, que
        // es el de la tabla de la spec. Dos ejecuciones dan el mismo resultado.
        return [.. senales.OrderByDescending(s => Math.Abs(s.Weight)).Take(MaxSignals)];
    }

    /// <summary>
    /// Pregunta directa al final del mensaje. Se mira el final y no todo el cuerpo:
    /// una pregunta en el primer parrafo de un correo largo suele ser retorica
    /// ("¿Sabias que...?" de una newsletter).
    /// </summary>
    private static bool AcabaPreguntando(string cleanBody)
    {
        if (string.IsNullOrWhiteSpace(cleanBody))
        {
            return false;
        }

        var lineas = cleanBody.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Las dos ultimas lineas: la ultima suele ser la despedida ("Un saludo").
        foreach (var linea in lineas.Reverse().Take(2))
        {
            if (TerminaEnPregunta().IsMatch(linea))
            {
                return true;
            }
        }

        return false;
    }
}
