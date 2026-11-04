using LocalMailAI.Api.Classification;
using LocalMailAI.Api.Mail;
using MimeKit;

namespace LocalMailAI.Tests;

/// <summary>
/// Header facts (BACKEND.md §4) and deterministic signals (§6).
///
/// The cases are copied from src/lib/mock.ts on purpose: that file is the contract
/// with the frontend and already describes which signals each message should produce.
/// If the backend disagrees with its fixtures, one of the two is wrong.
/// </summary>
public class SignalTests
{
    private static readonly IReadOnlySet<string> Me =
        new HashSet<string> { "kevin@sagetasolutions.com" };

    // --- Headers ---------------------------------------------------------------

    [Fact]
    public void The_thread_root_is_the_first_References_entry()
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
    public void Without_References_the_root_is_In_Reply_To()
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
    public void A_standalone_message_is_the_root_of_its_own_thread()
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
    public void Without_a_Message_ID_a_stable_one_is_derived()
    {
        // A fresh GUID on every sync would duplicate the row on each pass.
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
    public void Detects_List_Unsubscribe()
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
    public void Recognises_automated_mail(string header, bool expected)
    {
        var f = Facts(
            $"""
            From: Jira <jira@sageta.atlassian.net>
            To: kevin@sagetasolutions.com
            {header}

            SAG-1182 cambio de estado
            """);

        Assert.Equal(expected, f.IsAutoSubmitted);
    }

    [Fact]
    public void Reads_spf_and_dkim_from_Authentication_Results()
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
    public void Without_Authentication_Results_authentication_is_unknown_not_failed()
    {
        // "the check failed" and "nobody ran the check" are not the same thing.
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
    public void A_failed_spf_is_distinguishable_from_an_absent_one()
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
    public void Recognises_no_reply_addresses(string address, bool expected)
    {
        var f = Facts(
            $"""
            From: Remitente <{address}>
            To: kevin@sagetasolutions.com

            Cuerpo
            """);

        Assert.Equal(expected, f.IsNoReplyAddress);
    }

    [Fact]
    public void Addresses_are_stored_lowercase()
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

    // --- Signals and provisional classification ---------------------------------

    [Fact]
    public void A_message_from_a_known_contact_asking_a_question_needs_a_reply()
    {
        // mock.ts e1: responder, 0.94, with "Le has respondido 12 veces",
        // "Vas en Para:, no en Cc:", "Termina en pregunta directa",
        // "Hilo en el que ya participas".
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
            OwnAddresses = Me,
            SenderReceived = 24,
            SenderReplied = 12,
            IParticipatesInThread = true,
            CleanBody = "Te viene bien a las 10 en lugar de las 9?",
        };

        var signals = DeterministicSignals.Compute(f, ctx);
        var c = SignalBaseline.Classify(signals, ctx);

        Assert.Equal(Bucket.Responder, c.Bucket);
        Assert.True(c.Confidence > SignalBaseline.ConfidenceThreshold);
        Assert.Contains(signals, s => s.Kind == SignalKind.RepliedBefore && s.Label == "Le has respondido 12 veces");
        Assert.Contains(signals, s => s.Kind == SignalKind.ToNotCc);
        Assert.Contains(signals, s => s.Kind == SignalKind.EndsWithQuestion);
        Assert.Contains(signals, s => s.Kind == SignalKind.ThreadParticipant);
    }

    [Fact]
    public void An_unknown_sender_stays_below_the_threshold()
    {
        // mock.ts e2: confidence 0.48, below UMBRAL_CONFIANZA. The system admits it
        // does not know rather than guessing.
        var f = Facts(
            """
            From: Facturacion Nordia <facturacion@nordia-cloud.com>
            To: kevin@sagetasolutions.com
            Subject: Aviso de vencimiento

            Cuerpo
            """);

        var ctx = new SignalContext { OwnAddresses = Me, SenderReceived = 0, CleanBody = "La factura vence el 10/11." };
        var signals = DeterministicSignals.Compute(f, ctx);
        var c = SignalBaseline.Classify(signals, ctx);

        Assert.Contains(signals, s => s.Kind == SignalKind.NewSender);
        Assert.True(c.Confidence < SignalBaseline.ConfidenceThreshold,
            $"confidence {c.Confidence} should be below {SignalBaseline.ConfidenceThreshold}");
    }

    [Fact]
    public void A_newsletter_that_is_never_opened_is_noise()
    {
        // mock.ts e12: ruido. List-Unsubscribe and never opened.
        var f = Facts(
            """
            From: Marketing Zentrix <newsletter@zentrix.io>
            To: kevin@sagetasolutions.com
            Subject: 7 formas de multiplicar tu productividad
            List-Unsubscribe: <mailto:unsub@zentrix.io>

            Cuerpo
            """);

        var ctx = new SignalContext { OwnAddresses = Me, SenderReceived = 38, SenderOpened = 0 };
        var c = SignalBaseline.Classify(DeterministicSignals.Compute(f, ctx), ctx);

        Assert.Equal(Bucket.Ruido, c.Bucket);
    }

    [Fact]
    public void Machine_mail_that_does_get_read_is_automated_not_noise()
    {
        // mock.ts e9 (Vercel): automatizado, not ruido, despite carrying the same
        // headers as the newsletter. What separates them is that this one gets opened.
        var f = Facts(
            """
            From: Vercel <no-reply@vercel.com>
            To: kevin@sagetasolutions.com
            Subject: Despliegue correcto
            List-Unsubscribe: <mailto:unsub@vercel.com>

            Cuerpo
            """);

        var ctx = new SignalContext { OwnAddresses = Me, SenderReceived = 212, SenderOpened = 180 };
        var c = SignalBaseline.Classify(DeterministicSignals.Compute(f, ctx), ctx);

        Assert.Equal(Bucket.Automatizado, c.Bucket);
    }

    [Fact]
    public void A_personal_signal_outranks_machine_markers()
    {
        // mock.ts e5: GitHub is an automated sender and still asks for a review.
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
            OwnAddresses = Me,
            SenderReceived = 300,
            SenderReplied = 4,
            IParticipatesInThread = true,
        };

        var c = SignalBaseline.Classify(DeterministicSignals.Compute(f, ctx), ctx);

        Assert.Equal(Bucket.Responder, c.Bucket);
    }

    [Fact]
    public void Being_in_Cc_is_a_negative_signal()
    {
        // mock.ts e6: informativo, with "Vas en Cc:, no en Para:" as negative.
        var f = Facts(
            """
            From: Marta Ruiz <marta.ruiz@universidadsur.edu>
            To: comite@universidadsur.edu
            Cc: kevin@sagetasolutions.com
            Subject: Acta de la reunion

            Cuerpo
            """);

        var ctx = new SignalContext { OwnAddresses = Me, SenderReceived = 12, SenderOpened = 9 };
        var signals = DeterministicSignals.Compute(f, ctx);
        var c = SignalBaseline.Classify(signals, ctx);

        Assert.Contains(signals, s => s.Kind == SignalKind.CcNotTo && s.Weight < 0);
        Assert.Equal("neg", signals.First(s => s.Kind == SignalKind.CcNotTo).ToSignal().Sign);
        Assert.Equal(Bucket.Informativo, c.Bucket);
    }

    [Fact]
    public void A_third_unanswered_message_carries_the_maximum_weight()
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
            OwnAddresses = Me,
            SenderReceived = 47,
            SenderReplied = 20,
            ConsecutiveUnanswered = 3,
            CleanBody = "Seguimos sin respuesta del equipo tecnico. Hay alguna novedad?",
        };

        var signals = DeterministicSignals.Compute(f, ctx);
        var c = SignalBaseline.Classify(signals, ctx);

        Assert.Contains(signals, s => s.Kind == SignalKind.ThirdUnanswered && s.Weight == 3);
        Assert.Equal(Bucket.Responder, c.Bucket);
        Assert.True(c.Confidence >= 0.9, $"confidence {c.Confidence}");
    }

    [Fact]
    public void Never_more_than_six_signals_and_always_ordered_by_weight()
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
            OwnAddresses = Me,
            OrgDomain = "sagetasolutions.com",
            SenderReceived = 60,
            SenderReplied = 31,
            IParticipatesInThread = true,
            ConsecutiveUnanswered = 3,
            CleanBody = "Puedes revisarlo?",
        };

        var signals = DeterministicSignals.Compute(f, ctx);

        Assert.Equal(6, signals.Count);

        var weights = signals.Select(s => Math.Abs(s.Weight)).ToArray();
        Assert.Equal(weights.OrderByDescending(w => w), weights);
    }

    [Fact]
    public void With_little_history_it_does_not_claim_you_never_replied()
    {
        // "Nunca lo has respondido (0 de 2)" says nothing: there may never have been
        // an occasion.
        var f = Facts(
            """
            From: Lucia <lucia@herrera-asesoria.es>
            To: kevin@sagetasolutions.com

            Cuerpo
            """);

        var ctx = new SignalContext { OwnAddresses = Me, SenderReceived = 2, SenderReplied = 0 };
        var signals = DeterministicSignals.Compute(f, ctx);

        Assert.DoesNotContain(signals, s => s.Kind == SignalKind.NeverReplied);
    }

    [Fact]
    public void A_rhetorical_question_at_the_start_does_not_count()
    {
        var f = Facts(
            """
            From: Zentrix <newsletter@zentrix.io>
            To: kevin@sagetasolutions.com

            Cuerpo
            """);

        var ctx = new SignalContext
        {
            OwnAddresses = Me,
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
