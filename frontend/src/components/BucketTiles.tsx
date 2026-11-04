"use client";

import { BUCKETS, MODEL_STATUS, isLearning, isUnsure, type Email } from "@/lib/mock";
import type { Vista } from "./Sidebar";

/** The tiles are the navigation, not a metrics ornament: each one filters the list. */
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
      n: emails.filter((e) => e.bucket === b.id && !isUnsure(e)).length,
      sub: b.id === "reply" ? "someone is waiting on you" : b.id === "fyi" ? "to read" : b.id === "automated" ? "from systems" : "promotional",
    })),
    {
      id: "unsure" as Vista,
      label: "Not sure",
      bg: "var(--t-unsure-bg)",
      ink: "var(--t-unsure-ink)",
      n: dudosos,
      sub: isLearning(MODEL_STATUS) ? "don't know you yet" : "low confidence",
    },
  ];

  return (
    // On mobile a row that scrolls; from md, a grid. Five tiles stacked
    // vertically would eat the screen before the list came into view.
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
