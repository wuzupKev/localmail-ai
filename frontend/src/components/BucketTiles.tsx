"use client";

import { BUCKETS, ESTADO_MODELO, enAprendizaje, esDuda, type Email } from "@/lib/mock";
import type { Vista } from "./Sidebar";

/** Los tiles son la navegacion, no un adorno de metricas: cada uno filtra la lista. */
export function BucketTiles({
  emails,
  dudosos,
  vista,
  onVista,
}: {
  emails: Email[];
  dudosos: number;
  vista: Vista;
  onVista: (v: Vista) => void;
}) {
  const tiles = [
    ...BUCKETS.map((b) => ({
      id: b.id as Vista,
      label: b.label,
      bg: b.bg,
      ink: b.ink,
      n: emails.filter((e) => e.bucket === b.id && !esDuda(e)).length,
      sub: b.id === "responder" ? "someone is waiting on you" : b.id === "informativo" ? "to read" : b.id === "automatizado" ? "from systems" : "promotional",
    })),
    {
      id: "duda" as Vista,
      label: "Not sure",
      bg: "var(--t-duda-bg)",
      ink: "var(--t-duda-ink)",
      n: dudosos,
      sub: enAprendizaje(ESTADO_MODELO) ? "don't know you yet" : "low confidence",
    },
  ];

  return (
    // En móvil una fila que se desliza; desde md, rejilla. Cinco tiles apilados
    // en vertical se comerían la pantalla antes de llegar a la lista.
    <div className="flex snap-x snap-mandatory gap-2.5 overflow-x-auto px-4 pb-4 sm:px-6 md:grid md:grid-cols-3 md:gap-3 md:overflow-visible lg:grid-cols-5">
      {tiles.map((t) => {
        const activo = vista === t.id;
        return (
          <button
            key={t.id}
            onClick={() => onVista(t.id)}
            className="btn btn-tint group w-[136px] shrink-0 snap-start rounded-2xl p-3.5 text-left sm:w-[150px] md:w-auto md:p-4"
            style={{
              background: t.bg,
              outline: activo ? `2px solid ${t.ink}` : "2px solid transparent",
              outlineOffset: 2,
            }}
          >
            <div className="flex items-start justify-between">
              <span className="text-[26px] font-extrabold leading-none tabular-nums" style={{ color: t.ink }}>
                {t.n}
              </span>
              <span
                className="mt-1 h-2 w-2 rounded-full transition-opacity"
                style={{ background: t.ink, opacity: activo ? 1 : 0.25 }}
              />
            </div>
            <div className="mt-2.5 text-[12.5px] font-bold leading-tight" style={{ color: t.ink }}>
              {t.label}
            </div>
            <div className="text-[11px] leading-tight" style={{ color: t.ink, opacity: 0.65 }}>
              {t.sub}
            </div>
          </button>
        );
      })}
    </div>
  );
}
