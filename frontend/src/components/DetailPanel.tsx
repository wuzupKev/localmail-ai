"use client";

import { useEffect, useState } from "react";
import { BUCKETS, type Email } from "@/lib/mock";
import { api, ApiError } from "@/lib/api";
import { Avatar, BucketPill, Confidence, SignalRow, UrgencyPill } from "./bits";

function Seccion({ titulo, extra, children }: { titulo: string; extra?: React.ReactNode; children: React.ReactNode }) {
  return (
    <div className="border-b px-5 py-4" style={{ borderColor: "var(--line-soft)" }}>
      <div className="mb-2.5 flex items-center justify-between">
        <h3 className="text-[10px] font-bold uppercase tracking-wider" style={{ color: "var(--ink-3)" }}>
          {titulo}
        </h3>
        {extra}
      </div>
      {children}
    </div>
  );
}

/** Body shared by the desktop side panel and the mobile sheet. */
function Contenido({
  email,
  onReclassify,
}: {
  email: Email;
  onReclassify: (id: string, bucket: Email["bucket"]) => void;
}) {
  const [resumen, setSummary] = useState<string | null>(email.summary ?? null);
  const [cargando, setCargando] = useState(false);
  const [errorSummary, setErrorSummary] = useState<string | null>(null);
  const bajaConfidence = email.confidence < 0.55;

  // The panel is reused across messages, so without this the summary of the previous
  // thread would stay on screen under the new subject.
  useEffect(() => {
    setSummary(email.summary ?? null);
    setErrorSummary(null);
    setCargando(false);
  }, [email.id, email.summary]);

  // Real generation: a local model on CPU, seconds rather than milliseconds. The UI is
  // built for that wait instead of hiding it.
  const pedirSummary = async () => {
    setCargando(true);
    setErrorSummary(null);
    try {
      const r = await api.summarise(email.id);
      setSummary(r.summary);
    } catch (e) {
      // The backend's message for a missing Ollama says what to install, so it is
      // shown verbatim rather than replaced with a generic failure.
      setErrorSummary(e instanceof ApiError ? e.message : "could not generate the summary");
    } finally {
      setCargando(false);
    }
  };

  return (
    <>
      <div className="px-5 pt-5 pb-4">
        <div className="flex items-start gap-3">
          <Avatar name={email.from.name} bucket={email.bucket} />
          <div className="min-w-0 flex-1">
            <div className="truncate text-[13px] font-bold">{email.from.name}</div>
            <div className="truncate font-mono text-[11px]" style={{ color: "var(--ink-3)" }}>
              {email.from.email}
            </div>
          </div>
        </div>
        <h2 className="mt-3 text-[14.5px] font-bold leading-snug tracking-[-0.01em]">{email.subject}</h2>
        <div className="mt-2.5 flex items-center gap-2">
          <BucketPill bucket={email.bucket} />
          <div className="ml-auto">
            <Confidence value={email.confidence} wide />
          </div>
        </div>
      </div>

      {bajaConfidence && (
        <div
          className="mx-5 mb-4 rounded-xl px-3.5 py-3 text-[12px] leading-snug"
          style={{ background: "var(--t-unsure-bg)", color: "var(--t-unsure-ink)" }}
        >
          <strong className="font-bold">Not sure.</strong> Correct it below.
        </div>
      )}

      {email.extracted && (
        <Seccion titulo="What they want">
          <p className="text-[13px] font-medium leading-snug">{email.extracted.action}</p>
          <div className="mt-2 flex items-center gap-2">
            <UrgencyPill urgency={email.extracted.urgency} />
            {email.extracted.dueDate && (
              <span className="font-mono text-[11px]" style={{ color: "var(--ink-3)" }}>
                due {email.extracted.dueDate}
              </span>
            )}
          </div>
        </Seccion>
      )}

      <Seccion titulo="Why">
        <ul className="space-y-1.5">
          {email.signals.map((s, i) => (
            <SignalRow key={i} signal={s} />
          ))}
        </ul>
      </Seccion>

      <Seccion
        titulo={`Summary · ${email.threadCount} msg`}
        extra={
          !resumen &&
          !cargando && !errorSummary && (
            <button
              onClick={pedirSummary}
              className="btn btn-tint rounded-lg px-2.5 py-1 text-[11px] font-semibold"
              style={{ background: "var(--brand-soft)", color: "var(--brand)" }}
            >
              Summarise
            </button>
          )
        }
      >
        {cargando && (
          <div className="text-[12px]" style={{ color: "var(--ink-3)" }}>
            <span className="animate-blink">Running locally…</span>
          </div>
        )}
        {!cargando && resumen && (
          <p className="animate-fade text-[12.5px] leading-relaxed" style={{ color: "var(--ink-2)" }}>
            {resumen}
          </p>
        )}
        {!cargando && !resumen && errorSummary && (
          <div className="text-[12px]" style={{ color: "var(--ink-3)" }}>
            <p style={{ color: "var(--t-unsure-ink)" }}>{errorSummary}</p>
            <button
              onClick={pedirSummary}
              className="btn mt-2 rounded-lg px-2.5 py-1 text-[11px] font-semibold"
              style={{ background: "var(--brand-soft)", color: "var(--brand)" }}
            >
              Try again
            </button>
          </div>
        )}
        {!cargando && !resumen && !errorSummary && (
          <p className="text-[12px]" style={{ color: "var(--ink-3)" }}>
            On demand.
          </p>
        )}
      </Seccion>

      <Seccion titulo="Message">
        <div className="space-y-2 text-[12.5px] leading-relaxed whitespace-pre-line" style={{ color: "var(--ink-2)" }}>
          {email.body.map((p, i) => (
            <p key={i}>{p}</p>
          ))}
        </div>
      </Seccion>

      <div className="mt-auto p-5">
        <div className="mb-2.5 text-[10px] font-bold uppercase tracking-wider" style={{ color: "var(--ink-3)" }}>
          Move to
        </div>
        <div className="grid grid-cols-2 gap-2">
          {BUCKETS.map((b) => (
            <button
              key={b.id}
              onClick={() => onReclassify(email.id, b.id)}
              disabled={b.id === email.bucket}
              className="btn btn-tint flex items-center justify-between rounded-xl px-3 py-2.5 text-[12px] font-semibold disabled:opacity-30"
              style={{ background: b.bg, color: b.ink }}
            >
              {b.short}
              <span className="hidden font-mono text-[10px] opacity-60 lg:inline">{b.key}</span>
            </button>
          ))}
        </div>
        <p className="mt-3 text-[11px] leading-snug" style={{ color: "var(--ink-3)" }}>
          Labels are written to IMAP only when you confirm.
        </p>
      </div>
    </>
  );
}

/** Side panel, only from xl up: below that it does not fit beside the list. */
export function DetailPanel({
  email,
  onReclassify,
}: {
  email: Email | null;
  onReclassify: (id: string, bucket: Email["bucket"]) => void;
}) {
  if (!email) {
    return (
      <aside
        className="hidden w-[330px] shrink-0 rounded-2xl border shadow-card xl:block"
        style={{ borderColor: "var(--line)", background: "var(--card)" }}
      >
        <div className="grid h-full place-items-center px-10 text-center text-[12.5px]" style={{ color: "var(--ink-3)" }}>
          Select an email.
        </div>
      </aside>
    );
  }

  return (
    <aside
      className="hidden w-[330px] shrink-0 flex-col overflow-y-auto rounded-2xl border shadow-card xl:flex"
      style={{ borderColor: "var(--line)", background: "var(--card)" }}
    >
      <Contenido email={email} onReclassify={onReclassify} />
    </aside>
  );
}

/**
 * Below xl the detail opens as a full-screen sheet: on mobile there is no room
 * for two columns, and the "why" is exactly what must not be lost to a lack of width.
 */
export function DetailSheet({
  email,
  onClose,
  onReclassify,
}: {
  email: Email;
  onClose: () => void;
  onReclassify: (id: string, bucket: Email["bucket"]) => void;
}) {
  return (
    <div className="animate-fade fixed inset-0 z-50 flex flex-col xl:hidden" style={{ background: "var(--card)" }}>
      <header className="flex h-14 shrink-0 items-center gap-3 border-b px-4" style={{ borderColor: "var(--line)" }}>
        <button
          onClick={onClose}
          className="btn btn-raised flex items-center gap-1.5 rounded-full border px-3 py-1.5 text-[12.5px] font-semibold"
          style={{ borderColor: "var(--line)", background: "#fff", color: "var(--ink-2)" }}
        >
          <svg width="14" height="14" viewBox="0 0 16 16" fill="none" aria-hidden>
            <path d="M10 3 5 8l5 5" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
          Back
        </button>
        <span className="truncate text-[12.5px] font-bold">{email.from.name}</span>
      </header>
      <div className="flex min-h-0 flex-1 flex-col overflow-y-auto">
        <Contenido email={email} onReclassify={onReclassify} />
      </div>
    </div>
  );
}
