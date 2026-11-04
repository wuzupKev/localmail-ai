"use client";

import { ESTADO_MODELO, enAprendizaje, type Bucket } from "@/lib/mock";
import { Logo } from "./bits";

export type Vista = Bucket | "duda";

type NavId = "bandeja" | "buscar" | "modelo" | "ayuda";

const ICONS: Record<NavId | "lock" | "aviso", React.ReactNode> = {
  bandeja: (
    <svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden>
      <path d="M1.8 8.2 3.3 3.1A1.5 1.5 0 0 1 4.7 2h6.6a1.5 1.5 0 0 1 1.4 1.1l1.5 5.1" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
      <path d="M1.8 8.2h3.1l.8 1.7h4.6l.8-1.7h3.1V12a2 2 0 0 1-2 2H3.8a2 2 0 0 1-2-2V8.2Z" stroke="currentColor" strokeWidth="1.4" strokeLinejoin="round" />
    </svg>
  ),
  buscar: (
    <svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden>
      <circle cx="7.2" cy="7.2" r="4.6" stroke="currentColor" strokeWidth="1.4" />
      <path d="m10.7 10.7 2.8 2.8" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
    </svg>
  ),
  modelo: (
    <svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden>
      <rect x="4.5" y="4.5" width="7" height="7" rx="1.5" stroke="currentColor" strokeWidth="1.4" />
      <path d="M6.5 2v2M9.5 2v2M6.5 12v2M9.5 12v2M2 6.5h2M2 9.5h2M12 6.5h2M12 9.5h2" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
    </svg>
  ),
  ayuda: (
    <svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden>
      <circle cx="8" cy="8" r="6.2" stroke="currentColor" strokeWidth="1.4" />
      <path d="M6.3 6.2a1.75 1.75 0 1 1 2.3 1.65c-.4.14-.6.5-.6.92v.33" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
      <circle cx="8" cy="11.4" r="0.75" fill="currentColor" />
    </svg>
  ),
  aviso: (
    <svg width="13" height="13" viewBox="0 0 16 16" fill="none" aria-hidden>
      <path d="M8 2.6 14.4 13H1.6L8 2.6Z" stroke="currentColor" strokeWidth="1.4" strokeLinejoin="round" />
      <path d="M8 6.6v3" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
      <circle cx="8" cy="11.3" r="0.7" fill="currentColor" />
    </svg>
  ),
  lock: (
    <svg width="13" height="13" viewBox="0 0 16 16" fill="none" aria-hidden>
      <rect x="3.5" y="7" width="9" height="6.5" rx="1.6" stroke="currentColor" strokeWidth="1.4" />
      <path d="M5.8 7V5.3a2.2 2.2 0 0 1 4.4 0V7" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
    </svg>
  ),
};

export function Sidebar({
  dudosos,
  abierta,
  onClose,
  onSearch,
  onSettings,
  onHelp,
}: {
  dudosos: number;
  abierta: boolean;
  onClose: () => void;
  onSearch: () => void;
  onSettings: () => void;
  onHelp: () => void;
}) {
  const cerrarY = (fn?: () => void) => () => {
    onClose();
    fn?.();
  };

  const aprendiendo = enAprendizaje(ESTADO_MODELO);

  const items: { id: NavId; label: string; onClick: () => void; active?: boolean }[] = [
    { id: "bandeja", label: "Today's inbox", active: true, onClick: cerrarY() },
    { id: "buscar", label: "Search", onClick: cerrarY(onSearch) },
    { id: "modelo", label: "Model and data", onClick: cerrarY(onSettings) },
    { id: "ayuda", label: "Shortcuts", onClick: cerrarY(onHelp) },
  ];

  return (
    <>
      {/* Por debajo de lg la barra es un cajón: fuera de pantalla hasta que se pide. */}
      {abierta && (
        <div
          className="animate-fade fixed inset-0 z-40 lg:hidden"
          style={{ background: "rgba(22,24,30,.45)" }}
          onClick={onClose}
          aria-hidden
        />
      )}

      <aside
        className={`fixed inset-y-0 left-0 z-50 flex w-[252px] shrink-0 flex-col px-3 pt-5 pb-4 transition-transform duration-200 lg:static lg:z-auto lg:w-[236px] lg:translate-x-0 ${
          abierta ? "translate-x-0" : "-translate-x-full"
        }`}
        style={{ background: "var(--nav)" }}
      >
        <div className="shrink-0 px-1 pb-6">
          <Logo />
        </div>

        {/* La zona que se desplaza si la pantalla es baja; la tarjeta de estado queda al fondo. */}
        <div className="flex min-h-0 flex-1 flex-col overflow-y-auto">
          <nav className="flex shrink-0 flex-col gap-0.5">
            {items.map((it) => (
              <button
                key={it.id}
                onClick={it.onClick}
                className="btn flex items-center gap-2.5 rounded-xl px-3 py-2.5 text-left text-[13px] font-semibold hover:bg-[var(--nav-hover)]"
                style={{
                  background: it.active ? "var(--nav-active)" : "transparent",
                  color: it.active ? "#fff" : "var(--nav-ink)",
                }}
              >
                <span className="shrink-0">{ICONS[it.id]}</span>
                <span className="flex-1 truncate">{it.label}</span>
              </button>
            ))}
          </nav>

          {/*
            * Capacidad perdida, no producto roto: el triage por senales sigue activo.
            * Degradar bien esta bien; degradar en silencio, no.
            */}
          {!ESTADO_MODELO.embeddingsAvailable && (
            <div
              className="mt-5 shrink-0 rounded-2xl border p-3.5"
              style={{ borderColor: "rgba(255,255,255,.14)", background: "rgba(255,255,255,.06)" }}
            >
              <div className="flex items-center gap-1.5">
                <span style={{ color: "#f5c451" }}>{ICONS.aviso}</span>
                <span className="text-[11.5px] font-bold" style={{ color: "#fff" }}>
                  Search model ausente
                </span>
              </div>
              <p className="mt-1.5 text-[11px] leading-snug" style={{ color: "var(--nav-ink)" }}>
                {ESTADO_MODELO.embeddingsMissingReason}
              </p>
              <p className="mt-1.5 text-[11px] leading-snug" style={{ color: "var(--nav-ink)", opacity: 0.8 }}>
                No semantic search. Signal-based triage still works.
              </p>
            </div>
          )}

          {dudosos > 0 && (
            <div
              className="mt-5 shrink-0 rounded-2xl border p-3.5"
              style={{ borderColor: "rgba(234,179,8,.25)", background: "rgba(234,179,8,.12)" }}
            >
              <div className="flex items-baseline gap-2">
                <span className="text-[22px] font-extrabold leading-none tabular-nums" style={{ color: "#f5c451" }}>
                  {dudosos}
                </span>
                <span className="text-[11.5px] font-semibold" style={{ color: "#f5c451" }}>
                  unsorted
                </span>
              </div>
              {/* "Aún no te conozco" y "este correo es ambiguo" no son lo mismo. */}
              <p className="mt-1.5 text-[11px] leading-snug" style={{ color: "#f5c451", opacity: 0.75 }}>
                {aprendiendo
                  ? "I don't know you yet. Correct a few and I'll learn."
                  : "Correct these: they teach the model the most."}
              </p>
            </div>
          )}

          {/* Estado del motor: la prueba visible de que todo ocurre en la máquina. */}
          <div
            className="mt-auto shrink-0 rounded-2xl border p-4"
            style={{ background: "var(--nav-panel)", borderColor: "var(--nav-line)" }}
          >
            <div className="flex items-center gap-1.5" style={{ color: "#fff" }}>
              <span style={{ color: "var(--ok)" }}>{ICONS.lock}</span>
              <span className="text-[12px] font-bold">All local</span>
              <span className="ml-auto h-1.5 w-1.5 animate-blink rounded-full" style={{ background: "var(--ok)" }} />
            </div>

            <dl className="mt-3 space-y-1.5">
              {[
                ["Examples", ESTADO_MODELO.examples.toLocaleString("en-US")],
                // null = aún no medido (falta el set de evaluación). Inventar un número aquí
                // sería mentir en la propia barra lateral del usuario.
                ["Accuracy", ESTADO_MODELO.precision === null ? "not measured" : `${Math.round(ESTADO_MODELO.precision * 100)}%`],
                ["Indexed", ESTADO_MODELO.indexedEmails.toLocaleString("en-US")],
              ].map(([k, v]) => (
                <div key={k} className="flex items-baseline justify-between gap-2">
                  <dt className="text-[11px]" style={{ color: "rgba(255,255,255,.45)" }}>
                    {k}
                  </dt>
                  <dd
                    className="font-mono text-[11.5px] font-medium tabular-nums"
                    style={{ color: "rgba(255,255,255,.92)" }}
                  >
                    {v}
                  </dd>
                </div>
              ))}
            </dl>

            <button
              onClick={cerrarY(onSettings)}
              className="btn btn-raised mt-3.5 w-full rounded-lg py-2 text-[11.5px] font-semibold"
              style={{ background: "#fff", color: "var(--nav)" }}
            >
              See details
            </button>
          </div>
        </div>
      </aside>
    </>
  );
}
