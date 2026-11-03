using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using LocalMailAI.Api.Api;
using LocalMailAI.Api.Classification;

namespace LocalMailAI.Api.Data;

/// <summary>
/// Lectura de correos para la API (BACKEND.md §3). SQL explicito con Dapper, nada de
/// un ORM que decida por su cuenta que consulta lanza.
///
/// Un correo por debajo de UMBRAL_CONFIANZA no pertenece a su bucket: pertenece al
/// cajon "duda". La regla esta escrita UNA vez y la usan tanto el filtro por bucket
/// como los contadores. Si cada uno la aplicara a su manera, los numeros de los tiles
/// no cuadrarian con las listas que esos tiles abren, y encima la paginacion se
/// rompria: una pagina de 50 se quedaria en 43 al filtrar en el cliente.
/// </summary>
public sealed class EmailRepository(Database db)
{
    private const int MaxLimit = 200;

    // Una sola consulta con LEFT JOIN: extracted, summary y la clasificacion pueden
    // no existir todavia -el pipeline las rellena despues (§5)- y el correo tiene que
    // poder servirse igual, con lo que haya.
    private const string Seleccion = """
        SELECT  e.id            AS Id,
                e.from_name     AS FromName,
                e.from_email    AS FromEmail,
                e.subject       AS Subject,
                e.date_utc      AS DateUtc,
                e.unread        AS Unread,
                e.thread_id     AS ThreadId,
                t.body_clean    AS BodyClean,
                t.preview       AS Preview,
                c.bucket        AS Bucket,
                c.confidence    AS Confidence,
                c.signals_json  AS SignalsJson,
                x.accion        AS Accion,
                x.urgencia      AS Urgencia,
                x.fecha_limite  AS FechaLimite,
                s.summary       AS Summary,
                (SELECT COUNT(*) FROM emails e2
                  WHERE e2.thread_id = e.thread_id
                    AND e2.account_id = e.account_id) AS ThreadCount   -- acotado por cuenta: un hilo que llega a dos buzones no se cuenta dos veces
        FROM emails e
        LEFT JOIN email_text      t ON t.email_id  = e.id
        LEFT JOIN classifications c ON c.email_id  = e.id
        LEFT JOIN extractions     x ON x.email_id  = e.id
        LEFT JOIN summaries       s ON s.thread_id = e.thread_id
        """;

    private sealed class Fila
    {
        public string Id { get; init; } = string.Empty;
        public string? FromName { get; init; }
        public string FromEmail { get; init; } = string.Empty;
        public string? Subject { get; init; }
        public string DateUtc { get; init; } = string.Empty;
        public long Unread { get; init; }
        public string ThreadId { get; init; } = string.Empty;
        public string? BodyClean { get; init; }
        public string? Preview { get; init; }
        public string? Bucket { get; init; }
        public double? Confidence { get; init; }
        public string? SignalsJson { get; init; }
        public string? Accion { get; init; }
        public string? Urgencia { get; init; }
        public string? FechaLimite { get; init; }
        public string? Summary { get; init; }
        public long ThreadCount { get; init; }
    }

    /// <summary>
    /// Recuentos para los tiles de la pantalla principal. Una sola consulta: el front
    /// los pide en cada carga y pedir los cinco cajones enteros para pintar cinco
    /// numeros seria absurdo.
    ///
    /// Un correo por debajo del umbral cuenta en "duda" y NO en su bucket. Si contara
    /// en los dos, la suma de los tiles no daria el total y el usuario veria numeros
    /// que no cuadran.
    /// </summary>
    public async Task<CountsDto> CountsAsync()
    {
        using var cn = db.Open();

        // SUM() en SQLite es INTEGER y llega como long: se lee en una fila propia y
        // se convierte, en vez de declarar el DTO del contrato con long.
        var fila = await cn.QuerySingleAsync<FilaCounts>(
            """
            -- COUNT y no SUM: SUM sobre cero filas devuelve NULL, y una bandeja
            -- vacia es el estado de toda instalacion nueva.
            SELECT
              COUNT(CASE WHEN c.confidence >= @umbral AND c.bucket = 'responder'    THEN 1 END) AS Responder,
              COUNT(CASE WHEN c.confidence >= @umbral AND c.bucket = 'informativo'  THEN 1 END) AS Informativo,
              COUNT(CASE WHEN c.confidence >= @umbral AND c.bucket = 'automatizado' THEN 1 END) AS Automatizado,
              COUNT(CASE WHEN c.confidence >= @umbral AND c.bucket = 'ruido'        THEN 1 END) AS Ruido,
              -- Sin clasificar todavia tambien es duda: el pipeline aun no ha llegado
              -- a ese correo y afirmar un bucket seria inventarselo.
              COUNT(CASE WHEN c.bucket IS NULL OR c.confidence < @umbral            THEN 1 END) AS Duda,
              COUNT(CASE WHEN e.unread <> 0                                         THEN 1 END) AS Unread
            FROM emails e
            LEFT JOIN classifications c ON c.email_id = e.id
            """,
            new { umbral = SignalBaseline.ConfidenceThreshold });

        return new CountsDto(
            (int)fila.Responder,
            (int)fila.Informativo,
            (int)fila.Automatizado,
            (int)fila.Ruido,
            (int)fila.Duda,
            (int)fila.Unread);
    }

    private sealed record FilaCounts(
        long Responder,
        long Informativo,
        long Automatizado,
        long Ruido,
        long Duda,
        long Unread);

    /// <summary>
    /// Marca leido o no leido. Devuelve el correo ya actualizado, que es lo que el
    /// front necesita para repintar la fila sin volver a pedirlo.
    ///
    /// Marcar leido por primera vez incrementa <c>sender_stats.opened</c>, y eso NO es
    /// un detalle de contabilidad: es la senal que distingue "automatizado" de "ruido"
    /// (§6). Las dos son maquinas y llevan las mismas cabeceras; lo que las separa es
    /// si el usuario las abre. Cada correo que abre mejora ese triage.
    /// </summary>
    public async Task<EmailDto?> SetUnreadAsync(string id, bool unread, DateTimeOffset ahora)
    {
        using var cn = db.Open();
        using var tx = cn.BeginTransaction();

        var estadoPrevio = await cn.QuerySingleOrDefaultAsync<long?>(
            "SELECT unread FROM emails WHERE id = @id", new { id }, tx);

        if (estadoPrevio is null)
        {
            tx.Rollback();
            return null;
        }

        var eraNoLeido = estadoPrevio.Value != 0;

        await cn.ExecuteAsync(
            "UPDATE emails SET unread = @valor WHERE id = @id",
            new { id, valor = unread ? 1 : 0 }, tx);

        // Solo al pasar de no leido a leido, y solo una vez: abrir el mismo correo
        // diez veces no lo hace diez veces mas interesante.
        if (eraNoLeido && !unread)
        {
            await cn.ExecuteAsync(
                """
                UPDATE sender_stats
                SET opened = opened + 1
                WHERE (account_id, from_email) IN (
                    SELECT account_id, from_email FROM emails WHERE id = @id
                )
                """,
                new { id }, tx);
        }

        tx.Commit();

        return await GetAsync(id, ahora);
    }

    public async Task<EmailPageDto> ListAsync(string? bucket, int limit, string? cursor, DateTimeOffset ahora)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);

        var (cursorDate, cursorId) = DecodeCursor(cursor);

        // Se pide una fila mas de las necesarias: si llega, hay pagina siguiente, y
        // asi no hace falta un COUNT aparte sobre toda la tabla.
        var sql = $"""
            {Seleccion}
            WHERE (
                        @bucket IS NULL
                     OR (@bucket = 'duda' AND (c.bucket IS NULL OR c.confidence < @umbral))
                     OR (@bucket <> 'duda' AND c.bucket = @bucket AND c.confidence >= @umbral)
                  )
              AND (@cursorDate IS NULL OR (e.date_utc, e.id) < (@cursorDate, @cursorId))
            ORDER BY e.date_utc DESC, e.id DESC
            LIMIT @limit
            """;

        using var cn = db.Open();
        var filas = (await cn.QueryAsync<Fila>(sql, new
        {
            bucket,
            cursorDate,
            cursorId,
            limit = limit + 1,
            umbral = SignalBaseline.ConfidenceThreshold,
        })).ToList();

        string? siguiente = null;
        if (filas.Count > limit)
        {
            var ultima = filas[limit - 1];
            siguiente = EncodeCursor(ultima.DateUtc, ultima.Id);
            filas.RemoveRange(limit, filas.Count - limit);
        }

        return new EmailPageDto([.. filas.Select(f => Mapear(f, ahora))], siguiente);
    }

    public async Task<EmailDto?> GetAsync(string id, DateTimeOffset ahora)
    {
        using var cn = db.Open();
        var fila = await cn.QuerySingleOrDefaultAsync<Fila>(
            $"{Seleccion}\nWHERE e.id = @id", new { id });

        return fila is null ? null : Mapear(fila, ahora);
    }

    private static EmailDto Mapear(Fila f, DateTimeOffset ahora)
    {
        var fecha = DateTimeOffset.Parse(f.DateUtc, null, DateTimeStyles.RoundtripKind);

        var senales = string.IsNullOrWhiteSpace(f.SignalsJson)
            ? []
            : JsonSerializer.Deserialize<SignalDto[]>(f.SignalsJson) ?? [];

        var cuerpo = string.IsNullOrWhiteSpace(f.BodyClean)
            ? []
            : f.BodyClean.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new EmailDto
        {
            Id = f.Id,
            From = new MailboxDto(f.FromName ?? f.FromEmail, f.FromEmail),
            Subject = f.Subject ?? "(sin asunto)",
            Preview = f.Preview ?? string.Empty,
            Body = cuerpo,
            Time = RelativeTime.Format(fecha, ahora),

            // Un correo sin clasificar todavia sale como informativo con confianza 0:
            // el front lo mandara a "No estoy seguro" por estar bajo el umbral, que es
            // exactamente lo que queremos decir mientras el pipeline no ha llegado.
            Bucket = f.Bucket ?? "informativo",
            Confidence = f.Confidence ?? 0,
            Signals = senales,

            ThreadCount = (int)f.ThreadCount,
            Unread = f.Unread != 0,
            Extracted = f.Accion is null ? null : new ExtractedDto(f.Accion, f.Urgencia ?? "media", f.FechaLimite),

            // §5: el resumen solo tiene sentido en hilos largos.
            Summary = f.ThreadCount >= 3 ? f.Summary : null,
        };
    }

    // El cursor es opaco para el cliente (§3) pero legible para nosotros: la fecha y
    // el id de la ultima fila servida. Sin el id, dos correos con la misma fecha
    // harian que la paginacion se saltara uno o repitiera otro.
    private static string EncodeCursor(string dateUtc, string id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{dateUtc}|{id}"));

    private static (string? Date, string? Id) DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return (null, null);
        }

        try
        {
            var plano = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var corte = plano.IndexOf('|');
            return corte <= 0 ? (null, null) : (plano[..corte], plano[(corte + 1)..]);
        }
        catch (FormatException)
        {
            // Cursor manipulado o de otra version: servir la primera pagina es mas
            // util que devolver un error que el front no puede arreglar.
            return (null, null);
        }
    }
}
