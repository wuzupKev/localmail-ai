using Dapper;
using LocalMailAI.Api.Api;
using LocalMailAI.Api.Data;
using LocalMailAI.Api.Embeddings;
using Microsoft.Extensions.Configuration;
using LocalMailAI.Api.Classification;
using LocalMailAI.Api.Mail;
using LocalMailAI.Api.Model;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace LocalMailAI.Tests;

/// <summary>
/// The whole chain against a real database: parsing, normalisation, signals,
/// classification, writing and reading back through the API.
///
/// This is milestones 1 to 4 together, which is the only way to check that they fit:
/// each piece can be correct on its own and the join still be wrong.
/// </summary>
public sealed class IngestionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"localmail-test-{Guid.NewGuid():N}.db");
    private readonly Database _db;
    private readonly EmailIngestor _ingestor = new(NullLogger<EmailIngestor>.Instance);
    private readonly EmailRepository _repo;
    private readonly CorrectionsRepository _corrections;

    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    public IngestionTests()
    {
        _db = new Database(_path);
        _repo = new EmailRepository(_db);
        _corrections = new CorrectionsRepository(_db, _repo, NullLogger<CorrectionsRepository>.Instance);

        using var cn = _db.Open();
        new Migrator(NullLogger<Migrator>.Instance).Migrate(cn);

        cn.Execute(
            """
            INSERT INTO accounts (id, email, imap_host, imap_port, credential_key, created_at)
            VALUES ('cuenta', 'kevin@sagetasolutions.com', 'localhost', 993, 'key', '2026-11-01T00:00:00Z')
            """);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(f))
            {
                File.Delete(f);
            }
        }
    }

    [Fact]
    public async Task A_personal_message_goes_through_the_whole_pipeline()
    {
        var id = await Ingest(
            """
            From: Ana Molina <ana.molina@estudiolegal.es>
            To: kevin@sagetasolutions.com
            Subject: Re: Confirmacion reunion del jueves
            Message-ID: <ana1@estudiolegal.es>
            Date: Tue, 3 Nov 2026 09:14:00 +0000
            Authentication-Results: mx.google.com; dkim=pass; spf=pass
            Content-Type: text/plain; charset=utf-8

            Hola Kevin,

            ¿Te viene bien a las 10 en lugar de las 9?

            Un saludo,
            Ana

            El 1 de noviembre de 2026, Kevin <kevin@sagetasolutions.com> escribió:
            > Te confirmo el jueves a las 9.
            """);

        var email = await _repo.GetAsync(id, Now);

        Assert.NotNull(email);
        Assert.Equal("Ana Molina", email.From.Name);
        Assert.Equal("ana.molina@estudiolegal.es", email.From.Email);

        // The quote is gone and the accents survived the round trip through SQLite.
        Assert.DoesNotContain("Te confirmo el jueves a las 9", email.Preview);
        Assert.Contains("¿Te viene bien a las 10", email.Preview);
        Assert.All(email.Body, p => Assert.DoesNotContain(">", p));

        Assert.Equal(3, email.Body.Length);
        // Same day: it has to produce a time, not "Ayer" or a weekday name. The exact
        // value is not pinned because the .eml is in UTC and the API formats in local
        // time; pinning it would fail on a machine in another timezone.
        Assert.Matches(@"^\d{1,2}:\d{2}$", email.Time);
        Assert.True(email.Unread);
        Assert.Equal(1, email.ThreadCount);

        // No LLM yet: neither optional property is present (milestone 9).
        Assert.Null(email.Extracted);
        Assert.Null(email.Summary);

        Assert.Contains(email.Signals, s => s.Label == "You are on To:, not Cc:" && s.Sign == "pos");
        Assert.Contains(email.Signals, s => s.Label == "Verified sender (DKIM + SPF)");
    }

    [Fact]
    public async Task Ingesting_the_same_message_twice_does_not_duplicate_it()
    {
        // A repeated sync is the normal case, not the exception.
        const string eml =
            """
            From: Ana <ana@estudiolegal.es>
            To: kevin@sagetasolutions.com
            Subject: Reunion
            Message-ID: <repetido@estudiolegal.es>
            Date: Tue, 3 Nov 2026 09:14:00 +0000

            Cuerpo
            """;

        using var cn = _db.Open();
        var firstRun = await _ingestor.IngestAsync(cn, "cuenta", Load(eml), 1, "INBOX", 100);
        var secondRun = await _ingestor.IngestAsync(cn, "cuenta", Load(eml), 1, "INBOX", 100);

        Assert.True(firstRun.Inserted);
        Assert.False(secondRun.Inserted);
        Assert.Equal(firstRun.EmailId, secondRun.EmailId);
        Assert.Equal(1, await cn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM emails"));

        // And sender_stats did not count the message twice.
        Assert.Equal(1, await cn.ExecuteScalarAsync<long>(
            "SELECT received FROM sender_stats WHERE from_email = 'ana@estudiolegal.es'"));
    }

    [Fact]
    public async Task The_thread_counts_its_messages_and_detects_insistence()
    {
        // Three consecutive messages from the same sender with no reply: §6 calls this
        // "Third message with no reply from you" and it carries the heaviest weight.
        for (var i = 1; i <= 3; i++)
        {
            await Ingest(
                $"""
                From: Pablo Esteban <pablo@clientemayor.com>
                To: kevin@sagetasolutions.com
                Subject: Re: Incidencia en produccion
                Message-ID: <pablo{i}@clientemayor.com>
                References: <raiz-incidencia@clientemayor.com>
                Date: Tue, 3 Nov 2026 0{i}:00:00 +0000

                ¿Hay alguna novedad sobre la incidencia?
                """);
        }

        var page = await _repo.ListAsync(null, 50, null, Now);
        var last = page.Items[0];

        Assert.Equal(3, last.ThreadCount);
        Assert.Contains(last.Signals, s => s.Label == "Third message with no reply from you");
        Assert.Equal("reply", last.Bucket);
    }

    [Fact]
    public async Task An_html_newsletter_loses_its_markup_and_stays_out_of_responder()
    {
        var id = await Ingest(
            """
            From: Marketing Zentrix <newsletter@zentrix.io>
            To: kevin@sagetasolutions.com
            Subject: 7 formas de multiplicar tu productividad
            Message-ID: <zentrix1@zentrix.io>
            Date: Tue, 3 Nov 2026 05:30:00 +0000
            List-Unsubscribe: <mailto:unsub@zentrix.io>
            Precedence: bulk
            Content-Type: text/html; charset=utf-8

            <html><body><style>.cta{color:red}</style>
            <h1>7 formas</h1><p>Descubre las herramientas que están cambiando el trabajo.</p>
            </body></html>
            """);

        var email = await _repo.GetAsync(id, Now);

        Assert.NotNull(email);
        Assert.DoesNotContain("<", email.Preview);
        Assert.DoesNotContain("color:red", string.Join(' ', email.Body));
        Assert.Contains("Descubre las herramientas", string.Join(' ', email.Body));
        Assert.NotEqual("reply", email.Bucket);
        Assert.Contains(email.Signals, s => s.Label == "List-Unsubscribe present" && s.Sign == "neg");
    }

    [Fact]
    public async Task The_text_index_is_populated_and_searches_without_accents()
    {
        await Ingest(
            """
            From: Facturacion <facturacion@nordia-cloud.com>
            To: kevin@sagetasolutions.com
            Subject: Aviso de vencimiento - factura F-2291
            Message-ID: <nordia1@nordia-cloud.com>
            Date: Tue, 3 Nov 2026 08:47:00 +0000
            Content-Type: text/plain; charset=utf-8

            La factura F-2291 vence el 10/11/2026. Puede pagarla desde su área de cliente.
            """);

        using var cn = _db.Open();

        // The index rowid has to be the one from emails, or search cannot return the
        // message at all (email_fts is contentless).
        var found = await cn.QuerySingleOrDefaultAsync<string>(
            """
            SELECT e.id FROM email_fts f
            JOIN emails e ON e.rowid = f.rowid
            WHERE email_fts MATCH 'factura'
            """);

        Assert.NotNull(found);

        // remove_diacritics 2: searching "area" finds "área".
        var withoutAccent = await cn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM email_fts WHERE email_fts MATCH 'area'");

        Assert.Equal(1, withoutAccent);
    }

    [Fact]
    public async Task Paging_neither_repeats_nor_skips_messages()
    {
        for (var i = 1; i <= 7; i++)
        {
            await Ingest(
                $"""
                From: Remitente {i} <r{i}@ejemplo.com>
                To: kevin@sagetasolutions.com
                Subject: Message {i}
                Message-ID: <p{i}@ejemplo.com>
                Date: Tue, 3 Nov 2026 0{i}:00:00 +0000

                Cuerpo {i}
                """);
        }

        var seen = new List<string>();
        string? cursor = null;

        do
        {
            var page = await _repo.ListAsync(null, 3, cursor, Now);
            seen.AddRange(page.Items.Select(e => e.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(7, seen.Count);
        Assert.Equal(7, seen.Distinct().Count());
    }

    [Fact]
    public async Task Filtering_by_bucket_returns_only_that_bucket()
    {
        await Ingest(
            """
            From: Docker Hub <noreply@docker.com>
            To: kevin@sagetasolutions.com
            Subject: Limite de pulls
            Message-ID: <docker1@docker.com>
            Date: Tue, 3 Nov 2026 06:00:00 +0000
            List-Unsubscribe: <https://docker.com/unsub>

            Has usado 180 de 200 pulls.
            """);

        await Ingest(
            """
            From: Diego <d.ferran@sagetasolutions.com>
            To: kevin@sagetasolutions.com
            Subject: Numeros del Q3
            Message-ID: <diego1@sagetasolutions.com>
            Date: Tue, 3 Nov 2026 07:00:00 +0000

            ¿Puedes revisar los numeros del Q3?
            """);

        var responder = await _repo.ListAsync("reply", 50, null, Now);

        Assert.All(responder.Items, e => Assert.Equal("reply", e.Bucket));
        Assert.Contains(responder.Items, e => e.From.Email == "d.ferran@sagetasolutions.com");
        Assert.DoesNotContain(responder.Items, e => e.From.Email == "noreply@docker.com");
    }

    [Fact]
    public async Task A_message_with_no_body_is_still_served()
    {
        // Never fail on odd mail (§11). It reaches the inbox with whatever it has.
        var id = await Ingest(
            """
            From: Vercel <no-reply@vercel.com>
            To: kevin@sagetasolutions.com
            Subject: Despliegue correcto
            Message-ID: <vercel-vacio@vercel.com>
            Date: Tue, 3 Nov 2026 06:12:00 +0000
            Content-Type: text/plain; charset=utf-8


            """);

        var email = await _repo.GetAsync(id, Now);

        Assert.NotNull(email);
        Assert.Empty(email.Body);
        Assert.Equal(string.Empty, email.Preview);
        Assert.Equal("Despliegue correcto", email.Subject);
    }

    [Fact]
    public async Task The_thread_does_not_count_messages_from_another_account()
    {
        // The MVP handles a single mailbox, but the schema allows several and the
        // queries have to be scoped by account. Without this, a thread reaching two of
        // your addresses would report twice the messages, and the "you participate in
        // this thread" and "third unanswered message" signals would bleed across
        // inboxes. It is a bug nobody sees until a second account is added.
        using var cn = _db.Open();
        await cn.ExecuteAsync(
            """
            INSERT INTO accounts (id, email, imap_host, imap_port, credential_key, created_at)
            VALUES ('otra', 'kevin@personal.com', 'localhost', 993, 'clave2', '2026-11-01T00:00:00Z')
            """);

        const string eml =
            """
            From: Ana <ana@estudiolegal.es>
            To: kevin@sagetasolutions.com
            Subject: Re: Reunion
            Message-ID: <compartido@estudiolegal.es>
            References: <raiz-compartida@estudiolegal.es>
            Date: Tue, 3 Nov 2026 09:14:00 +0000

            El mismo email en los dos buzones.
            """;

        var inAccount = await _ingestor.IngestAsync(cn, "cuenta", Load(eml), 1, "INBOX", 100);
        var inOther = await _ingestor.IngestAsync(cn, "otra", Load(eml), 1, "INBOX", 100);

        // Two distinct rows: the id is hashed together with the account.
        Assert.NotEqual(inAccount.EmailId, inOther.EmailId);
        Assert.Equal(2, await cn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM emails"));

        // But each one sees its thread with ONE message, not two.
        var one = await _repo.GetAsync(inAccount.EmailId, Now);
        var other = await _repo.GetAsync(inOther.EmailId, Now);

        Assert.NotNull(one);
        Assert.NotNull(other);
        Assert.Equal(1, one.ThreadCount);
        Assert.Equal(1, other.ThreadCount);
    }

    // --- Tile counts and read state --------------------------------------------

    [Fact]
    public async Task The_counts_match_the_lists_they_open()
    {
        // The invariant the frontend's main screen depends on: the five tiles are the
        // navigation, so a tile's number has to be exactly the number of messages that
        // appear when it is pressed. If the threshold rule were applied in two
        // different places, this breaks silently.
        await SeedVariety();

        var c = await _repo.CountsAsync();

        foreach (var (bucket, expected) in new[]
        {
            ("reply", c.Reply),
            ("fyi", c.Fyi),
            ("automated", c.Automated),
            ("noise", c.Noise),
            ("unsure", c.Unsure),
        })
        {
            var list = await _repo.ListAsync(bucket, 200, null, Now);
            Assert.Equal(expected, list.Items.Length);
        }

        // And the five buckets partition every message, with no overlaps or gaps.
        using var cn = _db.Open();
        var total = (int)await cn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM emails");

        Assert.Equal(total, c.Reply + c.Fyi + c.Automated + c.Noise + c.Unsure);
    }

    [Fact]
    public async Task A_low_confidence_message_leaves_its_bucket_for_doubt()
    {
        // Unknown sender: the baseline gives 0.50, below the threshold. It must not
        // appear under "reply" even though that is its computed bucket, because
        // the frontend paints it in the doubt bucket and it would show up twice.
        var id = await Ingest(
            """
            From: Desconocido <added@proveedor-added.com>
            To: kevin@sagetasolutions.com
            Subject: Su solicitud ha sido registrada
            Message-ID: <dudoso@proveedor-added.com>
            Date: Tue, 3 Nov 2026 10:05:00 +0000

            Hemos recibido su solicitud.
            """);

        var email = await _repo.GetAsync(id, Now);
        Assert.NotNull(email);
        Assert.True(email.Confidence < 0.55, $"confianza {email.Confidence}");

        var inDoubt = await _repo.ListAsync("unsure", 50, null, Now);
        Assert.Contains(inDoubt.Items, e => e.Id == id);

        var inItsBucket = await _repo.ListAsync(email.Bucket, 50, null, Now);
        Assert.DoesNotContain(inItsBucket.Items, e => e.Id == id);
    }

    [Fact]
    public async Task Marking_read_returns_the_message_and_feeds_the_open_signal()
    {
        // Opening a message increments sender_stats.opened, which is what separates
        // "automated" from "noise" (§6). It is not bookkeeping: it is the signal.
        var id = await Ingest(
            """
            From: Vercel <no-reply@vercel.com>
            To: kevin@sagetasolutions.com
            Subject: Despliegue correcto
            Message-ID: <abrir@vercel.com>
            Date: Tue, 3 Nov 2026 06:12:00 +0000

            Tu despliegue se ha completado.
            """);

        var before = await _repo.GetAsync(id, Now);
        Assert.True(before!.Unread);

        var after = await _repo.SetUnreadAsync(id, false, Now);

        Assert.NotNull(after);
        Assert.False(after.Unread);
        Assert.Equal(id, after.Id);

        using var cn = _db.Open();
        Assert.Equal(1, await cn.ExecuteScalarAsync<long>(
            "SELECT opened FROM sender_stats WHERE from_email = 'no-reply@vercel.com'"));
    }

    [Fact]
    public async Task Opening_the_same_message_twice_counts_one_open()
    {
        // Opening the same message ten times does not make it ten times more interesting.
        var id = await Ingest(
            """
            From: Jira <jira@sageta.atlassian.net>
            To: kevin@sagetasolutions.com
            Subject: SAG-1182
            Message-ID: <doble@sageta.atlassian.net>
            Date: Tue, 3 Nov 2026 06:40:00 +0000

            Cambio de status.
            """);

        await _repo.SetUnreadAsync(id, false, Now);
        await _repo.SetUnreadAsync(id, false, Now);

        using var cn = _db.Open();
        Assert.Equal(1, await cn.ExecuteScalarAsync<long>(
            "SELECT opened FROM sender_stats WHERE from_email = 'jira@sageta.atlassian.net'"));

        // Marking unread and opening again does count: that is a new open.
        await _repo.SetUnreadAsync(id, true, Now);
        await _repo.SetUnreadAsync(id, false, Now);

        Assert.Equal(2, await cn.ExecuteScalarAsync<long>(
            "SELECT opened FROM sender_stats WHERE from_email = 'jira@sageta.atlassian.net'"));
    }

    [Fact]
    public async Task The_unread_count_follows_the_opens()
    {
        await SeedVariety();

        var initial = await _repo.CountsAsync();
        Assert.True(initial.Unread > 0);

        var first = (await _repo.ListAsync(null, 1, null, Now)).Items[0];
        await _repo.SetUnreadAsync(first.Id, false, Now);

        Assert.Equal(initial.Unread - 1, (await _repo.CountsAsync()).Unread);
    }

    [Fact]
    public async Task Asking_for_a_nonexistent_bucket_returns_nothing_rather_than_failing()
    {
        await SeedVariety();
        var list = await _repo.ListAsync("inventado", 50, null, Now);
        Assert.Empty(list.Items);
    }

    /// <summary>A handful of messages that land in different buckets.</summary>
    private async Task SeedVariety()
    {
        await Ingest(
            """
            From: Diego <d.ferran@sagetasolutions.com>
            To: kevin@sagetasolutions.com
            Subject: Numeros del Q3
            Message-ID: <v1@sagetasolutions.com>
            Date: Tue, 3 Nov 2026 07:00:00 +0000

            ¿Puedes revisar los numeros del Q3?
            """);

        await Ingest(
            """
            From: Zentrix <newsletter@zentrix.io>
            To: kevin@sagetasolutions.com
            Subject: Oferta del mes
            Message-ID: <v2@zentrix.io>
            Date: Tue, 3 Nov 2026 05:30:00 +0000
            List-Unsubscribe: <mailto:unsub@zentrix.io>

            Descubre las herramientas.
            """);

        await Ingest(
            """
            From: Marta <marta.ruiz@universidadsur.edu>
            To: comite@universidadsur.edu
            Cc: kevin@sagetasolutions.com
            Subject: Acta del comite
            Message-ID: <v3@universidadsur.edu>
            Date: Tue, 3 Nov 2026 11:30:00 +0000

            No requiere accion por vuestra parte.
            """);

        await Ingest(
            """
            From: Desconocido <other@sin-historial.com>
            To: kevin@sagetasolutions.com
            Subject: Solicitud registrada
            Message-ID: <v4@sin-historial.com>
            Date: Tue, 3 Nov 2026 10:05:00 +0000

            Hemos recibido su solicitud.
            """);
    }

    // --- Model status ----------------------------------------------------------

    [Fact]
    public async Task The_model_status_breaks_examples_down_by_origin()
    {
        // The frontend needs this to distinguish "I don't know you yet" (few examples,
        // and the ones there came from the bootstrap) from "I can't classify this".
        var id = await Ingest(
            """
            From: Ana <ana@estudiolegal.es>
            To: kevin@sagetasolutions.com
            Subject: Reunion
            Message-ID: <estado1@estudiolegal.es>
            Date: Tue, 3 Nov 2026 09:14:00 +0000

            Cuerpo
            """);

        using var cn = _db.Open();
        await cn.ExecuteAsync(
            """
            INSERT INTO corrections (email_id, bucket_before, bucket_after, source, created_at) VALUES
              (@id, 'fyi', 'reply', 'user',             '2026-11-03T10:00:00Z'),
              (@id, 'fyi', 'reply', 'user',             '2026-11-03T10:01:00Z'),
              (@id, NULL,          'reply', 'bootstrap_sent',   '2026-11-03T09:00:00Z'),
              (@id, NULL,          'noise',     'bootstrap_folder', '2026-11-03T09:00:00Z'),
              (@id, NULL,          'noise',     'rule',             '2026-11-03T09:00:00Z')
            """, new { id });

        var status = await new ModelStatusService(_db, _corrections, WithoutEmbeddings(), NullLogger<ModelStatusService>.Instance).GetAsync();

        Assert.Equal(2, status.ExamplesBySource.User);
        Assert.Equal(1, status.ExamplesBySource.BootstrapSent);
        Assert.Equal(1, status.ExamplesBySource.BootstrapFolder);
        Assert.Equal(1, status.ExamplesBySource.Rule);
        Assert.Equal(5, status.Examples);
        Assert.True(status.BootstrapRan);
        Assert.Equal(1, status.IndexedEmails);
    }

    [Fact]
    public async Task An_undo_does_not_raise_the_example_counter()
    {
        // Undo inserts an inverse event instead of deleting the row (§1, rule 4). If
        // the counter did not subtract it, undoing a correction would RAISE the example
        // count, which is the opposite of what the user just asked for.
        var id = await Ingest(
            """
            From: Ana <ana@estudiolegal.es>
            To: kevin@sagetasolutions.com
            Subject: Reunion
            Message-ID: <deshacer@estudiolegal.es>
            Date: Tue, 3 Nov 2026 09:14:00 +0000

            Cuerpo
            """);

        var service = new ModelStatusService(_db, _corrections, WithoutEmbeddings(), NullLogger<ModelStatusService>.Instance);
        using var cn = _db.Open();

        await cn.ExecuteAsync(
            """
            INSERT INTO corrections (email_id, bucket_before, bucket_after, source, created_at)
            VALUES (@id, 'fyi', 'reply', 'user', '2026-11-03T10:00:00Z')
            """, new { id });

        Assert.Equal(1, (await service.GetAsync()).Examples);

        await cn.ExecuteAsync(
            """
            INSERT INTO corrections (email_id, bucket_before, bucket_after, source, created_at)
            VALUES (@id, 'reply', 'fyi', 'undo', '2026-11-03T10:00:30Z')
            """, new { id });

        var afterUndo = await service.GetAsync();
        Assert.Equal(0, afterUndo.Examples);
        Assert.Equal(0, afterUndo.ExamplesBySource.User);
    }

    [Fact]
    public async Task Precision_is_null_until_an_evaluation_set_exists()
    {
        // The spec shows 0.91 as an example, but that number can only come from
        // measuring against 250 hand-labelled messages. Returning something invented
        // would put a false figure in front of the user.
        var status = await new ModelStatusService(_db, _corrections, WithoutEmbeddings(), NullLogger<ModelStatusService>.Instance).GetAsync();

        Assert.Null(status.Precision);
        Assert.Null(status.LastTrained);
        Assert.Equal(0, status.Examples);
        Assert.False(status.BootstrapRan);
        Assert.Equal("multilingual-e5-small (ONNX, 118M)", status.Embeddings);
    }

    [Fact]
    public async Task An_empty_inbox_returns_zeros_and_does_not_fail()
    {
        // This is the state of every fresh installation, before the first sync. Both
        // aggregate queries used SUM, which returns NULL rather than 0 over zero rows,
        // so both endpoints failed on exactly the first startup.
        var c = await _repo.CountsAsync();

        Assert.Equal(0, c.Reply);
        Assert.Equal(0, c.Fyi);
        Assert.Equal(0, c.Automated);
        Assert.Equal(0, c.Noise);
        Assert.Equal(0, c.Unsure);
        Assert.Equal(0, c.Unread);

        var list = await _repo.ListAsync(null, 50, null, Now);
        Assert.Empty(list.Items);
        Assert.Null(list.NextCursor);
    }

    // --- Corrections and undo ---------------------------------------------------

    [Fact]
    public async Task Correcting_moves_the_message_to_the_chosen_bucket_and_out_of_doubt()
    {
        // What actually has to happen on a correction: the message shows up where the
        // user said. If confidence stayed at the model's value, a corrected
        // low-confidence message would still appear under "not sure" and the
        // correction would look like it did nothing.
        var id = await Ingest(Message("dudoso"));

        var before = await _repo.GetAsync(id, Now);
        Assert.True(before!.Confidence < 0.55, $"confianza initial {before.Confidence}");
        Assert.Contains((await _repo.ListAsync("unsure", 50, null, Now)).Items, e => e.Id == id);

        var r = await _corrections.ApplyAsync(id, Bucket.Fyi, Now);

        Assert.NotNull(r);
        Assert.NotNull(r.Email);
        Assert.Equal("fyi", r.Email.Bucket);
        Assert.Equal(1.0, r.Email.Confidence);
        Assert.Equal(1, r.Examples);

        Assert.Contains((await _repo.ListAsync("fyi", 50, null, Now)).Items, e => e.Id == id);
        Assert.DoesNotContain((await _repo.ListAsync("unsure", 50, null, Now)).Items, e => e.Id == id);
    }

    [Fact]
    public async Task Correcting_keeps_the_signals_that_explain_the_models_mistake()
    {
        // The signals remain true facts about the message. Erasing them on correction
        // would leave the user unable to see what the system got wrong.
        var id = await Ingest(
            """
            From: Zentrix <newsletter@zentrix.io>
            To: kevin@sagetasolutions.com
            Subject: Oferta
            Message-ID: <signals@zentrix.io>
            Date: Tue, 3 Nov 2026 05:30:00 +0000
            List-Unsubscribe: <mailto:unsub@zentrix.io>

            Descubre las herramientas.
            """);

        var signalsBefore = (await _repo.GetAsync(id, Now))!.Signals.Length;
        Assert.True(signalsBefore > 0);

        var r = await _corrections.ApplyAsync(id, Bucket.Reply, Now);

        Assert.Equal(signalsBefore, r!.Email!.Signals.Length);
        Assert.Contains(r.Email.Signals, x => x.Label == "List-Unsubscribe present");
    }

    [Fact]
    public async Task Undo_restores_both_the_bucket_and_the_confidence()
    {
        // Restoring only the bucket is not undoing: a message that was under "not sure"
        // would reappear asserted in a bucket. That is why migration 002 stores
        // confidence_before.
        var id = await Ingest(Message("volver"));

        var original = await _repo.GetAsync(id, Now);
        var originalBucket = original!.Bucket;
        var originalConfidence = original.Confidence;

        await _corrections.ApplyAsync(id, Bucket.Noise, Now);
        Assert.Equal("noise", (await _repo.GetAsync(id, Now))!.Bucket);

        var r = await _corrections.UndoAsync(Now);

        Assert.NotNull(r.Email);
        Assert.Equal(originalBucket, r.Email.Bucket);
        Assert.Equal(originalConfidence, r.Email.Confidence);
        Assert.Equal(0, r.Examples);

        // And it is back in the doubt bucket, where it was.
        Assert.Contains((await _repo.ListAsync("unsure", 50, null, Now)).Items, e => e.Id == id);
    }

    [Fact]
    public async Task Undo_with_nothing_to_undo_is_not_an_error()
    {
        // The frontend's Ctrl+Z does not have to track whether a stack remains.
        var r = await _corrections.UndoAsync(Now);

        Assert.Null(r.Email);
        Assert.Equal(0, r.Examples);
    }

    [Fact]
    public async Task Undo_always_targets_the_latest_correction_still_in_effect()
    {
        // The case that breaks the naive version (count undos and subtract): correct A,
        // correct B, undo B, correct C, undo -> it has to undo C.
        var a = await Ingest(Message("a"));
        var b = await Ingest(Message("b"));
        var c = await Ingest(Message("c"));

        await _corrections.ApplyAsync(a, Bucket.Noise, Now);
        await _corrections.ApplyAsync(b, Bucket.Noise, Now);

        var undoneB = await _corrections.UndoAsync(Now);
        Assert.Equal(b, undoneB.Email!.Id);

        await _corrections.ApplyAsync(c, Bucket.Noise, Now);

        var undoneC = await _corrections.UndoAsync(Now);
        Assert.Equal(c, undoneC.Email!.Id);

        // A is still corrected: nobody touched it.
        Assert.Equal("noise", (await _repo.GetAsync(a, Now))!.Bucket);
        Assert.Equal(1, undoneC.Examples);

        // And the next undo does reach A.
        var undoneA = await _corrections.UndoAsync(Now);
        Assert.Equal(a, undoneA.Email!.Id);
        Assert.Equal(0, undoneA.Examples);
    }

    [Fact]
    public async Task The_correction_history_is_never_deleted()
    {
        // Rule 4 of §1: corrections are history, not state. Without it there is no
        // retraining from scratch and no way to measure whether the model improves.
        var id = await Ingest(Message("historial"));

        await _corrections.ApplyAsync(id, Bucket.Noise, Now);
        await _corrections.UndoAsync(Now);
        await _corrections.ApplyAsync(id, Bucket.Automated, Now);

        using var cn = _db.Open();
        var origins = (await cn.QueryAsync<string>(
            "SELECT source FROM corrections ORDER BY id")).ToList();

        Assert.Equal(new[] { "user", "undo", "user" }, origins);
        Assert.Equal(1, (await _corrections.CountAsync()).Total);
    }

    [Fact]
    public async Task Correcting_a_nonexistent_message_returns_null()
    {
        Assert.Null(await _corrections.ApplyAsync("noexiste", Bucket.Reply, Now));
    }

    /// <summary>A minimal message from an unknown sender, which lands in doubt.</summary>
    private static string Message(string key) =>
        $"""
        From: Remitente {key} <{key}@ejemplo.com>
        To: kevin@sagetasolutions.com
        Subject: Message {key}
        Message-ID: <{key}@ejemplo.com>
        Date: Tue, 3 Nov 2026 08:00:00 +0000

        Cuerpo de {key}.
        """;

    [Fact]
    public void An_empty_undo_sends_an_explicit_null_email_rather_than_omitting_it()
    {
        // The frontend distinguishes `email?: Email` from `email: Email | null`, which
        // are two different checks in TypeScript. Here null IS the answer - "there was
        // nothing to undo" - not a missing field, so it has to travel. The serializer
        // options are the ones Program.cs configures.
        var opciones = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(new CorrectionResult(null, 0), opciones);

        Assert.Contains("\"email\":null", json);
        Assert.Contains("\"examples\":0", json);
    }

    /// <summary>
    /// An embedding provider pointing at a directory that does not exist. Tests must
    /// not load 470 MB of model, and this exercises the degradation path as a side
    /// effect: without embeddings the rest of the system has to keep working.
    /// </summary>
    private static EmbeddingProvider WithoutEmbeddings() =>
        new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Models:Path"] = "carpeta-que-no-existe" })
                .Build(),
            NullLogger<EmbeddingProvider>.Instance,
            NullLogger<E5Embedder>.Instance);

    private async Task<string> Ingest(string eml)
    {
        using var cn = _db.Open();
        var r = await _ingestor.IngestAsync(cn, "cuenta", Load(eml), 1, "INBOX", eml.Length);
        return r.EmailId;
    }

    private static MimeMessage Load(string eml)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(eml.ReplaceLineEndings("\r\n"));
        using var ms = new MemoryStream(bytes);
        return MimeMessage.Load(ms);
    }
}

/// <summary>The contract's <c>time</c> field (§3).</summary>
public class RelativeTimeTests
{
    /// <summary>
    /// An instant built in LOCAL time. The backend runs on the user's own machine -
    /// it only listens on localhost - so its local time is theirs; building the cases
    /// in UTC would make the result depend on the timezone.
    /// </summary>
    private static DateTimeOffset Local(int day, int hour, int minute)
    {
        var unspecified = new DateTime(2026, 11, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, TimeZoneInfo.Local.GetUtcOffset(unspecified));
    }

    private static readonly DateTimeOffset Now = Local(3, 12, 0);

    [Fact]
    public void Today_shows_the_time()
    {
        Assert.Equal("9:14", RelativeTime.Format(Local(3, 9, 14), Now));
    }

    [Fact]
    public void Yesterday_is_spelled_out()
    {
        var cuando = Now.AddDays(-1);
        Assert.Equal("Ayer", RelativeTime.Format(cuando, Now));
    }

    [Fact]
    public void Within_the_week_the_weekday_name_is_used()
    {
        // 2026-11-03 is a Tuesday; three days earlier is a Saturday.
        var cuando = Now.AddDays(-3);
        Assert.Equal("Sáb", RelativeTime.Format(cuando, Now));
    }

    [Fact]
    public void Older_than_a_week_shows_day_and_abbreviated_month()
    {
        var cuando = Now.AddDays(-22);
        Assert.Equal("12 oct", RelativeTime.Format(cuando, Now));
    }

    [Fact]
    public void The_format_does_not_depend_on_the_machine_culture()
    {
        // With the system culture, the same API would return "Mon" or "Mo" depending
        // on where the backend runs, and the frontend would not know what to expect.
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("en-US");
            Assert.Equal("Ayer", RelativeTime.Format(Now.AddDays(-1), Now));
            Assert.Equal("Sáb", RelativeTime.Format(Now.AddDays(-3), Now));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
