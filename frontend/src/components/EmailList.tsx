"use client";

import { BUCKETS, type Email } from "@/lib/mock";
import { Avatar, BucketPill, Confidence, Kbd } from "./bits";
import type { Vista } from "./Sidebar";

const TITULOS: Record<Vista, string> = {
  reply: "Needs a reply",
  fyi: "FYI",
  automated: "Automated",
  noise: "Noise",
  unsure: "Not sure",
};

export function EmailList({
  emails,
  vista,
  selectedId,
  onSelect,
  onReclasificar,
}: {
  emails: Email[];
  vista: Vista;
  selectedId: string | null;
  onSelect: (id: string) => void;
  onReclasificar: (id: string, bucket: Email["bucket"]) => void;
}) {
  return (
    <section
      className="flex min-w-0 flex-1 flex-col overflow-hidden rounded-2xl border shadow-card"
      style={{ borderColor: "var(--line)", background: "var(--card)" }}
    >
      <header
        className="flex h-[52px] shrink-0 items-center gap-3 border-b px-4 sm:px-5"
        style={{ borderColor: "var(--line-soft)" }}
      >
        <h2 className="truncate text-[13.5px] font-bold">{TITULOS[vista]}</h2>
        <span
          className="shrink-0 rounded-full px-2 py-0.5 text-[11px] font-semibold tabular-nums"
          style={{ background: "var(--surface)", color: "var(--ink-2)" }}
        >
          {emails.length}
        </span>
        {/* Shortcuts are only announced where there is a keyboard. */}
        <div className="ml-auto hidden items-center gap-1.5 text-[11px] lg:flex" style={{ color: "var(--ink-3)" }}>
          <Kbd>j</Kbd>
          <Kbd>k</Kbd>
          <span className="mx-1">move</span>
          <Kbd>1</Kbd>
          <Kbd>4</Kbd>
          <span>reclassify</span>
        </div>
      </header>

      {/* Table header: it gives data density, but only when there is width for columns. */}
      <div
        className="hidden shrink-0 items-center gap-4 border-b px-5 py-2 text-[10px] font-bold uppercase tracking-wider lg:flex"
        style={{ borderColor: "var(--line-soft)", color: "var(--ink-3)", background: "var(--surface)" }}
      >
        <span className="w-9" />
        <span className="w-[150px]">Sender</span>
        <span className="flex-1">Subject</span>
        <span className="w-[200px] xl:w-[220px]">Why</span>
        <span className="w-[86px]">Confidence</span>
        <span className="w-10 text-right">Time</span>
      </div>

      <div className="flex-1 overflow-y-auto">
        {emails.length === 0 && (
          <div className="grid h-40 place-items-center text-[13px]" style={{ color: "var(--ink-3)" }}>
            Nothing here.
          </div>
        )}

        <ul role="listbox" aria-label={TITULOS[vista]}>
          {emails.map((e) => {
            const activo = e.id === selectedId;
            return (
              <li key={e.id}>
                <div
                  role="option"
                  aria-selected={activo}
                  tabIndex={0}
                  onClick={() => onSelect(e.id)}
                  onKeyDown={(ev) => ev.key === "Enter" && onSelect(e.id)}
                  className="group relative flex cursor-pointer items-start gap-3 border-b px-4 py-3 transition-colors hover:bg-[var(--surface)] sm:px-5 lg:items-center lg:gap-4"
                  style={{
                    borderColor: "var(--line-soft)",
                    background: activo ? "var(--brand-soft)" : undefined,
                  }}
                >
                  <Avatar name={e.from.name} bucket={e.bucket} />

                  {/* Stacked on mobile, in columns from lg up. */}
                  <div className="min-w-0 flex-1 lg:flex lg:items-center lg:gap-4">
                    <div className="min-w-0 lg:w-[150px] lg:shrink-0">
                      <div className="flex items-baseline gap-2">
                        <span
                          className="truncate text-[12.5px]"
                          style={{ color: "var(--ink)", fontWeight: e.unread ? 700 : 500 }}
                        >
                          {e.from.name}
                        </span>
                        <span
                          className="ml-auto shrink-0 font-mono text-[11px] tabular-nums lg:hidden"
                          style={{ color: "var(--ink-3)" }}
                        >
                          {e.time}
                        </span>
                      </div>
                      <div className="hidden truncate text-[11px] lg:block" style={{ color: "var(--ink-3)" }}>
                        {e.from.email}
                      </div>
                    </div>

                    <div className="mt-0.5 min-w-0 lg:mt-0 lg:flex-1">
                      <div
                        className="truncate text-[12.5px]"
                        style={{ color: "var(--ink)", fontWeight: e.unread ? 600 : 400 }}
                      >
                        {e.subject}
                        {e.threadCount > 1 && (
                          <span className="ml-1.5 font-mono text-[10px]" style={{ color: "var(--ink-3)" }}>
                            {e.threadCount}
                          </span>
                        )}
                      </div>
                      <div className="truncate text-[11.5px]" style={{ color: "var(--ink-3)" }}>
                        {e.preview}
                      </div>
                    </div>

                    {/* The why is never hidden: on mobile it drops to its own line. */}
                    <div className="mt-1.5 min-w-0 lg:mt-0 lg:w-[200px] lg:shrink-0 xl:w-[220px]">
                      <div className="truncate text-[11.5px] font-medium" style={{ color: "var(--ink-2)" }}>
                        &#8627; {e.signals[0]?.label}
                      </div>
                      <div className="hidden truncate text-[11px] lg:block" style={{ color: "var(--ink-3)" }}>
                        {e.signals[1]?.label ?? ""}
                      </div>
                    </div>

                    <div className="mt-2 flex items-center gap-2 lg:mt-0 lg:w-[86px] lg:shrink-0">
                      <Confidence value={e.confidence} />
                      <span className="lg:hidden">
                        <BucketPill bucket={e.bucket} />
                      </span>
                    </div>
                  </div>

                  <div
                    className="hidden w-10 shrink-0 text-right font-mono text-[11px] tabular-nums lg:block"
                    style={{ color: "var(--ink-3)" }}
                  >
                    {e.time}
                  </div>

                  {/*
                   * Reclassify in one gesture. Desktop only: it depends on hover, which does not
                   * exist on touch. On mobile it is done from the detail sheet, which has the same buttons.
                   */}
                  <div
                    className="absolute right-4 hidden gap-1 rounded-xl border p-1 opacity-0 shadow-pop transition-opacity group-hover:opacity-100 lg:flex"
                    style={{ borderColor: "var(--line)", background: "#fff" }}
                  >
                    {BUCKETS.map((b) => (
                      <button
                        key={b.id}
                        onClick={(ev) => {
                          ev.stopPropagation();
                          onReclasificar(e.id, b.id);
                        }}
                        disabled={b.id === e.bucket}
                        title={`Move to ${b.label}`}
                        className="btn btn-tint rounded-lg px-2 py-1 text-[11px] font-semibold disabled:opacity-30"
                        style={{ background: b.bg, color: b.ink }}
                      >
                        {b.short}
                      </button>
                    ))}
                  </div>
                </div>
              </li>
            );
          })}
        </ul>
      </div>
    </section>
  );
}
