// Fake data for the LocalMail AI mockup.
// None of it comes from a backend: it exists to discuss the interface, not to work.

export type Bucket = "reply" | "fyi" | "automated" | "noise";

export type BucketDef = { id: Bucket; label: string; short: string; bg: string; ink: string; key: string };

export const BUCKETS: BucketDef[] = [
  { id: "reply", label: "Needs a reply", short: "Reply", bg: "var(--t-reply-bg)", ink: "var(--t-reply-ink)", key: "1" },
  { id: "fyi", label: "FYI", short: "FYI", bg: "var(--t-info-bg)", ink: "var(--t-info-ink)", key: "2" },
  { id: "automated", label: "Automated", short: "Automated", bg: "var(--t-auto-bg)", ink: "var(--t-auto-ink)", key: "3" },
  { id: "noise", label: "Noise", short: "Noise", bg: "var(--t-noise-bg)", ink: "var(--t-noise-ink)", key: "4" },
];

/** A deterministic signal: cheap, explainable in one sentence. It is what makes triage legible. */
export type Signal = { label: string; sign: "pos" | "neg" | "neutral" };

export type Email = {
  id: string;
  from: { name: string; email: string };
  subject: string;
  preview: string;
  body: string[];
  time: string;
  bucket: Bucket;
  /** 0..1 - below the threshold the message falls into "Not sure" instead of being guessed. */
  confidence: number;
  signals: Signal[];
  threadCount: number;
  unread: boolean;
  /** Simulated structured extraction (what the SLM would return with a forced JSON schema). */
  extracted?: { action: string; urgency: "high" | "medium" | "low"; dueDate?: string };
  /** Simulated summary: long threads only, and in the real product it is requested on demand. */
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
    bucket: "reply",
    confidence: 0.94,
    signals: [
      { label: "You have replied 12 times", sign: "pos" },
      { label: "You are on To:, not Cc:", sign: "pos" },
      { label: "Ends with a direct question", sign: "pos" },
      { label: "A thread you are already in", sign: "pos" },
    ],
    threadCount: 4,
    unread: true,
    extracted: { action: "Confirm whether Thursday at 10 works", urgency: "high", dueDate: "2026-11-05" },
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
    bucket: "reply",
    confidence: 0.48,
    signals: [
      { label: "New sender, no history", sign: "neutral" },
      { label: "No List-Unsubscribe", sign: "pos" },
      { label: "Contains an amount and a due date", sign: "pos" },
      { label: "A no-reply address", sign: "neg" },
    ],
    threadCount: 1,
    unread: true,
    extracted: { action: "Pay invoice F-2291 (EUR 348.00)", urgency: "medium", dueDate: "2026-11-10" },
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
    bucket: "reply",
    confidence: 0.91,
    signals: [
      { label: "You have replied 31 times", sign: "pos" },
      { label: "Same domain as your organisation", sign: "pos" },
      { label: "Explicit request for data", sign: "pos" },
    ],
    threadCount: 9,
    unread: false,
    extracted: { action: "Send the Q3 usage data", urgency: "high", dueDate: "2026-11-06" },
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
    bucket: "reply",
    confidence: 0.88,
    signals: [
      { label: "You have replied 8 times", sign: "pos" },
      { label: "Explicit request", sign: "pos" },
      { label: "You are on To:", sign: "pos" },
    ],
    threadCount: 3,
    unread: false,
    extracted: { action: "Send the October expense receipts", urgency: "medium" },
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
    bucket: "reply",
    confidence: 0.72,
    signals: [
      { label: "Direct mention of your username", sign: "pos" },
      { label: "Automated sender", sign: "neg" },
      { label: "You act on 64% of these alerts", sign: "pos" },
    ],
    threadCount: 1,
    unread: true,
    extracted: { action: "Review pull request #14", urgency: "medium" },
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
    bucket: "fyi",
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
    bucket: "fyi",
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
    bucket: "fyi",
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
    bucket: "automated",
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
    bucket: "automated",
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
    bucket: "automated",
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
    bucket: "noise",
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
    bucket: "noise",
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
    bucket: "noise",
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
    bucket: "fyi",
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
    bucket: "reply",
    confidence: 0.95,
    signals: [
      { label: "Third message with no reply from you", sign: "pos" },
      { label: "Client with 47 emails of history", sign: "pos" },
      { label: "Direct question", sign: "pos" },
    ],
    threadCount: 6,
    unread: true,
    extracted: { action: "Give a status on the production incident", urgency: "high" },
    summary:
      "A 6-message thread opened on 28/10 over a billing service outage. The team confirmed the fault but has not shared a cause or a timeline. Pablo has written three times with no reply since 30/10. Escalation risk: he is the main contract contact.",
  },
];

/**
 * The threshold below which the classifier admits it does not know, instead of guessing.
 *
 * NOTE: in production this rule lives ONLY in the backend. The front asks for `?bucket=unsure`
 * and does not filter on confidence, because filtering client-side would make the tile's
 * number and the list's stop matching, and would break pagination too: you ask for 50, 43
 * remain after filtering, and the cursor already moved 50. Replicated here for the mock only.
 */
export const CONFIDENCE_THRESHOLD = 0.55;

/** The only place in the front that knows the threshold. Wiring up the API removes it. */
export const isUnsure = (e: Email) => e.confidence < CONFIDENCE_THRESHOLD;

/**
 * Engine status, in the exact shape of GET /api/model/status.
 *
 * `precision` and `lastTrained` are `number | null` / `string | null`, NOT optional: the backend
 * always sends them, and `null` means "not measured yet" (there is no evaluation set so far).
 * It is the opposite of `extracted`/`summary` in Email, which are omitted when absent; there
 * absent means "this message has no extraction". The difference is deliberate.
 */
export type ModelStatus = {
  examples: number;
  examplesBySource: { user: number; bootstrapSent: number; bootstrapFolder: number; rule: number };
  bootstrapRan: boolean;
  precision: number | null;
  embeddings: string;
  slm: string;
  lastTrained: string | null;
  indexedEmails: number;
  ollamaAvailable: boolean;
  /** The embeddings (470 MB) are not in git. Without them you lose capability, not the product. */
  embeddingsAvailable: boolean;
  /** Always travels; null when all is well. It is the explanation the notice shows. */
  embeddingsMissingReason: string | null;
};

export const MODEL_STATUS: ModelStatus = {
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

/** Below this, uncertainty is a lack of history rather than a genuinely hard case. */
export const EXAMPLES_TO_KNOW_YOU = 50;

/**
 * Tells apart the two reasons a message lands in "Not sure":
 * the model does not know you yet, or it does and this message is genuinely ambiguous.
 * On day 1 half the inbox lands there, and without this nuance it reads as a broken product.
 */
export const isLearning = (e: ModelStatus) => !e.bootstrapRan || e.examples < EXAMPLES_TO_KNOW_YOU;
