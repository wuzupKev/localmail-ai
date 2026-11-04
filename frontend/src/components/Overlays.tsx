"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { type Email } from "@/lib/mock";
import { api, ApiError } from "@/lib/api";
import { useModelStatus } from "@/lib/model-status";
import { Avatar, Kbd } from "./bits";

/**
 * Search, served by GET /api/search over the whole mailbox -- not just the drawer on
 * screen, which is the point of having a search at all.
 *
 * Offline it falls back to matching the messages already loaded, so the box still does
 * something when the service is down instead of looking broken.
 *
 * Queries are debounced: a keystroke is not a search, and FTS5 is fast enough that the
 * delay is the only thing standing between typing and a request per character.
 */
export function SearchOverlay({
  emails,
  offline = false,
  onClose,
  onSelect,
}: {
  emails: Email[];
  offline?: boolean;
  onClose: () => void;
  onSelect: (id: string) => void;
}) {
  const [q, setQ] = useState("");
  const [hits, setHits] = useState<{ e: Email; source: string }[]>([]);
  const [searching, setSearching] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const ref = useRef<HTMLInputElement>(null);

  useEffect(() => ref.current?.focus(), []);

  const local = useMemo(() => {
    const t = q.trim().toLowerCase();
    if (!t) return [];
    return emails
      .filter((e) =>
        `${e.subject} ${e.from.name} ${e.from.email} ${e.preview} ${e.body.join(" ")}`
          .toLowerCase()
          .includes(t)
      )
      .slice(0, 8)
      .map((e) => ({ e, source: "local" }));
  }, [q, emails]);

  useEffect(() => {
    if (offline) {
      setHits(local);
      return;
    }
    if (!q.trim()) {
      setHits([]);
      setError(null);
      return;
    }

    let cancelled = false;
    setSearching(true);
    const t = setTimeout(async () => {
      try {
        const { items } = await api.search(q, 8);
        if (cancelled) return;
        setHits(items.map((i) => ({ e: i.email, source: i.source })));
        setError(null);
      } catch (err) {
        if (cancelled) return;
        setError(err instanceof ApiError ? err.message : "search failed");
        setHits([]);
      } finally {
        if (!cancelled) setSearching(false);
      }
    }, 220);

    return () => {
      cancelled = true;
      clearTimeout(t);
    };
  }, [q, offline, local]);

  const resultados = hits;

  return (
    <div
      className="animate-fade fixed inset-0 z-50 flex items-start justify-center px-3 pt-[8vh] sm:px-4 sm:pt-[14vh]"
      style={{ background: "rgba(22,24,30,.32)", backdropFilter: "blur(3px)" }}
      onClick={onClose}
    >
      <div
        className="w-full max-w-[580px] overflow-hidden rounded-2xl shadow-pop"
        style={{ background: "#fff" }}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center gap-3 border-b px-5 py-4" style={{ borderColor: "var(--line-soft)" }}>
          <svg width="16" height="16" viewBox="0 0 16 16" fill="none" style={{ color: "var(--ink-3)" }} aria-hidden>
            <circle cx="7.2" cy="7.2" r="4.6" stroke="currentColor" strokeWidth="1.5" />
            <path d="m10.7 10.7 2.8 2.8" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
          </svg>
          <input
            ref={ref}
            value={q}
            onChange={(e) => setQ(e.target.value)}
            onKeyDown={(e) => e.key === "Escape" && onClose()}
            placeholder="overdue invoice, Thursday meeting, incident…"
            className="flex-1 bg-transparent text-[14px] outline-none placeholder:text-[var(--ink-3)]"
          />
          <Kbd>esc</Kbd>
        </div>

        <div className="max-h-[60dvh] overflow-y-auto">
          {!q && (
            <p className="px-5 py-6 text-[12px] leading-relaxed" style={{ color: "var(--ink-3)" }}>
              BM25 over the full text of every message. Semantic similarity joins it once the
              embedding model is installed. No query leaves this machine.
            </p>
          )}

          {q && searching && resultados.length === 0 && !error && (
            <p className="px-5 py-8 text-center text-[13px]" style={{ color: "var(--ink-3)" }}>
              Searching…
            </p>
          )}

          {error && (
            <p className="px-5 py-8 text-center text-[13px]" style={{ color: "var(--t-reply-ink)" }}>
              {error}
            </p>
          )}

          {q && !searching && !error && resultados.length === 0 && (
            <p className="px-5 py-8 text-center text-[13px]" style={{ color: "var(--ink-3)" }}>
              No results.
            </p>
          )}

          {resultados.map(({ e, source }) => (
            <button
              key={e.id}
              onClick={() => {
                onSelect(e.id);
                onClose();
              }}
              className="flex w-full items-center gap-3 border-b px-5 py-3 text-left transition-colors hover:bg-[var(--surface)]"
              style={{ borderColor: "var(--line-soft)" }}
            >
              <Avatar name={e.from.name} bucket={e.bucket} />
              <span className="min-w-0 flex-1">
                <span className="block truncate text-[12.5px] font-semibold">{e.subject}</span>
                <span className="block truncate text-[11.5px]" style={{ color: "var(--ink-3)" }}>
                  {e.from.name} · {e.preview}
                </span>
              </span>
              <span
                className="shrink-0 rounded-full px-2 py-0.5 font-mono text-[10px] font-semibold"
                style={{ background: "var(--surface)", color: "var(--ink-3)" }}
              >
                {source}
              </span>
            </button>
          ))}
        </div>
      </div>
    </div>
  );
}

export function SettingsPanel({ examples, onClose }: { examples: number; onClose: () => void }) {
  const modelStatus = useModelStatus();
  const { examplesBySource: src } = modelStatus;
  const filas: [string, string][] = [
    ["Language model", modelStatus.ollamaAvailable ? modelStatus.slm : `${modelStatus.slm} (unavailable)`],
    ["Classifier examples", `${examples}`],
    ["· corrected by you", `${src.user}`],
    ["· from your Sent folder", `${src.bootstrapSent}`],
    ["· from your folders", `${src.bootstrapFolder}`],
    // null is not 0: it means there is no evaluation set to measure against yet.
    ["Measured accuracy", modelStatus.precision === null ? "not measured yet" : `${Math.round(modelStatus.precision * 100)}%`],
    ["Last trained", modelStatus.lastTrained ?? "never"],
    ["Emails indexed", modelStatus.indexedEmails.toLocaleString("en-US")],
    [
      "Search model",
      modelStatus.embeddingsAvailable
        ? modelStatus.embeddings
        : (modelStatus.embeddingsMissingReason ?? "unavailable"),
    ],
  ];

  return (
    <div
      className="animate-fade fixed inset-0 z-50 flex items-center justify-center px-4"
      style={{ background: "rgba(22,24,30,.32)", backdropFilter: "blur(3px)" }}
      onClick={onClose}
    >
      <div
        className="max-h-[88dvh] w-full max-w-[520px] overflow-y-auto rounded-2xl shadow-pop"
        style={{ background: "#fff" }}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="px-6 pt-6 pb-4">
          <h2 className="text-[16px] font-extrabold tracking-[-0.01em]">Model and data</h2>
          <p className="mt-0.5 text-[12px]" style={{ color: "var(--ink-3)" }}>
            Everything runs and is stored on this machine.
          </p>
        </div>

        <dl className="px-6">
          {filas.map(([k, v]) => (
            <div key={k} className="flex justify-between gap-4 border-b py-2.5" style={{ borderColor: "var(--line-soft)" }}>
              <dt className="text-[12.5px]" style={{ color: "var(--ink-2)" }}>
                {k}
              </dt>
              <dd className="text-right font-mono text-[11.5px] font-medium" style={{ color: "var(--ink)" }}>
                {v}
              </dd>
            </div>
          ))}
        </dl>

        <div className="m-6 rounded-xl p-3.5 text-[12px] leading-snug" style={{ background: "var(--dark)", color: "rgba(255,255,255,.82)" }}>
          <span style={{ color: "var(--ok)" }}>✓</span> No telemetry. The only connections allowed are your IMAP
          server and Ollama on localhost. Verified by a test in CI.
        </div>

        <div className="flex flex-col gap-2 px-6 pb-6 sm:flex-row">
          <button
            className="btn btn-raised flex-1 rounded-xl border py-2.5 text-[12.5px] font-semibold hover:bg-[var(--surface)]"
            style={{ borderColor: "var(--line)", color: "var(--ink-2)" }}
          >
            Export my data
          </button>
          <button
            className="btn btn-tint flex-1 rounded-xl py-2.5 text-[12.5px] font-semibold"
            style={{ background: "var(--t-reply-bg)", color: "var(--t-reply-ink)" }}
          >
            Reset model
          </button>
        </div>
      </div>
    </div>
  );
}

/** Heuristics 3 and 9: every action can be undone, and the way back is in plain sight. */
export function Toast({ text, onUndo }: { text: string; onUndo?: () => void }) {
  return (
    <div
      role="status"
      aria-live="polite"
      className="animate-toast fixed bottom-5 left-1/2 z-[60] flex max-w-[calc(100vw-24px)] items-center gap-2.5 rounded-full py-2.5 pl-4 pr-2 text-[12px] font-medium shadow-pop sm:bottom-7 sm:gap-3 sm:text-[12.5px]"
      style={{ background: "var(--dark)", color: "#fff" }}
    >
      <span style={{ color: "var(--ok)" }}>✓</span>
      <span className="truncate">{text}</span>
      {onUndo && (
        <button
          onClick={onUndo}
          className="btn btn-soft-dark shrink-0 rounded-full px-3 py-1 text-[11.5px] font-bold hover:brightness-125"
          style={{ background: "rgba(255,255,255,.14)", color: "#fff" }}
        >
          Undo <span className="font-mono opacity-60">⌃Z</span>
        </button>
      )}
    </div>
  );
}

const ATAJOS: [string, string][] = [
  ["j / k", "Next / previous email"],
  ["1 2 3 4", "Move to Reply / FYI / Automated / Noise"],
  ["Ctrl + Z", "Undo the last change"],
  ["/", "Search"],
  ["?", "This help"],
  ["Esc", "Close"],
];

/** Heuristics 6 and 10: shortcuts are recognised, not memorised. */
export function HelpOverlay({ onClose }: { onClose: () => void }) {
  return (
    <div
      className="animate-fade fixed inset-0 z-50 flex items-center justify-center px-4"
      style={{ background: "rgba(22,24,30,.32)", backdropFilter: "blur(3px)" }}
      onClick={onClose}
      role="dialog"
      aria-modal="true"
      aria-label="Keyboard shortcuts"
    >
      <div
        className="max-h-[88dvh] w-full max-w-[440px] overflow-y-auto rounded-2xl p-5 shadow-pop sm:p-6"
        style={{ background: "#fff" }}
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className="text-[16px] font-extrabold tracking-[-0.01em]">Shortcuts</h2>
        <dl className="mt-4">
          {ATAJOS.map(([k, d]) => (
            <div key={k} className="flex items-center justify-between gap-6 border-b py-2.5" style={{ borderColor: "var(--line-soft)" }}>
              <dt className="flex gap-1">
                {k.split(" ").map((part) => (
                  <Kbd key={part}>{part}</Kbd>
                ))}
              </dt>
              <dd className="text-right text-[12.5px]" style={{ color: "var(--ink-2)" }}>
                {d}
              </dd>
            </div>
          ))}
        </dl>
        <button
          onClick={onClose}
          className="btn btn-tint mt-5 w-full rounded-xl py-2.5 text-[12.5px] font-semibold"
          style={{ background: "var(--brand-soft)", color: "var(--brand)" }}
        >
          Got it
        </button>
      </div>
    </div>
  );
}

/** Heuristic 5: nothing is written to the mail server without explicit confirmation. */
export function ConfirmDialog({
  n,
  onConfirm,
  onCancel,
}: {
  n: number;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  return (
    <div
      className="animate-fade fixed inset-0 z-50 flex items-center justify-center px-4"
      style={{ background: "rgba(22,24,30,.32)", backdropFilter: "blur(3px)" }}
      onClick={onCancel}
      role="dialog"
      aria-modal="true"
    >
      <div
        className="max-h-[88dvh] w-full max-w-[420px] overflow-y-auto rounded-2xl p-5 shadow-pop sm:p-6"
        style={{ background: "#fff" }}
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className="text-[16px] font-extrabold tracking-[-0.01em]">
          Write {n} {n === 1 ? "label" : "labels"} to IMAP
        </h2>
        <p className="mt-2 text-[12.5px] leading-relaxed" style={{ color: "var(--ink-2)" }}>
          <span className="font-mono">LocalMail/…</span> folders on your server, and the emails get
          labelled. You will see it in Gmail, Thunderbird and on your phone. Nothing is moved or deleted.
        </p>
        <div className="mt-5 flex flex-col gap-2 sm:flex-row">
          <button
            onClick={onCancel}
            className="btn btn-raised flex-1 rounded-xl border py-2.5 text-[12.5px] font-semibold hover:bg-[var(--surface)]"
            style={{ borderColor: "var(--line)", color: "var(--ink-2)" }}
          >
            Cancel
          </button>
          <button
            onClick={onConfirm}
            className="btn btn-dark flex-1 rounded-xl py-2.5 text-[12.5px] font-semibold"
            style={{ background: "var(--dark)", color: "#fff" }}
          >
            Write labels
          </button>
        </div>
      </div>
    </div>
  );
}
