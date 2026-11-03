using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using LocalMailAI.Api.Api;
using LocalMailAI.Api.Classification;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace LocalMailAI.Api.Mail;

/// <summary>Resultado de ingerir un mensaje.</summary>
/// <param name="EmailId">Id estable del correo.</param>
/// <param name="Inserted">False si ya estaba (sync repetido).</param>
/// <param name="Bucket">Clasificacion provisional por senales.</param>
public sealed record IngestResult(string EmailId, bool Inserted, Bucket Bucket, double Confidence);

/// <summary>Fila de sender_stats. Dapper no mapea tuplas con nombres.</summary>
internal sealed record SenderStats(long Received, long Replied, long Opened);

/// <summary>
/// Etapas 2 a 6 del pipeline de BACKEND.md §5: parseo MIME, normalizacion, senales
/// deterministas y clasificacion provisional, todo escrito en la BD.
///
/// Esta separado del sync IMAP a proposito. El sync solo trae bytes; lo que se hace
/// con ellos se puede probar sin red, y la misma ruta sirve para la ingesta real y
/// para los ficheros .eml de prueba.
/// </summary>
public sealed class EmailIngestor(ILogger<EmailIngestor> log)
{
    /// <summary>
    /// Id estable: hash de Message-ID + cuenta (§4). Estable entre syncs, porque el
    /// UID de IMAP puede cambiar y el Message-ID no.
    /// </summary>
    public static string ComputeId(string accountId, string messageId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{accountId}|{messageId}"));
        return Convert.ToHexStringLower(hash)[..32];
    }

    public async Task<IngestResult> IngestAsync(
        SqliteConnection cn,
        string accountId,
        MimeMessage message,
        uint uid,
        string folder,
        long rawSize)
    {
        ArgumentNullException.ThrowIfNull(cn);
        ArgumentNullException.ThrowIfNull(message);

        var facts = MessageFacts.From(message);
        var id = ComputeId(accountId, facts.MessageId);

        // Cada mensaje en su propia transaccion: si uno de 5.000 esta roto, se pierde
        // ese y no la pasada entera. §5 pide que cada etapa sea reanudable.
        using var tx = cn.BeginTransaction();

        var yaEstaba = await cn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM emails WHERE id = @id", new { id }, tx) > 0;

        if (yaEstaba)
        {
            tx.Rollback();
            return new IngestResult(id, false, Bucket.Informativo, 0);
        }

        var cuerpo = BodyNormalizer.FromMessage(message);

        await cn.ExecuteAsync(
            """
            INSERT INTO emails (
                id, account_id, message_id, thread_id, uid, folder,
                from_name, from_email, to_json, cc_json, subject, date_utc, in_reply_to,
                has_list_unsubscribe, is_auto_submitted, spf_pass, dkim_pass, unread, raw_size)
            VALUES (
                @id, @accountId, @messageId, @threadId, @uid, @folder,
                @fromName, @fromEmail, @toJson, @ccJson, @subject, @dateUtc, @inReplyTo,
                @hasListUnsubscribe, @isAutoSubmitted, @spfPass, @dkimPass, @unread, @rawSize)
            """,
            new
            {
                id,
                accountId,
                messageId = facts.MessageId,
                threadId = facts.ThreadId,
                uid,
                folder,
                fromName = facts.FromName,
                fromEmail = facts.FromEmail,
                toJson = JsonSerializer.Serialize(facts.To),
                ccJson = JsonSerializer.Serialize(facts.Cc),
                subject = facts.Subject,
                dateUtc = facts.DateUtc.ToString("O"),
                inReplyTo = facts.InReplyTo,
                hasListUnsubscribe = facts.HasListUnsubscribe ? 1 : 0,
                isAutoSubmitted = facts.IsAutoSubmitted ? 1 : 0,
                spfPass = facts.SpfPass is null ? (int?)null : facts.SpfPass.Value ? 1 : 0,
                dkimPass = facts.DkimPass is null ? (int?)null : facts.DkimPass.Value ? 1 : 0,
                unread = 1,
                rawSize,
            }, tx);

        await cn.ExecuteAsync(
            """
            INSERT INTO email_text (email_id, body_clean, preview, token_count)
            VALUES (@id, @bodyClean, @preview, @tokenCount)
            """,
            new
            {
                id,
                bodyClean = cuerpo.Clean,
                preview = cuerpo.Preview,
                // El recuento real de tokens lo pone la etapa de embeddings; esto es
                // una estimacion para poder ordenar trabajo por coste antes de eso.
                tokenCount = cuerpo.Clean.Length / 4,
            }, tx);

        // email_fts es contentless: su rowid tiene que ser el de emails (ver el
        // comentario de 001_initial.sql sobre VACUUM).
        var rowid = await cn.ExecuteScalarAsync<long>(
            "SELECT rowid FROM emails WHERE id = @id", new { id }, tx);

        await cn.ExecuteAsync(
            """
            INSERT INTO email_fts (rowid, subject, body_clean, from_name, from_email)
            VALUES (@rowid, @subject, @bodyClean, @fromName, @fromEmail)
            """,
            new
            {
                rowid,
                subject = facts.Subject ?? string.Empty,
                bodyClean = cuerpo.Clean,
                fromName = facts.FromName ?? string.Empty,
                fromEmail = facts.FromEmail,
            }, tx);

        await cn.ExecuteAsync(
            """
            INSERT INTO sender_stats (account_id, from_email, received, replied, opened, last_seen)
            VALUES (@accountId, @fromEmail, 1, 0, 0, @lastSeen)
            ON CONFLICT(account_id, from_email) DO UPDATE SET
                received = received + 1,
                last_seen = MAX(COALESCE(last_seen, ''), @lastSeen)
            """,
            new { accountId, fromEmail = facts.FromEmail, lastSeen = facts.DateUtc.ToString("O") }, tx);

        var ctx = await ContextoAsync(cn, tx, accountId, id, facts, cuerpo.Clean);
        var senales = DeterministicSignals.Compute(facts, ctx);
        var clasificacion = SignalBaseline.Classify(senales, ctx);

        await cn.ExecuteAsync(
            """
            INSERT INTO classifications (email_id, bucket, confidence, signals_json, model_version, computed_at)
            VALUES (@id, @bucket, @confidence, @signalsJson, @modelVersion, @computedAt)
            """,
            new
            {
                id,
                bucket = clasificacion.Bucket.ToWire(),
                confidence = clasificacion.Confidence,
                signalsJson = JsonSerializer.Serialize(
                    clasificacion.Signals.Select(s => new SignalDto(s.Label, s.Sign))),
                // Version 0 = solo senales, sin clasificador entrenado. El hito 6
                // empieza a incrementarla.
                modelVersion = 0,
                computedAt = DateTimeOffset.UtcNow.ToString("O"),
            }, tx);

        tx.Commit();

        log.LogDebug("Ingerido {Id} de {From}: {Bucket} ({Confianza})",
            id, facts.FromEmail, clasificacion.Bucket.ToWire(), clasificacion.Confidence);

        return new IngestResult(id, true, clasificacion.Bucket, clasificacion.Confidence);
    }

    /// <summary>
    /// Reune el historial que necesitan las senales: estadisticas del remitente y
    /// estado del hilo. Todo sale de la BD, no del mensaje.
    ///
    /// Todas las consultas acotan por cuenta. El MVP gestiona un solo buzon, pero el
    /// esquema admite varios y una consulta sin acotar daria senales cruzadas entre
    /// bandejas: un bug que no se ve hasta que alguien anade la segunda cuenta.
    /// </summary>
    private static async Task<SignalContext> ContextoAsync(
        SqliteConnection cn,
        SqliteTransaction tx,
        string accountId,
        string emailId,
        MessageFacts facts,
        string cleanBody)
    {
        var cuenta = await cn.QuerySingleOrDefaultAsync<string?>(
            "SELECT email FROM accounts WHERE id = @accountId", new { accountId }, tx);

        var propias = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(cuenta))
        {
            propias.Add(cuenta.Trim().ToLowerInvariant());
        }

        var stats = await cn.QuerySingleOrDefaultAsync<SenderStats>(
            """
            SELECT received AS Received, replied AS Replied, opened AS Opened
            FROM sender_stats WHERE account_id = @accountId AND from_email = @fromEmail
            """,
            new { accountId, fromEmail = facts.FromEmail }, tx) ?? new SenderStats(0, 0, 0);

        // El correo que se esta ingiriendo ya ha sumado 1 en received, y para las
        // senales interesa el historial ANTERIOR: "le has respondido 12 veces" no
        // deberia cambiar por el hecho de recibir este.
        var recibidos = (int)Math.Max(0, stats.Received - 1);

        // Participacion en el hilo: algun mensaje del hilo salio de una direccion
        // propia. Requiere haber ingerido Enviados, que es justo lo que §7 pide para
        // el arranque en frio.
        string[] mios = propias.Count == 0 ? [] : [.. propias];

        var participo = mios.Length > 0 && await cn.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*) FROM emails
            WHERE account_id = @accountId
              AND thread_id = @threadId
              AND from_email IN @mios
              AND id <> @id
            """,
            new { accountId, threadId = facts.ThreadId, mios, id = emailId }, tx) > 0;

        // Mensajes seguidos del remitente despues de la ultima respuesta propia.
        var ultimaMia = mios.Length == 0 ? null : await cn.ExecuteScalarAsync<string?>(
            """
            SELECT MAX(date_utc) FROM emails
            WHERE account_id = @accountId
              AND thread_id = @threadId
              AND from_email IN @mios
            """,
            new { accountId, threadId = facts.ThreadId, mios }, tx);

        var seguidos = (int)await cn.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*) FROM emails
            WHERE account_id = @accountId
              AND thread_id = @threadId
              AND from_email = @fromEmail
              AND date_utc > COALESCE(@ultimaMia, '')
            """,
            new { accountId, threadId = facts.ThreadId, fromEmail = facts.FromEmail, ultimaMia }, tx);

        var dominio = propias.Count > 0 && propias.First().Contains('@')
            ? propias.First().Split('@')[1]
            : null;

        return new SignalContext
        {
            OwnAddresses = propias,
            OrgDomain = dominio,
            SenderReceived = recibidos,
            SenderReplied = (int)stats.Replied,
            SenderOpened = (int)stats.Opened,
            IParticipatesInThread = participo,
            ConsecutiveUnanswered = seguidos,
            CleanBody = cleanBody,
        };
    }
}
