// Datos falsos para el mockup de LocalMail AI.
// Nada de esto viene de un backend: sirve para discutir la interfaz, no para funcionar.

export type Bucket = "responder" | "informativo" | "automatizado" | "ruido";

export type BucketDef = { id: Bucket; label: string; short: string; bg: string; ink: string; key: string };

export const BUCKETS: BucketDef[] = [
  { id: "responder", label: "Needs a reply", short: "Reply", bg: "var(--t-responder-bg)", ink: "var(--t-responder-ink)", key: "1" },
  { id: "informativo", label: "FYI", short: "FYI", bg: "var(--t-info-bg)", ink: "var(--t-info-ink)", key: "2" },
  { id: "automatizado", label: "Automated", short: "Automated", bg: "var(--t-auto-bg)", ink: "var(--t-auto-ink)", key: "3" },
  { id: "ruido", label: "Noise", short: "Noise", bg: "var(--t-ruido-bg)", ink: "var(--t-ruido-ink)", key: "4" },
];

/** Senal determinista: barata, explicable en una frase. El motivo por el que el triage se entiende. */
export type Signal = { label: string; sign: "pos" | "neg" | "neutral" };

export type Email = {
  id: string;
  from: { name: string; email: string };
  subject: string;
  preview: string;
  body: string[];
  time: string;
  bucket: Bucket;
  /** 0..1 - por debajo del umbral el correo cae en "No estoy seguro" en vez de adivinar. */
  confidence: number;
  signals: Signal[];
  threadCount: number;
  unread: boolean;
  /** Extraccion estructurada simulada (lo que devolveria el SLM con esquema JSON forzado). */
  extracted?: { accion: string; urgencia: "alta" | "media" | "baja"; fechaLimite?: string };
  /** Resumen simulado: solo para hilos largos, y en el producto real se pide bajo demanda. */
  summary?: string;
};

export const EMAILS: Email[] = [
  {
    id: "e1",
    from: { name: "Ana Molina", email: "ana.molina@legalpartners.com" },
    subject: "Re: Confirming Thursday meeting",
    preview: "Could we do 10 instead of 9? A hearing came up first thing...",
    body: [
      "Hi Kevin,",
      "Could we do 10 instead of 9? A hearing came up first thing and I will not make it.",
      "If you cannot, lets move it to Friday, same time. Let me know and I will lock it in with the team.",
      "Best,\nAna",
    ],
    time: "9:14",
    bucket: "responder",
    confidence: 0.94,
    signals: [
      { label: "You have replied 12 times", sign: "pos" },
      { label: "You are on To:, not Cc:", sign: "pos" },
      { label: "Ends with a direct question", sign: "pos" },
      { label: "A thread you are already in", sign: "pos" },
    ],
    threadCount: 4,
    unread: true,
    extracted: { accion: "Confirm whether Thursday at 10 works", urgencia: "alta", fechaLimite: "2026-11-05" },
  },
  {
    id: "e2",
    from: { name: "Nordia Billing", email: "billing@nordia-cloud.com" },
    subject: "Payment due - invoice F-2291",
    preview: "A reminder that invoice F-2291 for EUR 348.00 is due on 10/11...",
    body: [
      "Dear customer,",
      "A reminder that invoice F-2291 for EUR 348.00 is due on 10/11/2026.",
      "You can pay from your account area or by transfer to the usual account.",
    ],
    time: "8:47",
    bucket: "responder",
    confidence: 0.48,
    signals: [
      { label: "New sender, no history", sign: "neutral" },
      { label: "No List-Unsubscribe", sign: "pos" },
      { label: "Contains an amount and a due date", sign: "pos" },
      { label: "A no-reply address", sign: "neg" },
    ],
    threadCount: 1,
    unread: true,
    extracted: { accion: "Pay invoice F-2291 (EUR 348.00)", urgencia: "media", fechaLimite: "2026-11-10" },
  },
  {
    id: "e3",
    from: { name: "Diego Ferran", email: "d.ferran@sagetasolutions.com" },
    subject: "Migration proposal - still missing your Q3 numbers",
    preview: "I need the Q3 usage data before Friday to close the proposal...",
    body: [
      "Kevin,",
      "I need the Q3 usage data before Friday to close the client proposal.",
      "The growth does not add up against the Q2 figures you sent. Can you check?",
      "Thanks,\nDiego",
    ],
    time: "Yesterday",
    bucket: "responder",
    confidence: 0.91,
    signals: [
      { label: "You have replied 31 times", sign: "pos" },
      { label: "Same domain as your organisation", sign: "pos" },
      { label: "Explicit request for data", sign: "pos" },
    ],
    threadCount: 9,
    unread: false,
    extracted: { accion: "Send the Q3 usage data", urgencia: "alta", fechaLimite: "2026-11-06" },
    summary:
      "A 9-message thread about the Nordia migration proposal. Scope and price were agreed on 22/10. Diego blocked the send because the Q2 figures do not match the projected growth. Outstanding: you need to send the Q3 usage before Friday.",
  },
  {
    id: "e4",
    from: { name: "Lucia Herrera", email: "lucia@herrera-tax.com" },
    subject: "Quarterly filing - two receipts missing",
    preview: "I went through what you sent and the October receipts are missing...",
    body: [
      "Morning Kevin,",
      "I went through what you sent and the October receipts are missing (two software expenses).",
      "Without them I cannot file the quarter. Can you send them this week?",
    ],
    time: "Yesterday",
    bucket: "responder",
    confidence: 0.88,
    signals: [
      { label: "You have replied 8 times", sign: "pos" },
      { label: "Explicit request", sign: "pos" },
      { label: "You are on To:", sign: "pos" },
    ],
    threadCount: 3,
    unread: false,
    extracted: { accion: "Send the October expense receipts", urgencia: "media" },
  },
  {
    id: "e5",
    from: { name: "GitHub", email: "notifications@github.com" },
    subject: "[localmail-ai] PR #14: quote normalizer - review requested",
    preview: "@mrodriguez requested your review on pull request #14...",
    body: [
      "@mrodriguez requested your review on pull request #14 (Quote and signature normalizer).",
      "12 files changed, +431 -87.",
    ],
    time: "7:02",
    bucket: "responder",
    confidence: 0.72,
    signals: [
      { label: "Direct mention of your username", sign: "pos" },
      { label: "Automated sender", sign: "neg" },
      { label: "You act on 64% of these alerts", sign: "pos" },
    ],
    threadCount: 1,
    unread: true,
    extracted: { accion: "Review pull request #14", urgencia: "media" },
  },
  {
    id: "e6",
    from: { name: "Marta Ruiz", email: "marta.ruiz@southuni.edu" },
    subject: "Committee meeting minutes - 28 October",
    preview: "Attached are the committee minutes. No action needed from you...",
    body: [
      "Hi all,",
      "Attached are the minutes from the 28 October committee meeting.",
      "No action needed from you, this is just for the record.",
    ],
    time: "11:30",
    bucket: "informativo",
    confidence: 0.86,
    signals: [
      { label: "You are on Cc:, not To:", sign: "neg" },
      { label: "Says plainly: no action needed", sign: "neg" },
      { label: "Sent to 14 recipients", sign: "neg" },
    ],
    threadCount: 1,
    unread: true,
  },
  {
    id: "e7",
    from: { name: "Sageta Team", email: "internal@sagetasolutions.com" },
    subject: "Weekly team digest - week 44",
    preview: "Shipped: 7. Open issues: 3. Next demo: 12 November...",
    body: ["Shipped: 7.", "Open issues: 3.", "Next demo: 12 November."],
    time: "Mon",
    bucket: "informativo",
    confidence: 0.79,
    signals: [
      { label: "Recurring weekly email", sign: "neutral" },
      { label: "You open it 70% of the time", sign: "pos" },
      { label: "You have never replied", sign: "neg" },
    ],
    threadCount: 1,
    unread: false,
  },
  {
    id: "e8",
    from: { name: "Northbank", email: "alerts@northbank.com" },
    subject: "Activity on your account ending 4471",
    preview: "A charge of EUR 89.90 was posted on 02/11/2026...",
    body: ["A charge of EUR 89.90 was posted on 02/11/2026 to your account ending 4471."],
    time: "Mon",
    bucket: "informativo",
    confidence: 0.83,
    signals: [
      { label: "Verified sender (DKIM + SPF)", sign: "pos" },
      { label: "Transactional notification", sign: "neutral" },
      { label: "You have never replied", sign: "neg" },
    ],
    threadCount: 1,
    unread: false,
  },
  {
    id: "e9",
    from: { name: "Vercel", email: "no-reply@vercel.com" },
    subject: "Deployment succeeded - localmail-ai (production)",
    preview: "Your deployment completed successfully in 48s...",
    body: ["Your deployment completed successfully in 48s."],
    time: "6:12",
    bucket: "automatizado",
    confidence: 0.96,
    signals: [
      { label: "A no-reply address", sign: "neg" },
      { label: "List-Unsubscribe header present", sign: "neg" },
      { label: "You have never replied (0 of 212)", sign: "neg" },
    ],
    threadCount: 1,
    unread: true,
  },
  {
    id: "e10",
    from: { name: "Atlassian Jira", email: "jira@sageta.atlassian.net" },
    subject: "SAG-1182 moved to In Review",
    preview: "Ticket SAG-1182 changed status...",
    body: ["Ticket SAG-1182 moved to In Review."],
    time: "6:40",
    bucket: "automatizado",
    confidence: 0.93,
    signals: [
      { label: "Automated sender", sign: "neg" },
      { label: "No direct mention of you", sign: "neg" },
      { label: "You have never replied", sign: "neg" },
    ],
    threadCount: 1,
    unread: false,
  },
  {
    id: "e11",
    from: { name: "Docker Hub", email: "noreply@docker.com" },
    subject: "Your pull limit resets in 6 hours",
    preview: "You have used 180 of 200 anonymous pulls this month...",
    body: ["You have used 180 of 200 anonymous pulls this month."],
    time: "Mon",
    bucket: "automatizado",
    confidence: 0.9,
    signals: [
      { label: "List-Unsubscribe present", sign: "neg" },
      { label: "You have never replied", sign: "neg" },
    ],
    threadCount: 1,
    unread: false,
  },
  {
    id: "e12",
    from: { name: "Zentrix Marketing", email: "newsletter@zentrix.io" },
    subject: "7 ways to multiply your productivity with AI",
    preview: "Discover the tools that are changing how people work...",
    body: ["Discover the tools that are changing how people work."],
    time: "5:30",
    bucket: "ruido",
    confidence: 0.97,
    signals: [
      { label: "List-Unsubscribe present", sign: "neg" },
      { label: "Never opened (0 of 38)", sign: "neg" },
      { label: "Emoji in the subject", sign: "neg" },
      { label: "Marketing-only domain", sign: "neg" },
    ],
    threadCount: 1,
    unread: true,
  },
  {
    id: "e13",
    from: { name: "Tecnostore Deals", email: "deals@tecnostore.com" },
    subject: "Last chance: -40% today only",
    preview: "Your cart is waiting. Act before midnight...",
    body: ["Your cart is waiting. Act before midnight."],
    time: "4:58",
    bucket: "ruido",
    confidence: 0.98,
    signals: [
      { label: "List-Unsubscribe present", sign: "neg" },
      { label: "Never opened (0 of 91)", sign: "neg" },
      { label: "Commercial urgency language", sign: "neg" },
    ],
    threadCount: 1,
    unread: true,
  },
  {
    id: "e14",
    from: { name: "DevES Conference", email: "info@deves-conf.org" },
    subject: "3 days left for early bird",
    preview: "Do not miss the early registration discount...",
    body: ["Do not miss the early registration discount."],
    time: "Sun",
    bucket: "ruido",
    confidence: 0.81,
    signals: [
      { label: "List-Unsubscribe present", sign: "neg" },
      { label: "Opened 2 of 11 times", sign: "neutral" },
    ],
    threadCount: 1,
    unread: false,
  },
  {
    id: "e15",
    from: { name: "support@new-vendor.com", email: "support@new-vendor.com" },
    subject: "Your access request has been logged",
    preview: "We received your request. An agent will review it within 48 hours...",
    body: ["We received your request. An agent will review it within 48 hours."],
    time: "10:05",
    bucket: "informativo",
    confidence: 0.41,
    signals: [
      { label: "New sender, no history", sign: "neutral" },
      { label: "No List-Unsubscribe", sign: "pos" },
      { label: "Sender name same as address", sign: "neutral" },
    ],
    threadCount: 1,
    unread: true,
  },
  {
    id: "e16",
    from: { name: "Pablo Esteban", email: "pablo.esteban@northgate-group.com" },
    subject: "Re: Re: Production incident - following up",
    preview: "Still no word from the technical team. Any update?...",
    body: [
      "Morning,",
      "Still no word from the technical team about Monday incident. Any update?",
      "This is the third email I have sent about this.",
    ],
    time: "8:02",
    bucket: "responder",
    confidence: 0.95,
    signals: [
      { label: "Third message with no reply from you", sign: "pos" },
      { label: "Client with 47 emails of history", sign: "pos" },
      { label: "Direct question", sign: "pos" },
    ],
    threadCount: 6,
    unread: true,
    extracted: { accion: "Give a status on the production incident", urgencia: "alta" },
    summary:
      "A 6-message thread opened on 28/10 over a billing service outage. The team confirmed the fault but has not shared a cause or a timeline. Pablo has written three times with no reply since 30/10. Escalation risk: he is the main contract contact.",
  },
];

/**
 * Umbral por debajo del cual el clasificador admite que no sabe, en vez de adivinar.
 *
 * OJO: en produccion esta regla vive SOLO en el backend. El front pide `?bucket=duda`
 * y no filtra por confianza, porque si filtrara en cliente el numero del tile y el de
 * la lista dejarian de coincidir, y ademas romperia la paginacion: pides 50, te quedan
 * 43 tras filtrar, y el cursor ya avanzo 50. Aqui se replica solo para el mock.
 */
export const UMBRAL_CONFIANZA = 0.55;

/** Unico sitio del front que conoce el umbral. Al cablear la API, esto desaparece. */
export const esDuda = (e: Email) => e.confidence < UMBRAL_CONFIANZA;

/**
 * Estado del motor, con la forma exacta de GET /api/model/status.
 *
 * `precision` y `lastTrained` son `number | null` / `string | null`, NO opcionales: el backend
 * los manda siempre, y `null` significa "todavia no medido" (no hay set de evaluacion aun).
 * Es lo contrario de `extracted`/`summary` en Email, que se omiten cuando no existen; alli
 * ausente significa "este correo no tiene extraccion". La diferencia es deliberada.
 */
export type EstadoModelo = {
  examples: number;
  examplesBySource: { user: number; bootstrapSent: number; bootstrapFolder: number; rule: number };
  bootstrapRan: boolean;
  precision: number | null;
  embeddings: string;
  slm: string;
  lastTrained: string | null;
  indexedEmails: number;
  ollamaAvailable: boolean;
  /** Los embeddings (470 MB) no van en git. Sin ellos se pierde capacidad, no el producto. */
  embeddingsAvailable: boolean;
  /** Siempre viaja; null cuando todo esta bien. Es la explicacion que muestra el aviso. */
  embeddingsMissingReason: string | null;
};

export const ESTADO_MODELO: EstadoModelo = {
  examples: 312,
  examplesBySource: { user: 96, bootstrapSent: 184, bootstrapFolder: 32, rule: 0 },
  bootstrapRan: true,
  precision: 0.91,
  embeddings: "multilingual-e5-small (ONNX, 118M)",
  slm: "Qwen3 4B - Q4_K_M",
  lastTrained: "4 minutes ago",
  indexedEmails: 18427,
  ollamaAvailable: true,
  embeddingsAvailable: true,
  embeddingsMissingReason: null,
};

/** Por debajo de esto, la incertidumbre es falta de historial, no un caso dificil. */
export const EJEMPLOS_PARA_CONOCERTE = 50;

/**
 * Distingue los dos motivos por los que un correo cae en "No estoy seguro":
 * el modelo aun no te conoce, o te conoce y este correo es genuinamente ambiguo.
 * El dia 1 medio buzon cae en ese cajon, y sin este matiz se lee como producto roto.
 */
export const enAprendizaje = (e: EstadoModelo) => !e.bootstrapRan || e.examples < EJEMPLOS_PARA_CONOCERTE;
