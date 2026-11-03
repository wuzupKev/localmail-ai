namespace LocalMailAI.Api.Classification;

/// <summary>Resultado de una clasificacion: lo que acaba en la tabla <c>classifications</c>.</summary>
public sealed record Classification(Bucket Bucket, double Confidence, IReadOnlyList<Signal> Signals);

/// <summary>
/// Clasificador provisional a partir de las senales deterministas.
///
/// Cumple dos papeles distintos y los dos importan:
///
/// 1. En la ingesta (BACKEND.md §5, etapa 4) da una clasificacion disponible YA,
///    antes de calcular embeddings. El usuario ve su bandeja ordenada desde el primer
///    correo sincronizado, no cuando acabe el pipeline entero.
/// 2. Es el BASELINE del hito 5. Sin el no se puede saber si el clasificador
///    entrenado aporta algo, y se estaria optimizando a ciegas.
///
/// Aqui no hay probabilidades de verdad: <c>Confidence</c> es una funcion monotona de
/// la fuerza de las senales, calibrada para que un remitente desconocido caiga por
/// debajo de UMBRAL_CONFIANZA (0.55) y el front lo mande a "No estoy seguro" en vez
/// de adivinar. El clasificador del hito 6 sustituye esto por probabilidades reales.
/// </summary>
public static class SignalBaseline
{
    /// <summary>Mismo valor que UMBRAL_CONFIANZA en src/lib/mock.ts.</summary>
    public const double ConfidenceThreshold = 0.55;

    public static Classification Classify(IReadOnlyList<WeightedSignal> signals, SignalContext ctx)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(ctx);

        var score = signals.Sum(s => s.Weight);
        var publicas = signals.Select(s => s.ToSignal()).ToList();

        var esMaquina = signals.Any(s => s.Kind
            is SignalKind.ListUnsubscribe
            or SignalKind.NoReplyAddress
            or SignalKind.AutoSubmitted);

        // Senales que solo aparecen cuando el correo va dirigido a esta persona en
        // concreto. Un aviso de GitHub lleva marcas de maquina y aun asi requiere
        // accion, asi que las personales mandan sobre las de maquina.
        var esPersonal = signals.Any(s => s.Kind
            is SignalKind.RepliedBefore
            or SignalKind.ThreadParticipant
            or SignalKind.ThirdUnanswered);

        Bucket bucket;

        if (esMaquina && !esPersonal)
        {
            // Automatizado y ruido son las dos maquinas. Lo que las separa no es la
            // cabecera -ambas la llevan- sino si el usuario lo abre: el aviso de
            // despliegue se lee, la promocion no. Por eso hace falta opened.
            var nuncaAbierto = ctx.SenderReceived >= 5 && ctx.SenderOpened == 0;
            bucket = nuncaAbierto ? Bucket.Ruido : Bucket.Automatizado;
        }
        else if (score >= 2)
        {
            bucket = Bucket.Responder;
        }
        else
        {
            bucket = Bucket.Informativo;
        }

        return new Classification(bucket, Confianza(score, ctx), publicas);
    }

    /// <summary>
    /// Confianza en [0.30, 0.95]. Crece con la fuerza de las senales y baja cuando no
    /// hay historial del remitente.
    ///
    /// El remitente nuevo resta, NO pone un techo por debajo del umbral. La diferencia
    /// importa el dia 1, cuando todos los remitentes son nuevos: con un techo, la
    /// bandeja entera caeria en "No estoy seguro" y los otros cuatro cajones saldrian a
    /// cero, que es justo lo que §6 dice que no deberia pasar -las senales
    /// deterministas "funcionan desde el minuto cero"-. Un boletin de un remitente
    /// desconocido sigue siendo claramente un boletin: List-Unsubscribe, Precedence:
    /// bulk y una direccion no-reply no necesitan historial para ser concluyentes.
    ///
    /// Lo que sigue cayendo bajo el umbral es el caso que debe caer: remitente nuevo
    /// con senales flojas. El aviso de vencimiento de una factura de un proveedor
    /// desconocido sale en 0.49, que es lo que describe mock.ts para ese mismo correo.
    /// </summary>
    private static double Confianza(int score, SignalContext ctx)
    {
        var fuerza = Math.Min(Math.Abs(score), 8);
        var confianza = 0.5 + 0.055 * fuerza;

        if (ctx.SenderReceived == 0)
        {
            confianza -= 0.12;
        }

        return Math.Round(Math.Clamp(confianza, 0.30, 0.95), 2);
    }
}
