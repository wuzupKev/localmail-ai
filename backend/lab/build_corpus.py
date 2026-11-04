# -*- coding: utf-8 -*-
"""Builds the milestone 0 parity corpus (BACKEND.md §10).

Lab notebook: NOT shipped. The product is pure .NET.

The corpus deliberately mixes two things:
  - Realistic mail text taken from src/lib/mock.ts (bodies, subjects, senders).
    This is the real Spanish the model will see in production.
  - Edge cases that actually break a SentencePiece tokenizer: decomposed
    diacritics (NFD) versus precomposed (NFC), non-breaking and zero-width
    spaces, mojibake, emoji with modifiers, URLs, amounts, CJK and Cyrillic
    (the model is multilingual), and the "query:"/"passage:" prefixes e5
    requires, which therefore DO reach the tokenizer in production.

The sample texts stay in Spanish on purpose: they are the mail the tokenizer has
to handle, and translating them would stop testing what matters.

Output: corpus.json with exactly 100 {id, text} entries.
"""
import json
import pathlib

# --- 1. Realistic mail text (source: src/lib/mock.ts) -------------------------

BODIES = [
    "Hola Kevin,\nTe viene bien a las 10 en lugar de las 9? Me ha surgido una vista a primera hora y no llego a tiempo.\nSi no puedes, lo dejamos para el viernes a la misma hora. Dime algo cuando puedas y lo cierro con el resto del equipo.\nUn saludo,\nAna",
    "Estimado cliente,\nLe recordamos que la factura F-2291 por importe de 348,00 EUR vence el 10/11/2026.\nPuede realizar el pago desde su area de cliente o mediante transferencia a la cuenta habitual.",
    "Kevin,\nNecesito los datos de consumo del Q3 antes del viernes para cerrar la propuesta del cliente.\nCon lo que me pasaste de Q2 no me cuadra el crecimiento, puedes revisarlo?\nGracias,\nDiego",
    "Buenos dias Kevin,\nHe revisado lo que me mandaste y faltan los justificantes de octubre (dos gastos de software).\nSin ellos no puedo presentar el trimestral. Me los puedes enviar esta semana?",
    "@mrodriguez ha solicitado tu revision en el pull request #14 (Normalizador de citas y firmas).\n12 archivos modificados, +431 -87.",
    "Hola a todos,\nAdjunto el acta de la reunion del comite del 28 de octubre.\nNo requiere accion por vuestra parte, es solo para que quede constancia.",
    "Entregas cerradas: 7.\nIncidencias abiertas: 3.\nProxima demo: 12 de noviembre.",
    "Se ha registrado un cargo de 89,90 EUR el 02/11/2026 en tu cuenta terminada en 4471.",
    "Tu despliegue se ha completado correctamente en 48 s.",
    "El ticket SAG-1182 ha cambiado de estado a En revision.",
    "Has usado 180 de 200 pulls anonimos este mes.",
    "Descubre las herramientas que estan cambiando la forma de trabajar.",
    "Tu carrito te espera. Aprovecha antes de medianoche.",
    "No te pierdas el descuento de inscripcion anticipada.",
    "Hemos recibido su solicitud. Un agente la revisara en 48 horas.",
    "Buenos dias,\nSeguimos sin respuesta del equipo tecnico sobre la incidencia del lunes. Hay alguna novedad?\nEs el tercer correo que envio sobre esto.",
]

SUBJECTS = [
    "Re: Confirmacion reunion del jueves",
    "Aviso de vencimiento - factura F-2291",
    "Propuesta migracion - faltan tus numeros del Q3",
    "Documentacion trimestral - faltan dos justificantes",
    "[localmail-ai] PR #14: normalizador de citas - revision solicitada",
    "Acta de la reunion del comite - 28 de octubre",
    "Resumen semanal del equipo - semana 44",
    "Movimiento en tu cuenta terminada en 4471",
    "Despliegue correcto - localmail-ai (produccion)",
    "SAG-1182 cambio de estado a En revision",
    "Tu limite de pulls se reinicia en 6 horas",
    "7 formas de multiplicar tu productividad con IA",
    "Ultima oportunidad: -40% solo hoy",
    "Quedan 3 dias para el early bird",
    "Su solicitud de acceso ha sido registrada",
    "Re: Re: Incidencia en produccion - seguimiento",
]

SENDERS = [
    "Ana Molina <ana.molina@estudiolegal.es>",
    "Facturacion Nordia <facturacion@nordia-cloud.com>",
    "Diego Ferran <d.ferran@sagetasolutions.com>",
    "Lucia Herrera <lucia@herrera-asesoria.es>",
    "GitHub <notifications@github.com>",
    "Marta Ruiz <marta.ruiz@universidadsur.edu>",
    "Atlassian Jira <jira@sageta.atlassian.net>",
    "soporte@proveedor-nuevo.com",
]

# Spanish with real accents: mock.ts strips them, but real mail does NOT. This is
# exactly the kind of text where a badly loaded tokenizer drifts.
ACCENTED = [
    "\u00bfTe viene bien a las 10 en lugar de las 9? Me ha surgido una vista a primera hora y no llego a tiempo.",
    "Estimado cliente: le recordamos que la factura F-2291 por importe de 348,00 \u20ac vence el 10/11/2026.",
    "Necesito los datos de consumo del Q3 antes del viernes. \u00bfPuedes revisarlo? Con lo de Q2 no me cuadra.",
    "A\u00f1ado a Bego\u00f1a y a \u00cd\u00f1igo en copia. La reuni\u00f3n ser\u00e1 el mi\u00e9rcoles a las 9:30 en la sala peque\u00f1a.",
    "Adjunto el acta de la reuni\u00f3n del comit\u00e9. No requiere acci\u00f3n por vuestra parte, es s\u00f3lo constancia.",
    "Seguimos sin respuesta del equipo t\u00e9cnico sobre la incidencia del lunes. \u00bfHay alguna novedad?",
    "Hola Kevin: el env\u00edo lleg\u00f3 roto y el d\u00eda 3 vence el plazo de reclamaci\u00f3n. \u00bfLo gestionas t\u00fa o yo?",
    "Se\u00f1ores: ruego confirmaci\u00f3n de recepci\u00f3n. Atentamente, Jos\u00e9 \u00c1ngel Mu\u00f1oz Pe\u00f1a \u2014 Dpto. Administraci\u00f3n",
]

QUOTES_AND_SIGNATURES = [
    "El 2 de noviembre de 2026, 9:14, Ana Molina <ana.molina@estudiolegal.es> escribi\u00f3:\n> Te confirmo la reuni\u00f3n del jueves a las 9.\n> Un saludo,\n> Ana",
    "-----Original Message-----\nFrom: Diego Ferran\nSent: Monday, November 2, 2026 8:02 AM\nTo: Kevin Chavarria\nSubject: RE: Propuesta\n\nNecesito los numeros del Q3.",
    "Gracias,\nDiego\n\n--\nDiego Ferr\u00e1n | Sageta Solutions\nTel. +34 611 22 33 44 | d.ferran@sagetasolutions.com\nAviso legal: este mensaje y sus anexos son confidenciales.",
    "On Mon, Nov 2, 2026 at 8:02 AM Pablo Esteban <pablo.esteban@clientemayor.com> wrote:\n>> Es el tercer correo que env\u00edo sobre esto.\n>> Un saludo",
    "Sent from my iPhone\n\n> El 1 nov 2026, a las 22:10, soporte@proveedor-nuevo.com escribi\u00f3:\n>\n> Hemos recibido su solicitud.",
]

# --- 2. Edge cases: what actually shifts the ids ------------------------------

EDGE = [
    # Unicode: precomposed (NFC) versus decomposed (NFD). The same text to the
    # eye, different bytes. If the tokenizer's normaliser is not applied the same
    # way in C# as in Python, this is where it shows.
    "Facturaci\u00f3n pendiente: a\u00f1\u00e1deme en copia, Bego\u00f1a.",
    "Facturacio\u0301n pendiente: an\u0303a\u0301deme en copia, Bego\u0301n\u0303a.",
    "MAY\u00daSCULAS CON ACENTO: \u00c1\u00c9\u00cd\u00d3\u00da\u00d1\u00dc \u00bfPOR QU\u00c9 GRITAS?",
    "\ufb00 \ufb01 ligaduras y \u2460 \u2461 numeros encerrados y \u00bd fracciones",
    "\u216b numeral romano, \u33a1 metro cuadrado, \u2105 care of",
    # Unusual spaces: non-breaking, thin, zero-width, tab, BOM.
    "Importe:\u00a0348,00\u00a0\u20ac (espacio no separable)",
    "Firma\u2009con\u2009espacio\u2009fino y \u200bancho cero\u200b dentro",
    "Columna1\tColumna2\tColumna3\nValor1\tValor2\tValor3",
    "\ufeffAsunto con BOM al principio",
    "Multiples     espacios      seguidos   y\n\n\nsaltos\n\n\nrepetidos",
    # Mojibake: UTF-8 read as latin-1. MimeKit will produce this from the lying
    # charsets §11 warns about.
    "Facturaci\u00c3\u00b3n pendiente, a\u00c3\u00b1ade a Bego\u00c3\u00b1a en copia",
    "\u00c2\u00bfQu\u00c3\u00a9 tal? El d\u00c3\u00ada 3 vence el plazo",
    "Reuni\u00f3n\u001b(B rota con secuencia de escape",
    # Emoji, including ZWJ-composed ones and skin tone modifiers.
    "Ultima oportunidad: -40% solo hoy \U0001f525\U0001f525 \u00a1corre!",
    "Enhorabuena \U0001f468\u200d\U0001f469\u200d\U0001f467\u200d\U0001f466 por el nacimiento \U0001f389 \u2014 del equipo \U0001f44b\U0001f3fd",
    "Reuni\u00f3n \u2705 confirmada, documento \U0001f4ce adjunto, \u26a0\ufe0f revisa el plazo",
    "\U0001f1ea\U0001f1f8 Equipo Espa\u00f1a vs \U0001f1ef\U0001f1f5 equipo Jap\u00f3n",
    # Identifiers, URLs, paths: here mail is more "code" than "language".
    "Confirma en https://nordia-cloud.com/area-cliente/facturas?id=F-2291&utm_source=email&utm_campaign=aviso",
    "Clona con git@github.com:kevin/localmail-ai.git y revisa src/lib/mock.ts:142",
    "Ruta del log: C:\\Users\\KEVIN CHAVARRIA\\AppData\\Local\\LocalMailAI\\localmail.db",
    "Message-ID: <CAF8vZ1k_9mQ2pR3nT4@mail.gmail.com> In-Reply-To: <a1b2c3d4@estudiolegal.es>",
    "Cabecera: List-Unsubscribe: <mailto:unsub@zentrix.io>, <https://zentrix.io/u/9f2a1b>",
    "IBAN ES91 2100 0418 4502 0005 1332 \u2014 BIC CAIXESBBXXX \u2014 ref. F-2291/2026",
    "Importes: 348,00 \u20ac / $1,299.99 / \u00a345.50 / \u00a512.000 / 1.234.567,89",
    "Fechas: 10/11/2026, 2026-11-10, 10-nov-2026, jueves 10 de noviembre de 2026",
    "Telefonos: +34 611 22 33 44, 0034911234567, 611-22-33-44",
    "SAG-1182, PR #14, ticket 00123456, v0.1.0, commit 3f2376a",
    # Multilingual: the model is multilingual-e5-small, not Spanish only.
    "Please find attached the Q3 consumption data. Let me know if anything is unclear.",
    "Bonjour, je vous confirme la r\u00e9union de jeudi \u00e0 10h. Cordialement.",
    "Guten Tag, die Rechnung F-2291 \u00fcber 348,00 EUR ist am 10.11.2026 f\u00e4llig.",
    "Ol\u00e1, segue em anexo a ata da reuni\u00e3o do comit\u00e9 de 28 de outubro.",
    "Buongiorno, confermo la riunione di gioved\u00ec alle 10. Saluti.",
    "\u8bf7\u67e5\u6536\u7b2c\u4e09\u5b63\u5ea6\u7684\u6d88\u8017\u6570\u636e\uff0c\u5468\u4e94\u4e4b\u524d\u9700\u8981\u786e\u8ba4\u3002\u8c22\u8c22\u3002",
    "\u7b2c3\u56db\u534a\u671f\u306e\u6d88\u8cbb\u30c7\u30fc\u30bf\u3092\u6dfb\u4ed8\u3057\u307e\u3059\u3002\u91d1\u66dc\u65e5\u307e\u3067\u306b\u3054\u78ba\u8a8d\u304f\u3060\u3055\u3044\u3002",
    "\u0417\u0434\u0440\u0430\u0432\u0441\u0442\u0432\u0443\u0439\u0442\u0435! \u041f\u043e\u0434\u0442\u0432\u0435\u0440\u0436\u0434\u0430\u044e \u0432\u0441\u0442\u0440\u0435\u0447\u0443 \u0432 \u0447\u0435\u0442\u0432\u0435\u0440\u0433 \u0432 10:00. \u0421 \u0443\u0432\u0430\u0436\u0435\u043d\u0438\u0435\u043c.",
    "\u0645\u0631\u062d\u0628\u0627\u060c \u0623\u0631\u0641\u0642 \u0644\u0643 \u0628\u064a\u0627\u0646\u0627\u062a \u0627\u0644\u0631\u0628\u0639 \u0627\u0644\u062b\u0627\u0644\u062b. \u064a\u0631\u062c\u0649 \u0627\u0644\u062a\u0623\u0643\u064a\u062f \u0642\u0628\u0644 \u0627\u0644\u062c\u0645\u0639\u0629.",
    "\u039a\u03b1\u03bb\u03b7\u03bc\u03ad\u03c1\u03b1, \u03b5\u03c0\u03b9\u03b2\u03b5\u03b2\u03b1\u03b9\u03ce\u03bd\u03c9 \u03c4\u03b7 \u03c3\u03c5\u03bd\u03ac\u03bd\u03c4\u03b7\u03c3\u03b7 \u03c4\u03b7\u03c2 \u03a0\u03ad\u03bc\u03c0\u03c4\u03b7\u03c2 \u03c3\u03c4\u03b9\u03c2 10.",
    # e5 prefixes: these reach the tokenizer in production (§8). If they break
    # here, the whole of search breaks, silently.
    "query: correos de Ana sobre la reuni\u00f3n del jueves",
    "query: factura F-2291 vencimiento",
    "passage: Te viene bien a las 10 en lugar de las 9? Me ha surgido una vista a primera hora.",
    "passage: Le recordamos que la factura F-2291 por importe de 348,00 \u20ac vence el 10/11/2026.",
    # Degenerate cases: what breaks the tokenizer's boundaries.
    "",
    " ",
    "\n",
    "a",
    "\u00bf",
    "\u20ac",
    "\U0001f525",
    "A" * 600,
    "palabrasinespacios" * 30,
    "-" * 120,
    "=?UTF-8?B?RmFjdHVyYWNpw7NuIHBlbmRpZW50ZQ==?=",
    "<p>Hola <b>Kevin</b>,</p><p>&iquest;Te viene bien a las 10?</p>&nbsp;&amp;&lt;&gt;",
]


def main() -> None:
    texts: list[str] = []
    for group in (BODIES, SUBJECTS, SENDERS, ACCENTED, QUOTES_AND_SIGNATURES, EDGE):
        texts.extend(group)

    # Deduplicated, order preserved: a duplicate adds no coverage.
    seen: set[str] = set()
    unique: list[str] = []
    for t in texts:
        if t not in seen:
            seen.add(t)
            unique.append(t)

    if len(unique) < 100:
        raise SystemExit(f"corpus too small: {len(unique)} unique, 100 required")

    corpus = [{"id": f"c{i:03d}", "text": t} for i, t in enumerate(unique[:100])]

    destination = pathlib.Path(__file__).with_name("corpus.json")
    destination.write_text(json.dumps(corpus, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"corpus.json: {len(corpus)} texts ({len(unique)} unique available)")


if __name__ == "__main__":
    main()
