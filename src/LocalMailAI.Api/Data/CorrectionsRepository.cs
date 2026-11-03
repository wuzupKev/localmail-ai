using Dapper;
using LocalMailAI.Api.Api;
using LocalMailAI.Api.Classification;

namespace LocalMailAI.Api.Data;

/// <summary>Ejemplos de entrenamiento por origen (<c>corrections.source</c>).</summary>
public sealed record ExampleCounts(int User, int BootstrapSent, int BootstrapFolder, int Rule)
{
    /// <summary>
    /// Total en vigor. Es lo que el front muestra en el toast. No se serializa: va
    /// aparte como `examples`, y duplicarlo dentro del desglose solo daria dos sitios
    /// donde el mismo numero podria discrepar.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int Total => User + BootstrapSent + BootstrapFolder + Rule;
}

/// <summary>Respuesta de una correccion o de un deshacer (§3).</summary>
///
/// `Email` se serializa AUNQUE SEA NULL, al contrario que el resto de la API. En un
/// deshacer sin nada que deshacer, null es la respuesta -"no habia nada"- y no la
/// ausencia de un dato. Si se omitiera, el front tendria que distinguir `email?: Email`
/// de `email: Email | null`, que son dos comprobaciones distintas en TypeScript.
public sealed record CorrectionResult(
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    EmailDto? Email,
    int Examples);

/// <summary>
/// Correcciones del usuario y deshacer (BACKEND.md §3 y regla 4 de §1).
///
/// La regla que manda aqui: las correcciones son HISTORIAL, no estado. Nunca se
/// sobreescribe ni se borra una fila; deshacer inserta un evento inverso. Eso permite
/// reentrenar desde cero y medir si el modelo mejora, que es imposible si el historial
/// se pisa.
/// </summary>
public sealed class CorrectionsRepository(Database db, EmailRepository emails, ILogger<CorrectionsRepository> log)
{
    /// <summary>
    /// Registra una correccion del usuario y deja el correo en el bucket corregido.
    ///
    /// La confianza pasa a 1.0 a proposito: lo ha dicho el usuario. Si se quedara en
    /// la del modelo, un correo corregido que estuviera por debajo del umbral seguiria
    /// saliendo en "No estoy seguro" y la correccion pareceria no haber funcionado.
    /// </summary>
    public async Task<CorrectionResult?> ApplyAsync(string emailId, Bucket bucket, DateTimeOffset ahora)
    {
        using var cn = db.Open();
        using var tx = cn.BeginTransaction();

        var actual = await cn.QuerySingleOrDefaultAsync<EstadoActual>(
            """
            SELECT e.id AS EmailId, c.bucket AS Bucket, c.confidence AS Confidence
            FROM emails e
            LEFT JOIN classifications c ON c.email_id = e.id
            WHERE e.id = @emailId
            """,
            new { emailId }, tx);

        if (actual is null)
        {
            tx.Rollback();
            return null;
        }

        await cn.ExecuteAsync(
            """
            INSERT INTO corrections (email_id, bucket_before, bucket_after, source, created_at, confidence_before)
            VALUES (@emailId, @bucketBefore, @bucketAfter, 'user', @ahora, @confidenceBefore)
            """,
            new
            {
                emailId,
                bucketBefore = actual.Bucket,
                bucketAfter = bucket.ToWire(),
                ahora = ahora.ToString("O"),
                confidenceBefore = actual.Confidence,
            }, tx);

        await EscribirClasificacionAsync(cn, tx, emailId, bucket.ToWire(), 1.0, ahora);

        var ejemplos = await CountAsync(cn, tx);
        tx.Commit();

        log.LogInformation("Correccion de {EmailId}: {Antes} -> {Despues} ({Ejemplos} ejemplos)",
            emailId, actual.Bucket ?? "sin clasificar", bucket.ToWire(), ejemplos.Total);

        return new CorrectionResult(await emails.GetAsync(emailId, ahora), ejemplos.Total);
    }

    /// <summary>
    /// Deshace la ultima correccion del usuario que siga en vigor.
    ///
    /// Devuelve <c>Email = null</c> con el total de ejemplos si no hay nada que
    /// deshacer, en vez de un error: asi el Ctrl+Z del front no tiene que saber si
    /// queda pila.
    /// </summary>
    public async Task<CorrectionResult> UndoAsync(DateTimeOffset ahora)
    {
        using var cn = db.Open();
        using var tx = cn.BeginTransaction();

        // Se recorre el registro de lo mas reciente hacia atras llevando la cuenta de
        // los deshaceres pendientes. Contar undos y restarlos no vale: si el usuario
        // deshace dos veces y luego corrige otro correo, el siguiente deshacer tiene
        // que afectar a esa correccion nueva, no a una ya cancelada.
        var eventos = await cn.QueryAsync<EventoCorreccion>(
            """
            SELECT id AS Id, email_id AS EmailId, bucket_before AS BucketBefore,
                   bucket_after AS BucketAfter, source AS Source, confidence_before AS ConfidenceBefore
            FROM corrections
            WHERE source IN ('user', 'undo')
            ORDER BY id DESC
            """, tx);

        EventoCorreccion? objetivo = null;
        var deshaceresPendientes = 0;

        foreach (var evento in eventos)
        {
            if (evento.Source == "undo")
            {
                deshaceresPendientes++;
                continue;
            }

            if (deshaceresPendientes > 0)
            {
                deshaceresPendientes--;
                continue;
            }

            objetivo = evento;
            break;
        }

        if (objetivo is null)
        {
            var sinNada = await CountAsync(cn, tx);
            tx.Rollback();
            return new CorrectionResult(null, sinNada.Total);
        }

        // Evento inverso, no borrado (§1, regla 4). Con source='undo' para que no
        // cuente como ejemplo de entrenamiento: si contara, le estariamos ensenando al
        // modelo la correccion y su contraria a la vez.
        await cn.ExecuteAsync(
            """
            INSERT INTO corrections (email_id, bucket_before, bucket_after, source, created_at, confidence_before)
            VALUES (@emailId, @bucketBefore, @bucketAfter, 'undo', @ahora, @confidenceBefore)
            """,
            new
            {
                emailId = objetivo.EmailId,
                bucketBefore = objetivo.BucketAfter,
                bucketAfter = objetivo.BucketBefore,
                ahora = ahora.ToString("O"),
                confidenceBefore = 1.0,
            }, tx);

        // Volver a donde estaba, confianza incluida. Si el correo no estaba clasificado
        // antes de la correccion, se queda sin clasificar: el pipeline lo recogera.
        if (objetivo.BucketBefore is null)
        {
            await cn.ExecuteAsync(
                "DELETE FROM classifications WHERE email_id = @emailId",
                new { emailId = objetivo.EmailId }, tx);
        }
        else
        {
            await EscribirClasificacionAsync(
                cn, tx, objetivo.EmailId, objetivo.BucketBefore,
                objetivo.ConfidenceBefore ?? 0, ahora);
        }

        var ejemplos = await CountAsync(cn, tx);
        tx.Commit();

        log.LogInformation("Deshecha la correccion {Id} de {EmailId}: vuelve a {Bucket} ({Ejemplos} ejemplos)",
            objetivo.Id, objetivo.EmailId, objetivo.BucketBefore ?? "sin clasificar", ejemplos.Total);

        return new CorrectionResult(await emails.GetAsync(objetivo.EmailId, ahora), ejemplos.Total);
    }

    /// <summary>
    /// Ejemplos en vigor, por origen. Los deshaceres se descuentan de las correcciones
    /// del usuario: sin eso, deshacer SUBIRIA el contador que el front muestra en el
    /// toast justo despues de pulsar Deshacer.
    /// </summary>
    public async Task<ExampleCounts> CountAsync()
    {
        using var cn = db.Open();
        return await CountAsync(cn, null);
    }

    private static async Task<ExampleCounts> CountAsync(
        Microsoft.Data.Sqlite.SqliteConnection cn,
        Microsoft.Data.Sqlite.SqliteTransaction? tx)
    {
        // COUNT y no SUM: SUM sobre cero filas devuelve NULL, y cero filas es el estado
        // de toda instalacion nueva.
        var fila = await cn.QuerySingleAsync<FilaOrigenes>(
            """
            SELECT
              COUNT(CASE WHEN source = 'user'             THEN 1 END) AS [User],
              COUNT(CASE WHEN source = 'bootstrap_sent'   THEN 1 END) AS BootstrapSent,
              COUNT(CASE WHEN source = 'bootstrap_folder' THEN 1 END) AS BootstrapFolder,
              COUNT(CASE WHEN source = 'rule'             THEN 1 END) AS Rule,
              COUNT(CASE WHEN source = 'undo'             THEN 1 END) AS Undo
            FROM corrections
            """, transaction: tx);

        return new ExampleCounts(
            (int)Math.Max(0, fila.User - fila.Undo),
            (int)fila.BootstrapSent,
            (int)fila.BootstrapFolder,
            (int)fila.Rule);
    }

    private static Task EscribirClasificacionAsync(
        Microsoft.Data.Sqlite.SqliteConnection cn,
        Microsoft.Data.Sqlite.SqliteTransaction tx,
        string emailId,
        string bucket,
        double confianza,
        DateTimeOffset ahora)
    {
        // Las senales NO se tocan. Siguen siendo hechos ciertos sobre el correo y son
        // la explicacion de por que el sistema creyo otra cosa; borrarlas al corregir
        // dejaria al usuario sin saber en que se equivoco el modelo.
        return cn.ExecuteAsync(
            """
            INSERT INTO classifications (email_id, bucket, confidence, signals_json, model_version, computed_at)
            VALUES (@emailId, @bucket, @confianza, '[]', 0, @ahora)
            ON CONFLICT(email_id) DO UPDATE SET
                bucket = @bucket,
                confidence = @confianza,
                computed_at = @ahora
            """,
            new { emailId, bucket, confianza, ahora = ahora.ToString("O") }, tx);
    }

    private sealed record EstadoActual(string EmailId, string? Bucket, double? Confidence);

    private sealed record EventoCorreccion(
        long Id,
        string EmailId,
        string? BucketBefore,
        string BucketAfter,
        string Source,
        double? ConfidenceBefore);

    private sealed record FilaOrigenes(long User, long BootstrapSent, long BootstrapFolder, long Rule, long Undo);
}
