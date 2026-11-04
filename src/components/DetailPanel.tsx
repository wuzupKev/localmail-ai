"use client";

import { useState } from "react";
import { BUCKETS, type Email } from "@/lib/mock";
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

/** Cuerpo compartido por el panel lateral de escritorio y la hoja de móvil. */
function Contenido({
  email,
  onReclasificar,
}: {
  email: Email;
  onReclasificar: (id: string, bucket: Email["bucket"]) => void;
}) {
  const [resumen, setSummary] = useState<string | null>(null);
  const [cargando, setCargando] = useState(false);
  const bajaConfidence = email.confidence < 0.55;

  // Latencia falsa que imita el prefill de un SLM en CPU: el front debe estar diseñado para que tarde.
  const pedirSummary = () => {
    setCargando(true);
    setTimeout(() => {
      setSummary(email.summary ?? "Short thread: no summary needed.");
      setCargando(false);
    }, 1400);
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
          style={{ background: "var(--t-duda-bg)", color: "var(--t-duda-ink)" }}
        >
          <strong className="font-bold">Not sure.</strong> Correct it below.
        </div>
      )}

      {email.extracted && (
        <Seccion titulo="What they want">
          <p className="text-[13px] font-medium leading-snug">{email.extracted.accion}</p>
          <div className="mt-2 flex items-center gap-2">
            <UrgencyPill urgencia={email.extracted.urgencia} />
            {email.extracted.fechaLimite && (
              <span className="font-mono text-[11px]" style={{ color: "var(--ink-3)" }}>
                due {email.extracted.fechaLimite}
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
          !cargando && (
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
            <span className="animate-blink">Qwen3 4B running locally…</span>
            <div className="mt-1 font-mono text-[10.5px]">~4,100 input tokens</div>
          </div>
        )}
        {resumen && (
          <p className="animate-fade text-[12.5px] leading-relaxed" style={{ color: "var(--ink-2)" }}>
            {resumen}
          </p>
        )}
        {!resumen && !cargando && (
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
              onClick={() => onReclasificar(email.id, b.id)}
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

/** Panel lateral, solo a partir de xl: por debajo no cabe junto a la lista. */
export function DetailPanel({
  email,
  onReclasificar,
}: {
  email: Email | null;
  onReclasificar: (id: string, bucket: Email["bucket"]) => void;
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
      <Contenido email={email} onReclasificar={onReclasificar} />
    </aside>
  );
}

/**
 * Por debajo de xl el detalle se abre como hoja a pantalla completa: en móvil no hay sitio
 * para dos columnas, y el "por qué" es justo lo que no puede perderse por falta de ancho.
 */
export function DetailSheet({
  email,
  onClose,
  onReclasificar,
}: {
  email: Email;
  onClose: () => void;
  onReclasificar: (id: string, bucket: Email["bucket"]) => void;
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
        <Contenido email={email} onReclasificar={onReclasificar} />
      </div>
    </div>
  );
}
