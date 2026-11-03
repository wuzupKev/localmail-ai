# -*- coding: utf-8 -*-
"""Genera ficheros .eml de prueba en backend/fixtures/.

Cuaderno de laboratorio: NO se distribuye. El producto solo sabe leer .eml de una
carpeta (`dotnet run --project src/LocalMailAI.Api -- --seed fixtures`); los datos de
ejemplo viven fuera de el.

Los casos son los de src/lib/mock.ts, pero con las cabeceras que de verdad disparan
las senales de §6: List-Unsubscribe, Auto-Submitted, Precedence, Authentication-Results,
To frente a Cc, y References para formar hilos. Un .eml sin esas cabeceras no probaria
nada del triage.

Sirven para dos cosas:
  - poblar la bandeja sin cuenta IMAP, para ver el front contra el backend real;
  - material de inspeccion para el hito 3 (normalizacion), con HTML, citas y firmas.
"""
import email.utils
import pathlib
from email.message import EmailMessage

YO = "kevin@sagetasolutions.com"
DESTINO = pathlib.Path(__file__).resolve().parent.parent / "fixtures"

# (nombre, remitente, asunto, cuerpo, es_html, cabeceras, desplazamiento_horas, hilo)
CASOS = [
    (
        "01-ana-reunion",
        ("Ana Molina", "ana.molina@estudiolegal.es"),
        "Re: Confirmacion reunion del jueves",
        "Hola Kevin,\n\n¿Te viene bien a las 10 en lugar de las 9? Me ha surgido una vista a primera hora "
        "y no llego a tiempo.\n\nSi no puedes, lo dejamos para el viernes a la misma hora. Dime algo cuando "
        "puedas y lo cierro con el resto del equipo.\n\nUn saludo,\nAna\n\n"
        "El 1 de noviembre de 2026, 18:02, Kevin Chavarria <kevin@sagetasolutions.com> escribió:\n"
        "> Te confirmo el jueves a las 9.\n> Un saludo\n",
        False,
        {"Authentication-Results": "mx.google.com; dkim=pass header.i=@estudiolegal.es; spf=pass"},
        -2,
        "hilo-reunion-jueves",
    ),
    (
        "02-factura-nordia",
        ("Facturacion Nordia", "facturacion@nordia-cloud.com"),
        "Aviso de vencimiento - factura F-2291",
        "Estimado cliente,\n\nLe recordamos que la factura F-2291 por importe de 348,00 € vence el "
        "10/11/2026.\n\nPuede realizar el pago desde su área de cliente o mediante transferencia a la "
        "cuenta habitual.\n",
        False,
        {},
        -3,
        None,
    ),
    (
        "03-diego-q3",
        ("Diego Ferran", "d.ferran@sagetasolutions.com"),
        "Propuesta migracion - faltan tus numeros del Q3",
        "Kevin,\n\nNecesito los datos de consumo del Q3 antes del viernes para cerrar la propuesta del "
        "cliente.\n\nCon lo que me pasaste de Q2 no me cuadra el crecimiento, ¿puedes revisarlo?\n\n"
        "Gracias,\nDiego\n\n--\nDiego Ferrán | Sageta Solutions\nTel. +34 611 22 33 44 | "
        "d.ferran@sagetasolutions.com\nAviso legal: este mensaje y sus anexos son confidenciales.\n",
        False,
        {"Authentication-Results": "mx.google.com; dkim=pass; spf=pass"},
        -26,
        "hilo-propuesta-migracion",
    ),
    (
        "04-lucia-justificantes",
        ("Lucia Herrera", "lucia@herrera-asesoria.es"),
        "Documentacion trimestral - faltan dos justificantes",
        "Buenos días Kevin,\n\nHe revisado lo que me mandaste y faltan los justificantes de octubre "
        "(dos gastos de software).\n\nSin ellos no puedo presentar el trimestral. ¿Me los puedes enviar "
        "esta semana?\n",
        False,
        {},
        -28,
        "hilo-trimestral",
    ),
    (
        "05-github-pr",
        ("GitHub", "notifications@github.com"),
        "[localmail-ai] PR #14: normalizador de citas - revision solicitada",
        "<p>@mrodriguez ha solicitado tu revisión en el pull request "
        '<a href="https://github.com/kevin/localmail-ai/pull/14">#14</a> '
        "(Normalizador de citas y firmas).</p><p>12 archivos modificados, +431 -87.</p>",
        True,
        {"List-Id": "<localmail-ai.kevin.github.com>", "Auto-Submitted": "auto-generated"},
        -4,
        None,
    ),
    (
        "06-acta-comite",
        ("Marta Ruiz", "marta.ruiz@universidadsur.edu"),
        "Acta de la reunion del comite - 28 de octubre",
        "Hola a todos,\n\nAdjunto el acta de la reunión del comité del 28 de octubre.\n\n"
        "No requiere acción por vuestra parte, es solo para que quede constancia.\n",
        False,
        {},
        -1,
        None,
    ),
    (
        "07-resumen-semanal",
        ("Equipo Sageta", "interno@sagetasolutions.com"),
        "Resumen semanal del equipo - semana 44",
        "Entregas cerradas: 7.\n\nIncidencias abiertas: 3.\n\nPróxima demo: 12 de noviembre.\n",
        False,
        {"Precedence": "bulk"},
        -50,
        None,
    ),
    (
        "08-banco-movimiento",
        ("Banco Iberico", "avisos@bancoiberico.es"),
        "Movimiento en tu cuenta terminada en 4471",
        "Se ha registrado un cargo de 89,90 € el 02/11/2026 en tu cuenta terminada en 4471.\n",
        False,
        {
            "Authentication-Results": "mx.google.com; dkim=pass header.i=@bancoiberico.es; spf=pass",
            "Auto-Submitted": "auto-generated",
        },
        -52,
        None,
    ),
    (
        "09-vercel-despliegue",
        ("Vercel", "no-reply@vercel.com"),
        "Despliegue correcto - localmail-ai (produccion)",
        "Tu despliegue se ha completado correctamente en 48 s.\n",
        False,
        {"List-Unsubscribe": "<https://vercel.com/unsub>", "Auto-Submitted": "auto-generated"},
        -5,
        None,
    ),
    (
        "10-jira-ticket",
        ("Atlassian Jira", "jira@sageta.atlassian.net"),
        "SAG-1182 cambio de estado a En revision",
        "El ticket SAG-1182 ha cambiado de estado a En revisión.\n",
        False,
        {"Auto-Submitted": "auto-generated", "Precedence": "bulk"},
        -5,
        None,
    ),
    (
        "11-docker-limite",
        ("Docker Hub", "noreply@docker.com"),
        "Tu limite de pulls se reinicia en 6 horas",
        "Has usado 180 de 200 pulls anónimos este mes.\n",
        False,
        {"List-Unsubscribe": "<https://docker.com/unsub>"},
        -55,
        None,
    ),
    (
        "12-zentrix-newsletter",
        ("Marketing Zentrix", "newsletter@zentrix.io"),
        "7 formas de multiplicar tu productividad con IA",
        "<html><body><h1>7 formas de multiplicar tu productividad</h1>"
        "<p>Descubre las herramientas que están cambiando la forma de trabajar.</p>"
        '<p><a href="https://zentrix.io/promo">Leer más</a></p>'
        "<style>.cta{color:red}</style></body></html>",
        True,
        {"List-Unsubscribe": "<mailto:unsub@zentrix.io>", "Precedence": "bulk"},
        -6,
        None,
    ),
    (
        "13-tecnotienda-oferta",
        ("Ofertas Tecnotienda", "ofertas@tecnotienda.es"),
        "Ultima oportunidad: -40% solo hoy",
        "Tu carrito te espera. Aprovecha antes de medianoche.\n",
        False,
        {"List-Unsubscribe": "<mailto:baja@tecnotienda.es>"},
        -7,
        None,
    ),
    (
        "14-deves-earlybird",
        ("Conferencia DevES", "info@deves-conf.org"),
        "Quedan 3 dias para el early bird",
        "No te pierdas el descuento de inscripción anticipada.\n",
        False,
        {"List-Unsubscribe": "<mailto:baja@deves-conf.org>"},
        -72,
        None,
    ),
    (
        "15-proveedor-solicitud",
        ("soporte@proveedor-nuevo.com", "soporte@proveedor-nuevo.com"),
        "Su solicitud de acceso ha sido registrada",
        "Hemos recibido su solicitud. Un agente la revisará en 48 horas.\n",
        False,
        {"Authentication-Results": "mx.google.com; spf=fail smtp.mailfrom=proveedor-nuevo.com"},
        -1,
        None,
    ),
    (
        "16-pablo-incidencia",
        ("Pablo Esteban", "pablo.esteban@clientemayor.com"),
        "Re: Re: Incidencia en produccion - seguimiento",
        "Buenos días,\n\nSeguimos sin respuesta del equipo técnico sobre la incidencia del lunes. "
        "¿Hay alguna novedad?\n\nEs el tercer correo que envío sobre esto.\n",
        False,
        {},
        -3,
        "hilo-incidencia-produccion",
    ),
]

# Los tres mensajes anteriores de Pablo, para que "Tercer mensaje sin respuesta tuya"
# tenga de donde salir: la senal depende del estado del hilo, no de un solo correo.
SEGUIMIENTO = [
    ("16a-pablo-incidencia-1", "Incidencia en produccion",
     "Buenos días,\n\nTenemos el servicio de facturación caído desde esta mañana.\n", -120),
    ("16b-pablo-incidencia-2", "Re: Incidencia en produccion",
     "¿Alguna novedad sobre la incidencia? Seguimos sin servicio.\n", -72),
]


def construir(nombre, remitente, asunto, cuerpo, es_html, cabeceras, horas, hilo, en_cc=False):
    msg = EmailMessage()
    msg["From"] = email.utils.formataddr(remitente)
    if en_cc:
        msg["To"] = "comite@universidadsur.edu"
        msg["Cc"] = YO
    else:
        msg["To"] = YO
    msg["Subject"] = asunto
    msg["Date"] = email.utils.format_datetime(
        email.utils.parsedate_to_datetime("Tue, 03 Nov 2026 12:00:00 +0100")
        + __import__("datetime").timedelta(hours=horas)
    )
    msg["Message-ID"] = f"<{nombre}@localmail.fixtures>"

    if hilo:
        msg["References"] = f"<{hilo}@localmail.fixtures>"
        msg["In-Reply-To"] = f"<{hilo}@localmail.fixtures>"

    for k, v in cabeceras.items():
        msg[k] = v

    if es_html:
        msg.set_content("Versión en texto no disponible.")
        msg.add_alternative(cuerpo, subtype="html")
    else:
        msg.set_content(cuerpo)

    return msg


def main() -> None:
    DESTINO.mkdir(parents=True, exist_ok=True)
    for viejo in DESTINO.glob("*.eml"):
        viejo.unlink()

    escritos = 0

    for nombre, remitente, asunto, cuerpo, es_html, cabeceras, horas, hilo in CASOS:
        # El acta del comite llega en Cc: es la senal negativa de mock.ts e6.
        en_cc = nombre.startswith("06-")
        msg = construir(nombre, remitente, asunto, cuerpo, es_html, cabeceras, horas, hilo, en_cc)
        (DESTINO / f"{nombre}.eml").write_bytes(msg.as_bytes())
        escritos += 1

    for nombre, asunto, cuerpo, horas in SEGUIMIENTO:
        msg = construir(nombre, ("Pablo Esteban", "pablo.esteban@clientemayor.com"),
                        asunto, cuerpo, False, {}, horas, "hilo-incidencia-produccion")
        (DESTINO / f"{nombre}.eml").write_bytes(msg.as_bytes())
        escritos += 1

    print(f"{escritos} ficheros .eml en {DESTINO}")


if __name__ == "__main__":
    main()
