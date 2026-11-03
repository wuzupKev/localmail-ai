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
/// La cadena completa contra una base de datos de verdad: parseo, normalizacion,
/// senales, clasificacion, escritura y lectura por la API.
///
/// Son los hitos 1 a 4 juntos, que es la unica forma de comprobar que encajan: cada
/// pieza por separado puede estar bien y la union estar mal.
/// </summary>
public sealed class IngestionTests : IDisposable
{
    private readonly string _ruta = Path.Combine(Path.GetTempPath(), $"localmail-test-{Guid.NewGuid():N}.db");
    private readonly Database _db;
    private readonly EmailIngestor _ingestor = new(NullLogger<EmailIngestor>.Instance);
    private readonly EmailRepository _repo;
    private readonly CorrectionsRepository _correcciones;

    private static readonly DateTimeOffset Ahora = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    public IngestionTests()
    {
        _db = new Database(_ruta);
        _repo = new EmailRepository(_db);
        _correcciones = new CorrectionsRepository(_db, _repo, NullLogger<CorrectionsRepository>.Instance);

        using var cn = _db.Open();
        new Migrator(NullLogger<Migrator>.Instance).Migrate(cn);

        cn.Execute(
            """
            INSERT INTO accounts (id, email, imap_host, imap_port, credential_key, created_at)
            VALUES ('cuenta', 'kevin@sagetasolutions.com', 'localhost', 993, 'clave', '2026-11-01T00:00:00Z')
            """);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _ruta, _ruta + "-wal", _ruta + "-shm" })
        {
            if (File.Exists(f))
            {
                File.Delete(f);
            }
        }
    }

    [Fact]
    public async Task Un_correo_personal_recorre_el_pipeline_entero()
    {
        var id = await Ingerir(
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

        var correo = await _repo.GetAsync(id, Ahora);

        Assert.NotNull(correo);
        Assert.Equal("Ana Molina", correo.From.Name);
        Assert.Equal("ana.molina@estudiolegal.es", correo.From.Email);

        // La cita se ha ido y los acentos han sobrevivido al viaje por SQLite.
        Assert.DoesNotContain("Te confirmo el jueves a las 9", correo.Preview);
        Assert.Contains("¿Te viene bien a las 10", correo.Preview);
        Assert.All(correo.Body, p => Assert.DoesNotContain(">", p));

        Assert.Equal(3, correo.Body.Length);
        // Mismo dia: tiene que salir una hora, no "Ayer" ni un dia de la semana.
        // No se fija el valor porque el .eml va en UTC y la API formatea en hora
        // local: fijarlo haria que el test fallara en una maquina de otro huso.
        Assert.Matches(@"^\d{1,2}:\d{2}$", correo.Time);
        Assert.True(correo.Unread);
        Assert.Equal(1, correo.ThreadCount);

        // Sin LLM todavia: las dos propiedades opcionales no vienen (hito 9).
        Assert.Null(correo.Extracted);
        Assert.Null(correo.Summary);

        Assert.Contains(correo.Signals, s => s.Label == "Vas en Para:, no en Cc:" && s.Sign == "pos");
        Assert.Contains(correo.Signals, s => s.Label == "Remitente verificado (DKIM + SPF)");
    }

    [Fact]
    public async Task Ingerir_dos_veces_el_mismo_correo_no_lo_duplica()
    {
        // Un sync que se repite es lo normal, no la excepcion.
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
        var primera = await _ingestor.IngestAsync(cn, "cuenta", Cargar(eml), 1, "INBOX", 100);
        var segunda = await _ingestor.IngestAsync(cn, "cuenta", Cargar(eml), 1, "INBOX", 100);

        Assert.True(primera.Inserted);
        Assert.False(segunda.Inserted);
        Assert.Equal(primera.EmailId, segunda.EmailId);
        Assert.Equal(1, await cn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM emails"));

        // Y sender_stats no ha contado el correo dos veces.
        Assert.Equal(1, await cn.ExecuteScalarAsync<long>(
            "SELECT received FROM sender_stats WHERE from_email = 'ana@estudiolegal.es'"));
    }

    [Fact]
    public async Task El_hilo_cuenta_sus_mensajes_y_detecta_la_insistencia()
    {
        // Tres mensajes seguidos del mismo remitente sin respuesta: §6 lo llama
        // "Tercer mensaje sin respuesta tuya" y es la senal de mas peso.
        for (var i = 1; i <= 3; i++)
        {
            await Ingerir(
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

        var pagina = await _repo.ListAsync(null, 50, null, Ahora);
        var ultimo = pagina.Items[0];

        Assert.Equal(3, ultimo.ThreadCount);
        Assert.Contains(ultimo.Signals, s => s.Label == "Tercer mensaje sin respuesta tuya");
        Assert.Equal("responder", ultimo.Bucket);
    }

    [Fact]
    public async Task Una_newsletter_html_queda_sin_markup_y_fuera_de_responder()
    {
        var id = await Ingerir(
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

        var correo = await _repo.GetAsync(id, Ahora);

        Assert.NotNull(correo);
        Assert.DoesNotContain("<", correo.Preview);
        Assert.DoesNotContain("color:red", string.Join(' ', correo.Body));
        Assert.Contains("Descubre las herramientas", string.Join(' ', correo.Body));
        Assert.NotEqual("responder", correo.Bucket);
        Assert.Contains(correo.Signals, s => s.Label == "List-Unsubscribe presente" && s.Sign == "neg");
    }

    [Fact]
    public async Task El_indice_de_texto_queda_poblado_y_busca_sin_acentos()
    {
        await Ingerir(
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

        // El rowid del indice tiene que ser el de emails, o la busqueda no podra
        // devolver el correo (email_fts es contentless).
        var encontrado = await cn.QuerySingleOrDefaultAsync<string>(
            """
            SELECT e.id FROM email_fts f
            JOIN emails e ON e.rowid = f.rowid
            WHERE email_fts MATCH 'factura'
            """);

        Assert.NotNull(encontrado);

        // remove_diacritics 2: buscar "area" encuentra "área".
        var sinAcento = await cn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM email_fts WHERE email_fts MATCH 'area'");

        Assert.Equal(1, sinAcento);
    }

    [Fact]
    public async Task La_paginacion_no_repite_ni_se_salta_correos()
    {
        for (var i = 1; i <= 7; i++)
        {
            await Ingerir(
                $"""
                From: Remitente {i} <r{i}@ejemplo.com>
                To: kevin@sagetasolutions.com
                Subject: Correo {i}
                Message-ID: <p{i}@ejemplo.com>
                Date: Tue, 3 Nov 2026 0{i}:00:00 +0000

                Cuerpo {i}
                """);
        }

        var vistos = new List<string>();
        string? cursor = null;

        do
        {
            var pagina = await _repo.ListAsync(null, 3, cursor, Ahora);
            vistos.AddRange(pagina.Items.Select(e => e.Id));
            cursor = pagina.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(7, vistos.Count);
        Assert.Equal(7, vistos.Distinct().Count());
    }

    [Fact]
    public async Task Filtrar_por_bucket_solo_devuelve_ese_bucket()
    {
        await Ingerir(
            """
            From: Docker Hub <noreply@docker.com>
            To: kevin@sagetasolutions.com
            Subject: Limite de pulls
            Message-ID: <docker1@docker.com>
            Date: Tue, 3 Nov 2026 06:00:00 +0000
            List-Unsubscribe: <https://docker.com/unsub>

            Has usado 180 de 200 pulls.
            """);

        await Ingerir(
            """
            From: Diego <d.ferran@sagetasolutions.com>
            To: kevin@sagetasolutions.com
            Subject: Numeros del Q3
            Message-ID: <diego1@sagetasolutions.com>
            Date: Tue, 3 Nov 2026 07:00:00 +0000

            ¿Puedes revisar los numeros del Q3?
            """);

        var responder = await _repo.ListAsync("responder", 50, null, Ahora);

        Assert.All(responder.Items, e => Assert.Equal("responder", e.Bucket));
        Assert.Contains(responder.Items, e => e.From.Email == "d.ferran@sagetasolutions.com");
        Assert.DoesNotContain(responder.Items, e => e.From.Email == "noreply@docker.com");
    }

    [Fact]
    public async Task Un_correo_sin_cuerpo_se_sirve_igual()
    {
        // No fallar nunca por correo raro (§11). Llega a la bandeja con lo que tenga.
        var id = await Ingerir(
            """
            From: Vercel <no-reply@vercel.com>
            To: kevin@sagetasolutions.com
            Subject: Despliegue correcto
            Message-ID: <vercel-vacio@vercel.com>
            Date: Tue, 3 Nov 2026 06:12:00 +0000
            Content-Type: text/plain; charset=utf-8


            """);

        var correo = await _repo.GetAsync(id, Ahora);

        Assert.NotNull(correo);
        Assert.Empty(correo.Body);
        Assert.Equal(string.Empty, correo.Preview);
        Assert.Equal("Despliegue correcto", correo.Subject);
    }

    [Fact]
    public async Task El_hilo_no_cuenta_los_mensajes_de_otra_cuenta()
    {
        // El MVP gestiona un solo buzon, pero el esquema admite varios y las
        // consultas tienen que acotar por cuenta. Sin esto, un hilo que llega a dos
        // direcciones tuyas saldria con el doble de mensajes, y las senales de
        // "participas en el hilo" o "tercer mensaje sin respuesta" se contaminarian
        // entre bandejas. Es un bug que no se ve hasta anadir la segunda cuenta.
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

            El mismo correo en los dos buzones.
            """;

        var enCuenta = await _ingestor.IngestAsync(cn, "cuenta", Cargar(eml), 1, "INBOX", 100);
        var enOtra = await _ingestor.IngestAsync(cn, "otra", Cargar(eml), 1, "INBOX", 100);

        // Dos filas distintas: el id se hashea con la cuenta.
        Assert.NotEqual(enCuenta.EmailId, enOtra.EmailId);
        Assert.Equal(2, await cn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM emails"));

        // Pero cada uno ve su hilo con UN mensaje, no con dos.
        var uno = await _repo.GetAsync(enCuenta.EmailId, Ahora);
        var otro = await _repo.GetAsync(enOtra.EmailId, Ahora);

        Assert.NotNull(uno);
        Assert.NotNull(otro);
        Assert.Equal(1, uno.ThreadCount);
        Assert.Equal(1, otro.ThreadCount);
    }

    // --- Recuentos de los tiles y leido/no leido -------------------------------

    [Fact]
    public async Task Los_recuentos_cuadran_con_las_listas_que_abren()
    {
        // La invariante de la que depende la pantalla principal del front: los cinco
        // tiles son la navegacion, asi que el numero del tile tiene que ser
        // exactamente el numero de correos que sale al pulsarlo. Si la regla del
        // umbral se aplicara en dos sitios distintos, esto se rompe en silencio.
        await SembrarVariados();

        var c = await _repo.CountsAsync();

        foreach (var (bucket, esperado) in new[]
        {
            ("responder", c.Responder),
            ("informativo", c.Informativo),
            ("automatizado", c.Automatizado),
            ("ruido", c.Ruido),
            ("duda", c.Duda),
        })
        {
            var lista = await _repo.ListAsync(bucket, 200, null, Ahora);
            Assert.Equal(esperado, lista.Items.Length);
        }

        // Y los cinco cajones reparten todos los correos, sin solapes ni huecos.
        using var cn = _db.Open();
        var total = (int)await cn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM emails");

        Assert.Equal(total, c.Responder + c.Informativo + c.Automatizado + c.Ruido + c.Duda);
    }

    [Fact]
    public async Task Un_correo_dudoso_sale_de_su_bucket_y_va_a_duda()
    {
        // Remitente desconocido: el baseline da 0.50, por debajo del umbral. No debe
        // aparecer en "responder" aunque su bucket calculado sea ese, porque el front
        // lo pinta en el cajon de duda y el correo saldria dos veces.
        var id = await Ingerir(
            """
            From: Desconocido <nuevo@proveedor-nuevo.com>
            To: kevin@sagetasolutions.com
            Subject: Su solicitud ha sido registrada
            Message-ID: <dudoso@proveedor-nuevo.com>
            Date: Tue, 3 Nov 2026 10:05:00 +0000

            Hemos recibido su solicitud.
            """);

        var correo = await _repo.GetAsync(id, Ahora);
        Assert.NotNull(correo);
        Assert.True(correo.Confidence < 0.55, $"confianza {correo.Confidence}");

        var enDuda = await _repo.ListAsync("duda", 50, null, Ahora);
        Assert.Contains(enDuda.Items, e => e.Id == id);

        var enSuBucket = await _repo.ListAsync(correo.Bucket, 50, null, Ahora);
        Assert.DoesNotContain(enSuBucket.Items, e => e.Id == id);
    }

    [Fact]
    public async Task Marcar_leido_devuelve_el_correo_y_alimenta_la_senal_de_apertura()
    {
        // Abrir un correo incrementa sender_stats.opened, que es lo que distingue
        // "automatizado" de "ruido" (§6). No es contabilidad: es la senal.
        var id = await Ingerir(
            """
            From: Vercel <no-reply@vercel.com>
            To: kevin@sagetasolutions.com
            Subject: Despliegue correcto
            Message-ID: <abrir@vercel.com>
            Date: Tue, 3 Nov 2026 06:12:00 +0000

            Tu despliegue se ha completado.
            """);

        var antes = await _repo.GetAsync(id, Ahora);
        Assert.True(antes!.Unread);

        var despues = await _repo.SetUnreadAsync(id, false, Ahora);

        Assert.NotNull(despues);
        Assert.False(despues.Unread);
        Assert.Equal(id, despues.Id);

        using var cn = _db.Open();
        Assert.Equal(1, await cn.ExecuteScalarAsync<long>(
            "SELECT opened FROM sender_stats WHERE from_email = 'no-reply@vercel.com'"));
    }

    [Fact]
    public async Task Abrir_el_mismo_correo_dos_veces_no_cuenta_dos_aperturas()
    {
        // Abrir diez veces el mismo correo no lo hace diez veces mas interesante.
        var id = await Ingerir(
            """
            From: Jira <jira@sageta.atlassian.net>
            To: kevin@sagetasolutions.com
            Subject: SAG-1182
            Message-ID: <doble@sageta.atlassian.net>
            Date: Tue, 3 Nov 2026 06:40:00 +0000

            Cambio de estado.
            """);

        await _repo.SetUnreadAsync(id, false, Ahora);
        await _repo.SetUnreadAsync(id, false, Ahora);

        using var cn = _db.Open();
        Assert.Equal(1, await cn.ExecuteScalarAsync<long>(
            "SELECT opened FROM sender_stats WHERE from_email = 'jira@sageta.atlassian.net'"));

        // Volver a marcar como no leido y abrir otra vez si cuenta: es una apertura nueva.
        await _repo.SetUnreadAsync(id, true, Ahora);
        await _repo.SetUnreadAsync(id, false, Ahora);

        Assert.Equal(2, await cn.ExecuteScalarAsync<long>(
            "SELECT opened FROM sender_stats WHERE from_email = 'jira@sageta.atlassian.net'"));
    }

    [Fact]
    public async Task El_recuento_de_no_leidos_sigue_las_aperturas()
    {
        await SembrarVariados();

        var inicial = await _repo.CountsAsync();
        Assert.True(inicial.Unread > 0);

        var primero = (await _repo.ListAsync(null, 1, null, Ahora)).Items[0];
        await _repo.SetUnreadAsync(primero.Id, false, Ahora);

        Assert.Equal(inicial.Unread - 1, (await _repo.CountsAsync()).Unread);
    }

    [Fact]
    public async Task Pedir_un_bucket_inexistente_no_devuelve_nada_en_vez_de_reventar()
    {
        await SembrarVariados();
        var lista = await _repo.ListAsync("inventado", 50, null, Ahora);
        Assert.Empty(lista.Items);
    }

    /// <summary>Un puñado de correos que caen en cajones distintos.</summary>
    private async Task SembrarVariados()
    {
        await Ingerir(
            """
            From: Diego <d.ferran@sagetasolutions.com>
            To: kevin@sagetasolutions.com
            Subject: Numeros del Q3
            Message-ID: <v1@sagetasolutions.com>
            Date: Tue, 3 Nov 2026 07:00:00 +0000

            ¿Puedes revisar los numeros del Q3?
            """);

        await Ingerir(
            """
            From: Zentrix <newsletter@zentrix.io>
            To: kevin@sagetasolutions.com
            Subject: Oferta del mes
            Message-ID: <v2@zentrix.io>
            Date: Tue, 3 Nov 2026 05:30:00 +0000
            List-Unsubscribe: <mailto:unsub@zentrix.io>

            Descubre las herramientas.
            """);

        await Ingerir(
            """
            From: Marta <marta.ruiz@universidadsur.edu>
            To: comite@universidadsur.edu
            Cc: kevin@sagetasolutions.com
            Subject: Acta del comite
            Message-ID: <v3@universidadsur.edu>
            Date: Tue, 3 Nov 2026 11:30:00 +0000

            No requiere accion por vuestra parte.
            """);

        await Ingerir(
            """
            From: Desconocido <otro@sin-historial.com>
            To: kevin@sagetasolutions.com
            Subject: Solicitud registrada
            Message-ID: <v4@sin-historial.com>
            Date: Tue, 3 Nov 2026 10:05:00 +0000

            Hemos recibido su solicitud.
            """);
    }

    // --- Estado del modelo -----------------------------------------------------

    [Fact]
    public async Task El_estado_del_modelo_desglosa_los_ejemplos_por_origen()
    {
        // El front lo necesita para distinguir "aun no te conozco" (pocos ejemplos, y
        // los que hay vienen del bootstrap) de "no se clasificar esto".
        var id = await Ingerir(
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
              (@id, 'informativo', 'responder', 'user',             '2026-11-03T10:00:00Z'),
              (@id, 'informativo', 'responder', 'user',             '2026-11-03T10:01:00Z'),
              (@id, NULL,          'responder', 'bootstrap_sent',   '2026-11-03T09:00:00Z'),
              (@id, NULL,          'ruido',     'bootstrap_folder', '2026-11-03T09:00:00Z'),
              (@id, NULL,          'ruido',     'rule',             '2026-11-03T09:00:00Z')
            """, new { id });

        var estado = await new ModelStatusService(_db, _correcciones, SinEmbeddings(), NullLogger<ModelStatusService>.Instance).GetAsync();

        Assert.Equal(2, estado.ExamplesBySource.User);
        Assert.Equal(1, estado.ExamplesBySource.BootstrapSent);
        Assert.Equal(1, estado.ExamplesBySource.BootstrapFolder);
        Assert.Equal(1, estado.ExamplesBySource.Rule);
        Assert.Equal(5, estado.Examples);
        Assert.True(estado.BootstrapRan);
        Assert.Equal(1, estado.IndexedEmails);
    }

    [Fact]
    public async Task Un_deshacer_no_sube_el_contador_de_ejemplos()
    {
        // El deshacer inserta un evento inverso en vez de borrar la fila (§1, regla 4).
        // Si el contador no lo descontara, deshacer una correccion SUBIRIA el numero de
        // ejemplos, que es justo lo contrario de lo que el usuario acaba de hacer.
        var id = await Ingerir(
            """
            From: Ana <ana@estudiolegal.es>
            To: kevin@sagetasolutions.com
            Subject: Reunion
            Message-ID: <deshacer@estudiolegal.es>
            Date: Tue, 3 Nov 2026 09:14:00 +0000

            Cuerpo
            """);

        var servicio = new ModelStatusService(_db, _correcciones, SinEmbeddings(), NullLogger<ModelStatusService>.Instance);
        using var cn = _db.Open();

        await cn.ExecuteAsync(
            """
            INSERT INTO corrections (email_id, bucket_before, bucket_after, source, created_at)
            VALUES (@id, 'informativo', 'responder', 'user', '2026-11-03T10:00:00Z')
            """, new { id });

        Assert.Equal(1, (await servicio.GetAsync()).Examples);

        await cn.ExecuteAsync(
            """
            INSERT INTO corrections (email_id, bucket_before, bucket_after, source, created_at)
            VALUES (@id, 'responder', 'informativo', 'undo', '2026-11-03T10:00:30Z')
            """, new { id });

        var tras = await servicio.GetAsync();
        Assert.Equal(0, tras.Examples);
        Assert.Equal(0, tras.ExamplesBySource.User);
    }

    [Fact]
    public async Task La_precision_es_nula_mientras_no_haya_set_de_evaluacion()
    {
        // La spec pone 0.91 como ejemplo, pero ese numero solo sale de medir contra
        // 250 correos etiquetados a mano. Devolver algo inventado pondria una cifra
        // falsa delante del usuario.
        var estado = await new ModelStatusService(_db, _correcciones, SinEmbeddings(), NullLogger<ModelStatusService>.Instance).GetAsync();

        Assert.Null(estado.Precision);
        Assert.Null(estado.LastTrained);
        Assert.Equal(0, estado.Examples);
        Assert.False(estado.BootstrapRan);
        Assert.Equal("multilingual-e5-small (ONNX, 118M)", estado.Embeddings);
    }

    [Fact]
    public async Task Una_bandeja_vacia_devuelve_ceros_y_no_revienta()
    {
        // Es el estado de toda instalacion nueva, antes del primer sync. Las dos
        // consultas de agregacion usaban SUM, que sobre cero filas devuelve NULL en
        // vez de 0, asi que los dos endpoints fallaban justo en el primer arranque.
        var c = await _repo.CountsAsync();

        Assert.Equal(0, c.Responder);
        Assert.Equal(0, c.Informativo);
        Assert.Equal(0, c.Automatizado);
        Assert.Equal(0, c.Ruido);
        Assert.Equal(0, c.Duda);
        Assert.Equal(0, c.Unread);

        var lista = await _repo.ListAsync(null, 50, null, Ahora);
        Assert.Empty(lista.Items);
        Assert.Null(lista.NextCursor);
    }

    // --- Correcciones y deshacer -----------------------------------------------

    [Fact]
    public async Task Corregir_mueve_el_correo_al_cajon_elegido_y_lo_saca_de_duda()
    {
        // Lo que de verdad tiene que pasar al corregir: el correo aparece donde el
        // usuario ha dicho. Si la confianza se quedara en la del modelo, un correo
        // dudoso corregido seguiria saliendo en "No estoy seguro" y la correccion
        // pareceria no haber hecho nada.
        var id = await Ingerir(Correo("dudoso"));

        var antes = await _repo.GetAsync(id, Ahora);
        Assert.True(antes!.Confidence < 0.55, $"confianza inicial {antes.Confidence}");
        Assert.Contains((await _repo.ListAsync("duda", 50, null, Ahora)).Items, e => e.Id == id);

        var r = await _correcciones.ApplyAsync(id, Bucket.Informativo, Ahora);

        Assert.NotNull(r);
        Assert.NotNull(r.Email);
        Assert.Equal("informativo", r.Email.Bucket);
        Assert.Equal(1.0, r.Email.Confidence);
        Assert.Equal(1, r.Examples);

        Assert.Contains((await _repo.ListAsync("informativo", 50, null, Ahora)).Items, e => e.Id == id);
        Assert.DoesNotContain((await _repo.ListAsync("duda", 50, null, Ahora)).Items, e => e.Id == id);
    }

    [Fact]
    public async Task Corregir_conserva_las_senales_que_explican_el_error_del_modelo()
    {
        // Las senales siguen siendo hechos ciertos sobre el correo. Borrarlas al
        // corregir dejaria al usuario sin poder ver en que se equivoco el sistema.
        var id = await Ingerir(
            """
            From: Zentrix <newsletter@zentrix.io>
            To: kevin@sagetasolutions.com
            Subject: Oferta
            Message-ID: <senales@zentrix.io>
            Date: Tue, 3 Nov 2026 05:30:00 +0000
            List-Unsubscribe: <mailto:unsub@zentrix.io>

            Descubre las herramientas.
            """);

        var senalesAntes = (await _repo.GetAsync(id, Ahora))!.Signals.Length;
        Assert.True(senalesAntes > 0);

        var r = await _correcciones.ApplyAsync(id, Bucket.Responder, Ahora);

        Assert.Equal(senalesAntes, r!.Email!.Signals.Length);
        Assert.Contains(r.Email.Signals, x => x.Label == "List-Unsubscribe presente");
    }

    [Fact]
    public async Task Deshacer_devuelve_el_correo_a_su_bucket_Y_a_su_confianza()
    {
        // Devolver solo el bucket no es deshacer: un correo que estaba en "No estoy
        // seguro" reapareceria afirmado en un cajon. Por eso la migracion 002 guarda
        // confidence_before.
        var id = await Ingerir(Correo("volver"));

        var original = await _repo.GetAsync(id, Ahora);
        var bucketOriginal = original!.Bucket;
        var confianzaOriginal = original.Confidence;

        await _correcciones.ApplyAsync(id, Bucket.Ruido, Ahora);
        Assert.Equal("ruido", (await _repo.GetAsync(id, Ahora))!.Bucket);

        var r = await _correcciones.UndoAsync(Ahora);

        Assert.NotNull(r.Email);
        Assert.Equal(bucketOriginal, r.Email.Bucket);
        Assert.Equal(confianzaOriginal, r.Email.Confidence);
        Assert.Equal(0, r.Examples);

        // Y vuelve a estar en duda, donde estaba.
        Assert.Contains((await _repo.ListAsync("duda", 50, null, Ahora)).Items, e => e.Id == id);
    }

    [Fact]
    public async Task Deshacer_sin_nada_que_deshacer_no_es_un_error()
    {
        // El Ctrl+Z del front no tiene que llevar la cuenta de la pila.
        var r = await _correcciones.UndoAsync(Ahora);

        Assert.Null(r.Email);
        Assert.Equal(0, r.Examples);
    }

    [Fact]
    public async Task Deshacer_afecta_siempre_a_la_ultima_correccion_en_vigor()
    {
        // El caso que rompe la version ingenua (contar deshaceres y restarlos):
        // corrige A, corrige B, deshace B, corrige C, deshace -> tiene que deshacer C.
        var a = await Ingerir(Correo("a"));
        var b = await Ingerir(Correo("b"));
        var c = await Ingerir(Correo("c"));

        await _correcciones.ApplyAsync(a, Bucket.Ruido, Ahora);
        await _correcciones.ApplyAsync(b, Bucket.Ruido, Ahora);

        var deshaceB = await _correcciones.UndoAsync(Ahora);
        Assert.Equal(b, deshaceB.Email!.Id);

        await _correcciones.ApplyAsync(c, Bucket.Ruido, Ahora);

        var deshaceC = await _correcciones.UndoAsync(Ahora);
        Assert.Equal(c, deshaceC.Email!.Id);

        // A sigue corregido: nadie lo ha tocado.
        Assert.Equal("ruido", (await _repo.GetAsync(a, Ahora))!.Bucket);
        Assert.Equal(1, deshaceC.Examples);

        // Y el siguiente deshacer si alcanza a A.
        var deshaceA = await _correcciones.UndoAsync(Ahora);
        Assert.Equal(a, deshaceA.Email!.Id);
        Assert.Equal(0, deshaceA.Examples);
    }

    [Fact]
    public async Task El_historial_de_correcciones_no_se_borra_nunca()
    {
        // Regla 4 de §1: las correcciones son historial, no estado. Sin el no se puede
        // reentrenar desde cero ni medir si el modelo mejora.
        var id = await Ingerir(Correo("historial"));

        await _correcciones.ApplyAsync(id, Bucket.Ruido, Ahora);
        await _correcciones.UndoAsync(Ahora);
        await _correcciones.ApplyAsync(id, Bucket.Automatizado, Ahora);

        using var cn = _db.Open();
        var origenes = (await cn.QueryAsync<string>(
            "SELECT source FROM corrections ORDER BY id")).ToList();

        Assert.Equal(new[] { "user", "undo", "user" }, origenes);
        Assert.Equal(1, (await _correcciones.CountAsync()).Total);
    }

    [Fact]
    public async Task Corregir_un_correo_inexistente_devuelve_null()
    {
        Assert.Null(await _correcciones.ApplyAsync("noexiste", Bucket.Responder, Ahora));
    }

    /// <summary>Correo minimo de un remitente desconocido, que cae en duda.</summary>
    private static string Correo(string clave) =>
        $"""
        From: Remitente {clave} <{clave}@ejemplo.com>
        To: kevin@sagetasolutions.com
        Subject: Correo {clave}
        Message-ID: <{clave}@ejemplo.com>
        Date: Tue, 3 Nov 2026 08:00:00 +0000

        Cuerpo de {clave}.
        """;

    [Fact]
    public void Un_deshacer_vacio_manda_email_null_explicito_no_lo_omite()
    {
        // El front distingue `email?: Email` de `email: Email | null`, que son dos
        // comprobaciones distintas en TypeScript. Aqui null ES la respuesta -"no habia
        // nada que deshacer"- y no la ausencia de un dato, asi que tiene que viajar.
        // Las opciones de serializacion son las mismas que configura Program.cs.
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
    /// Proveedor de embeddings apuntando a una carpeta que no existe. Los tests no
    /// deben cargar 470 MB de modelo, y de paso esto ejercita el camino de degradacion:
    /// sin embeddings el resto del sistema tiene que seguir funcionando.
    /// </summary>
    private static EmbeddingProvider SinEmbeddings() =>
        new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Models:Path"] = "carpeta-que-no-existe" })
                .Build(),
            NullLogger<EmbeddingProvider>.Instance,
            NullLogger<E5Embedder>.Instance);

    private async Task<string> Ingerir(string eml)
    {
        using var cn = _db.Open();
        var r = await _ingestor.IngestAsync(cn, "cuenta", Cargar(eml), 1, "INBOX", eml.Length);
        return r.EmailId;
    }

    private static MimeMessage Cargar(string eml)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(eml.ReplaceLineEndings("\r\n"));
        using var ms = new MemoryStream(bytes);
        return MimeMessage.Load(ms);
    }
}

/// <summary>El campo <c>time</c> del contrato (§3).</summary>
public class RelativeTimeTests
{
    /// <summary>
    /// Instante construido en hora LOCAL. El backend corre en la maquina del propio
    /// usuario -solo escucha en localhost-, asi que su hora local es la del usuario;
    /// montar los casos en UTC haria que el resultado dependiera del huso.
    /// </summary>
    private static DateTimeOffset Local(int dia, int hora, int minuto)
    {
        var sinHuso = new DateTime(2026, 11, dia, hora, minuto, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(sinHuso, TimeZoneInfo.Local.GetUtcOffset(sinHuso));
    }

    private static readonly DateTimeOffset Ahora = Local(3, 12, 0);

    [Fact]
    public void Hoy_muestra_la_hora()
    {
        Assert.Equal("9:14", RelativeTime.Format(Local(3, 9, 14), Ahora));
    }

    [Fact]
    public void Ayer_se_dice_con_palabras()
    {
        var cuando = Ahora.AddDays(-1);
        Assert.Equal("Ayer", RelativeTime.Format(cuando, Ahora));
    }

    [Fact]
    public void Dentro_de_la_semana_se_usa_el_dia_en_espanol()
    {
        // 2026-11-03 es martes; tres dias antes es sabado.
        var cuando = Ahora.AddDays(-3);
        Assert.Equal("Sáb", RelativeTime.Format(cuando, Ahora));
    }

    [Fact]
    public void Mas_antiguo_lleva_dia_y_mes_abreviado()
    {
        var cuando = Ahora.AddDays(-22);
        Assert.Equal("12 oct", RelativeTime.Format(cuando, Ahora));
    }

    [Fact]
    public void El_formato_no_depende_de_la_cultura_de_la_maquina()
    {
        // Si usara la cultura del sistema, la misma API devolveria "Mon" o "Mo"
        // segun donde corre el backend y el front no sabria que esperar.
        var previa = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("en-US");
            Assert.Equal("Ayer", RelativeTime.Format(Ahora.AddDays(-1), Ahora));
            Assert.Equal("Sáb", RelativeTime.Format(Ahora.AddDays(-3), Ahora));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previa;
        }
    }
}
