using LocalMailAI.Api.Classification;
using LocalMailAI.Api.Mail;
using MimeKit;

namespace LocalMailAI.Tests;

/// <summary>
/// Cabeceras (BACKEND.md §4) y senales deterministas (§6).
///
/// Los casos estan calcados de src/lib/mock.ts a proposito: ese fichero es el
/// contrato con el front y ya describe que senales deberia producir cada correo.
/// Si el backend no coincide con sus fixtures, uno de los dos esta mal.
/// </summary>
public class SignalTests
{
    private static readonly IReadOnlySet<string> Yo =
        new HashSet<string> { "kevin@sagetasolutions.com" };

    // --- Cabeceras -------------------------------------------------------------

    [Fact]
    public void La_raiz_del_hilo_es_el_primer_References()
    {
        var f = Facts(
            """
            From: Ana <ana@estudiolegal.es>
            To: kevin@sagetasolutions.com
            Subject: Re: Reunion
            Message-ID: <c@estudiolegal.es>
            References: <a@sageta.com> <b@estudiolegal.es>
            In-Reply-To: <b@estudiolegal.es>

            Cuerpo
            """);

        Assert.Equal("a@sageta.com", f.ThreadId);
        Assert.Equal("b@estudiolegal.es", f.InReplyTo);
        Assert.Equal(2, f.References.Length);
    }

    [Fact]
    public void Sin_References_la_raiz_es_el_In_Reply_To()
    {
        var f = Facts(
            """
            From: Ana <ana@estudiolegal.es>
            To: kevin@sagetasolutions.com
            Message-ID: <b@estudiolegal.es>
            In-Reply-To: <a@sageta.com>

            Cuerpo
            """);

        Assert.Equal("a@sageta.com", f.ThreadId);
    }

    [Fact]
    public void Un_mensaje_suelto_es_la_raiz_de_su_propio_hilo()
    {
        var f = Facts(
            """
            From: Ana <ana@estudiolegal.es>
            To: kevin@sagetasolutions.com
            Message-ID: <solo@estudiolegal.es>

            Cuerpo
            """);

        Assert.Equal("solo@estudiolegal.es", f.ThreadId);
        Assert.Equal("solo@estudiolegal.es", f.MessageId);
    }

    [Fact]
    public void Sin_Message_ID_se_genera_uno_estable()
    {
        // Un GUID nuevo en cada sync duplicaria la fila en cada pasada.
        const string eml =
            """
            From: Ana <ana@estudiolegal.es>
            To: kevin@sagetasolutions.com
            Subject: Sin identificador
            Date: Mon, 2 Nov 2026 09:14:00 +0100

            Cuerpo
            """;

        Assert.Equal(Facts(eml).MessageId, Facts(eml).MessageId);
        Assert.Contains("sin-message-id-", Facts(eml).MessageId);
    }

    [Fact]
    public void Detecta_List_Unsubscribe()
    {
        var f = Facts(
            """
            From: Zentrix <newsletter@zentrix.io>
            To: kevin@sagetasolutions.com
            List-Unsubscribe: <mailto:unsub@zentrix.io>

            Oferta
            """);

        Assert.True(f.HasListUnsubscribe);
    }

    [Theory]
    [InlineData("Auto-Submitted: auto-generated", true)]
    [InlineData("Auto-Submitted: no", false)]
    [InlineData("Precedence: bulk", true)]
    [InlineData("Precedence: normal", false)]
    [InlineData("List-Id: <avisos.github.com>", true)]
    public void Reconoce_el_correo_automatico(string cabecera, bool esperado)
    {
        var f = Facts(
            $"""
            From: Jira <jira@sageta.atlassian.net>
            To: kevin@sagetasolutions.com
            {cabecera}

            SAG-1182 cambio de estado
            """);

        Assert.Equal(esperado, f.IsAutoSubmitted);
    }

    [Fact]
    public void Lee_spf_y_dkim_de_Authentication_Results()
    {
        var f = Facts(
            """
            From: Banco <avisos@bancoiberico.es>
            To: kevin@sagetasolutions.com
            Authentication-Results: mx.google.com; dkim=pass header.i=@bancoiberico.es; spf=pass smtp.mailfrom=bancoiberico.es

            Movimiento en tu cuenta
            """);

        Assert.True(f.SpfPass);
        Assert.True(f.DkimPass);
    }

    [Fact]
    public void Sin_Authentication_Results_la_autenticacion_es_desconocida_no_fallida()
    {
        // No es lo mismo "ha fallado la verificacion" que "nadie la ha hecho".
        var f = Facts(
            """
            From: Ana <ana@estudiolegal.es>
            To: kevin@sagetasolutions.com

            Cuerpo
            """);

        Assert.Null(f.SpfPass);
        Assert.Null(f.DkimPass);
    }

    [Fact]
    public void Un_spf_fallido_se_distingue_de_un_spf_ausente()
    {
        var f = Facts(
            """
            From: Falso <soporte@proveedor-nuevo.com>
            To: kevin@sagetasolutions.com
            Authentication-Results: mx.google.com; spf=fail smtp.mailfrom=proveedor-nuevo.com

            Su solicitud
            """);

        Assert.False(f.SpfPass);
        Assert.Null(f.DkimPass);
    }

    [Theory]
    [InlineData("no-reply@vercel.com", true)]
    [InlineData("noreply@docker.com", true)]
    [InlineData("do.not.reply@banco.es", true)]
    [InlineData("facturacion@nordia-cloud.com", false)]
    [InlineData("ana.molina@estudiolegal.es", false)]
    public void Reconoce_las_direcciones_no_reply(string direccion, bool esperado)
    {
        var f = Facts(
            $"""
            From: Remitente <{direccion}>
            To: kevin@sagetasolutions.com

            Cuerpo
            """);

        Assert.Equal(esperado, f.IsNoReplyAddress);
    }

    [Fact]
    public void Las_direcciones_se_guardan_en_minusculas()
    {
        var f = Facts(
            """
            From: Ana <Ana.Molina@EstudioLegal.ES>
            To: Kevin@SagetaSolutions.com
            Cc: Diego@SagetaSolutions.com

            Cuerpo
            """);

        Assert.Equal("ana.molina@estudiolegal.es", f.FromEmail);
        Assert.Equal(["kevin@sagetasolutions.com"], f.To);
        Assert.Equal(["diego@sagetasolutions.com"], f.Cc);
        Assert.Equal(2, f.RecipientCount);
    }

    // --- Senales y clasificacion provisional -----------------------------------

    [Fact]
    public void Correo_de_Ana_requiere_respuesta()
    {
        // mock.ts e1: responder, 0.94, con "Le has respondido 12 veces",
        // "Vas en Para:, no en Cc:", "Termina en pregunta directa", "Hilo en el que ya participas".
        var f = Facts(
            """
            From: Ana Molina <ana.molina@estudiolegal.es>
            To: kevin@sagetasolutions.com
            Subject: Re: Confirmacion reunion del jueves
            Message-ID: <d@estudiolegal.es>
            References: <a@sageta.com>

            Cuerpo
            """);

        var ctx = new SignalContext
        {
            OwnAddresses = Yo,
            SenderReceived = 24,
            SenderReplied = 12,
            IParticipatesInThread = true,
            CleanBody = "Te viene bien a las 10 en lugar de las 9?",
        };

        var senales = DeterministicSignals.Compute(f, ctx);
        var c = SignalBaseline.Classify(senales, ctx);

        Assert.Equal(Bucket.Responder, c.Bucket);
        Assert.True(c.Confidence > SignalBaseline.ConfidenceThreshold);
        Assert.Contains(senales, s => s.Kind == SignalKind.RepliedBefore && s.Label == "Le has respondido 12 veces");
        Assert.Contains(senales, s => s.Kind == SignalKind.ToNotCc);
        Assert.Contains(senales, s => s.Kind == SignalKind.EndsWithQuestion);
        Assert.Contains(senales, s => s.Kind == SignalKind.ThreadParticipant);
    }

    [Fact]
    public void Un_remitente_desconocido_queda_por_debajo_del_umbral()
    {
        // mock.ts e2: confianza 0.48, por debajo de UMBRAL_CONFIANZA. El sistema
        // admite que no sabe en vez de adivinar.
        var f = Facts(
            """
            From: Facturacion Nordia <facturacion@nordia-cloud.com>
            To: kevin@sagetasolutions.com
            Subject: Aviso de vencimiento

            Cuerpo
            """);

        var ctx = new SignalContext { OwnAddresses = Yo, SenderReceived = 0, CleanBody = "La factura vence el 10/11." };
        var senales = DeterministicSignals.Compute(f, ctx);
        var c = SignalBaseline.Classify(senales, ctx);

        Assert.Contains(senales, s => s.Kind == SignalKind.NewSender);
        Assert.True(c.Confidence < SignalBaseline.ConfidenceThreshold,
            $"confianza {c.Confidence} deberia estar por debajo de {SignalBaseline.ConfidenceThreshold}");
    }

    [Fact]
    public void Una_newsletter_que_nunca_se_abre_es_ruido()
    {
        // mock.ts e12: ruido. List-Unsubscribe y nunca abierta.
        var f = Facts(
            """
            From: Marketing Zentrix <newsletter@zentrix.io>
            To: kevin@sagetasolutions.com
            Subject: 7 formas de multiplicar tu productividad
            List-Unsubscribe: <mailto:unsub@zentrix.io>

            Cuerpo
            """);

        var ctx = new SignalContext { OwnAddresses = Yo, SenderReceived = 38, SenderOpened = 0 };
        var c = SignalBaseline.Classify(DeterministicSignals.Compute(f, ctx), ctx);

        Assert.Equal(Bucket.Ruido, c.Bucket);
    }

    [Fact]
    public void Un_aviso_de_maquina_que_si_se_lee_es_automatizado_no_ruido()
    {
        // mock.ts e9 (Vercel): automatizado, no ruido, aunque lleve las mismas
        // cabeceras que la newsletter. Lo que las separa es que este se abre.
        var f = Facts(
            """
            From: Vercel <no-reply@vercel.com>
            To: kevin@sagetasolutions.com
            Subject: Despliegue correcto
            List-Unsubscribe: <mailto:unsub@vercel.com>

            Cuerpo
            """);

        var ctx = new SignalContext { OwnAddresses = Yo, SenderReceived = 212, SenderOpened = 180 };
        var c = SignalBaseline.Classify(DeterministicSignals.Compute(f, ctx), ctx);

        Assert.Equal(Bucket.Automatizado, c.Bucket);
    }

    [Fact]
    public void Una_senal_personal_gana_a_las_marcas_de_maquina()
    {
        // mock.ts e5: GitHub es un remitente automatizado y aun asi pide revision.
        var f = Facts(
            """
            From: GitHub <notifications@github.com>
            To: kevin@sagetasolutions.com
            Subject: PR #14 revision solicitada
            List-Id: <localmail-ai.github.com>

            Cuerpo
            """);

        var ctx = new SignalContext
        {
            OwnAddresses = Yo,
            SenderReceived = 300,
            SenderReplied = 4,
            IParticipatesInThread = true,
        };

        var c = SignalBaseline.Classify(DeterministicSignals.Compute(f, ctx), ctx);

        Assert.Equal(Bucket.Responder, c.Bucket);
    }

    [Fact]
    public void Ir_en_Cc_es_senal_negativa()
    {
        // mock.ts e6: informativo, con "Vas en Cc:, no en Para:" en negativo.
        var f = Facts(
            """
            From: Marta Ruiz <marta.ruiz@universidadsur.edu>
            To: comite@universidadsur.edu
            Cc: kevin@sagetasolutions.com
            Subject: Acta de la reunion

            Cuerpo
            """);

        var ctx = new SignalContext { OwnAddresses = Yo, SenderReceived = 12, SenderOpened = 9 };
        var senales = DeterministicSignals.Compute(f, ctx);
        var c = SignalBaseline.Classify(senales, ctx);

        Assert.Contains(senales, s => s.Kind == SignalKind.CcNotTo && s.Weight < 0);
        Assert.Equal("neg", senales.First(s => s.Kind == SignalKind.CcNotTo).ToSignal().Sign);
        Assert.Equal(Bucket.Informativo, c.Bucket);
    }

    [Fact]
    public void El_tercer_mensaje_sin_respuesta_pesa_lo_maximo()
    {
        // mock.ts e16: responder, 0.95, "Tercer mensaje sin respuesta tuya".
        var f = Facts(
            """
            From: Pablo Esteban <pablo.esteban@clientemayor.com>
            To: kevin@sagetasolutions.com
            Subject: Re: Re: Incidencia en produccion

            Cuerpo
            """);

        var ctx = new SignalContext
        {
            OwnAddresses = Yo,
            SenderReceived = 47,
            SenderReplied = 20,
            ConsecutiveUnanswered = 3,
            CleanBody = "Seguimos sin respuesta del equipo tecnico. Hay alguna novedad?",
        };

        var senales = DeterministicSignals.Compute(f, ctx);
        var c = SignalBaseline.Classify(senales, ctx);

        Assert.Contains(senales, s => s.Kind == SignalKind.ThirdUnanswered && s.Weight == 3);
        Assert.Equal(Bucket.Responder, c.Bucket);
        Assert.True(c.Confidence >= 0.9, $"confianza {c.Confidence}");
    }

    [Fact]
    public void Nunca_mas_de_seis_senales_y_ordenadas_por_peso()
    {
        var f = Facts(
            """
            From: Diego Ferran <d.ferran@sagetasolutions.com>
            To: kevin@sagetasolutions.com
            Subject: Propuesta
            Authentication-Results: mx.google.com; dkim=pass; spf=pass
            List-Unsubscribe: <mailto:x@y.com>
            Precedence: bulk

            Cuerpo
            """);

        var ctx = new SignalContext
        {
            OwnAddresses = Yo,
            OrgDomain = "sagetasolutions.com",
            SenderReceived = 60,
            SenderReplied = 31,
            IParticipatesInThread = true,
            ConsecutiveUnanswered = 3,
            CleanBody = "Puedes revisarlo?",
        };

        var senales = DeterministicSignals.Compute(f, ctx);

        Assert.Equal(6, senales.Count);

        var pesos = senales.Select(s => Math.Abs(s.Weight)).ToArray();
        Assert.Equal(pesos.OrderByDescending(p => p), pesos);
    }

    [Fact]
    public void Con_poco_historial_no_se_afirma_que_nunca_has_respondido()
    {
        // "Nunca lo has respondido (0 de 2)" no dice nada: puede que no haya habido ocasion.
        var f = Facts(
            """
            From: Lucia <lucia@herrera-asesoria.es>
            To: kevin@sagetasolutions.com

            Cuerpo
            """);

        var ctx = new SignalContext { OwnAddresses = Yo, SenderReceived = 2, SenderReplied = 0 };
        var senales = DeterministicSignals.Compute(f, ctx);

        Assert.DoesNotContain(senales, s => s.Kind == SignalKind.NeverReplied);
    }

    [Fact]
    public void La_pregunta_retorica_del_principio_no_cuenta()
    {
        var f = Facts(
            """
            From: Zentrix <newsletter@zentrix.io>
            To: kevin@sagetasolutions.com

            Cuerpo
            """);

        var ctx = new SignalContext
        {
            OwnAddresses = Yo,
            SenderReceived = 38,
            CleanBody = "Sabias que puedes duplicar tu productividad?\n\nDescubre las herramientas.\nApuntate ya.",
        };

        Assert.DoesNotContain(DeterministicSignals.Compute(f, ctx), s => s.Kind == SignalKind.EndsWithQuestion);
    }

    private static MessageFacts Facts(string eml)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(eml.ReplaceLineEndings("\r\n"));
        using var ms = new MemoryStream(bytes);
        return MessageFacts.From(MimeMessage.Load(ms));
    }
}
