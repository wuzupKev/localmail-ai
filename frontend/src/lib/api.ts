// Client for the .NET service. The shapes it returns are the ones in mock.ts: that
// file is the contract, and this module is the only place in the front that talks HTTP.
//
// Two deliberate choices:
//
// 1. The drawer split is the SERVER's. We ask for `?bucket=<drawer>` and render what
//    comes back; the front never filters by confidence. Filtering here would make the
//    tile's number disagree with the list it opens, and would break pagination.
// 2. Every failure surfaces. A backend that is not running is a normal state during
//    development, so the caller is told rather than being handed an empty inbox that
//    looks like a working product with no mail in it.

import type { Bucket, Drawer, Email, ModelStatus } from "@/lib/mock";

export type { Drawer };

export const API_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://127.0.0.1:5174";

/** GET /api/counts. Feeds the five tiles and the "N unread" in the header. */
export type Counts = Record<Drawer, number> & { unread: number };

export type Page = { items: Email[]; nextCursor: string | null };

/** The §3 error envelope, carried as an exception so callers can show the message. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    message: string
  ) {
    super(message);
    this.name = "ApiError";
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let res: Response;
  try {
    res = await fetch(`${API_URL}${path}`, {
      ...init,
      headers: init?.body ? { "Content-Type": "application/json", ...init?.headers } : init?.headers,
    });
  } catch {
    // fetch only rejects when the request never completed: the service is down, the
    // port is wrong, or CORS refused it before any status existed.
    throw new ApiError(0, "unreachable", `cannot reach the service at ${API_URL}`);
  }

  if (!res.ok) {
    const body = await res.json().catch(() => null);
    const err = body?.error;
    throw new ApiError(res.status, err?.code ?? "http_error", err?.message ?? `HTTP ${res.status}`);
  }

  return res.json() as Promise<T>;
}

export const api = {
  health: () => request<{ status: string; version: string }>("/api/health"),

  counts: () => request<Counts>("/api/counts"),

  /** Without a drawer it returns everything, newest first. */
  emails: (drawer?: Drawer, limit = 100, cursor?: string) => {
    const q = new URLSearchParams();
    if (drawer) q.set("bucket", drawer);
    q.set("limit", String(limit));
    if (cursor) q.set("cursor", cursor);
    return request<Page>(`/api/emails?${q}`);
  },

  email: (id: string) => request<Email>(`/api/emails/${encodeURIComponent(id)}`),

  /**
   * Marking a message read is not UI bookkeeping: the first unread -> read transition
   * increments sender_stats.opened, which is the only signal separating `automated`
   * from `noise`. Reading your mail is what trains the system.
   */
  markRead: (id: string, unread: boolean) =>
    request<Email>(`/api/emails/${encodeURIComponent(id)}`, {
      method: "PATCH",
      body: JSON.stringify({ unread }),
    }),

  /** Records the correction and retrains before answering. `examples` is the new total. */
  reclassify: (id: string, bucket: Bucket) =>
    request<{ email: Email; examples: number }>(`/api/emails/${encodeURIComponent(id)}/classification`, {
      method: "POST",
      body: JSON.stringify({ bucket, source: "user" }),
    }),

  /** LIFO. With nothing on the stack it answers 200 with `email: null`, not an error. */
  undo: () =>
    request<{ email: Email | null; examples: number }>("/api/corrections/undo", { method: "POST" }),

  modelStatus: () => request<ModelStatus>("/api/model/status"),

  /**
   * Generates the thread summary, or returns the cached one. Slow by nature: it runs
   * a local model on CPU, so the caller must show progress. Answers 503 with code
   * `ollama_unavailable` when Ollama or the model is missing, and that message is
   * meant to be shown: it says what to install.
   */
  summarise: (id: string, force = false) =>
    request<{ summary: string; model: string }>(
      `/api/emails/${encodeURIComponent(id)}/summary${force ? "?force=true" : ""}`,
      { method: "POST" }
    ),

  /**
   * Hybrid search. Today only the BM25 half answers -- `source` is always "bm25" --
   * because the vector half needs the embedding model. The field is in the response
   * so wiring the other half later changes nothing here.
   */
  search: (q: string, limit = 20) =>
    request<{ items: { email: Email; score: number; source: "bm25" | "vector" | "both" }[] }>(
      `/api/search?q=${encodeURIComponent(q)}&limit=${limit}`
    ),

  /** Starts a pass and returns immediately: a mailbox takes minutes. Poll syncStatus. */
  startSync: () => request<{ state: string }>("/api/sync", { method: "POST" }),

  syncStatus: () =>
    request<{ state: "idle" | "syncing"; fetched: number; total: number; lastSync: string | null; error: string | null }>(
      "/api/sync/status"
    ),

  /** Messages whose current bucket differs from the last one written to IMAP. */
  pendingLabels: () => request<{ count: number; emailIds: string[] }>("/api/labels/pending"),

  /**
   * The only call that writes to the mail server. Per-message results, because an
   * IMAP batch routinely fails halfway and the user has to know what landed.
   */
  applyLabels: () =>
    request<{
      applied: number;
      failed: number;
      results: { emailId: string; ok: boolean; folder: string | null; error: string | null }[];
    }>("/api/labels/apply", { method: "POST" }),
};
