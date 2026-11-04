"use client";

import Image from "next/image";
import { BUCKETS, type Bucket, type Signal } from "@/lib/mock";

/**
 * The official logo with not one colour touched. The only change is flood-filling the
 * background away from the edges, so the design's black outlines are not eaten.
 * It sits on the dark sidebar, which is where this mark is meant to live.
 */
export function Logo() {
  return (
    <Image
      src="/brand/logo-lockup-dark.png"
      alt="LocalMail AI"
      width={1320}
      height={886}
      priority
      className="h-auto w-[164px]"
    />
  );
}

export function Kbd({ children }: { children: React.ReactNode }) {
  return (
    <kbd
      className="inline-grid h-[19px] min-w-[19px] place-items-center rounded-[5px] border px-1 font-mono text-[10px] font-medium leading-none"
      style={{ borderColor: "var(--line)", background: "#fff", color: "var(--ink-3)" }}
    >
      {children}
    </kbd>
  );
}

/** The sender's initial instead of a fake avatar: same visual weight, no invented people. */
export function Avatar({ name, bucket }: { name: string; bucket: Bucket }) {
  const b = BUCKETS.find((x) => x.id === bucket)!;
  const initials = name
    .replace(/[^\p{L}\s]/gu, "")
    .trim()
    .split(/\s+/)
    .slice(0, 2)
    .map((w) => w[0])
    .join("")
    .toUpperCase();
  return (
    <span
      className="grid h-9 w-9 shrink-0 place-items-center rounded-full text-[12px] font-bold"
      style={{ background: b.bg, color: b.ink }}
      aria-hidden
    >
      {initials || "?"}
    </span>
  );
}

export function Pill({
  children,
  bg,
  ink,
}: {
  children: React.ReactNode;
  bg: string;
  ink: string;
}) {
  return (
    <span
      className="inline-flex items-center gap-1 rounded-full px-2 py-[3px] text-[10.5px] font-semibold"
      style={{ background: bg, color: ink }}
    >
      {children}
    </span>
  );
}

export function BucketPill({ bucket }: { bucket: Bucket }) {
  const b = BUCKETS.find((x) => x.id === bucket)!;
  return (
    <Pill bg={b.bg} ink={b.ink}>
      {b.short}
    </Pill>
  );
}

/** Signals are plain text with a sign: they are data, not decoration. */
export function SignalRow({ signal }: { signal: Signal }) {
  const ink = signal.sign === "pos" ? "var(--ok)" : signal.sign === "neg" ? "var(--t-auto-ink)" : "var(--ink-3)";
  const glyph = signal.sign === "pos" ? "+" : signal.sign === "neg" ? "−" : "~";
  return (
    <li className="flex items-baseline gap-2 text-[12.5px]" style={{ color: "var(--ink-2)" }}>
      <span className="w-2 shrink-0 text-center font-mono text-[11px] font-bold" style={{ color: ink }}>
        {glyph}
      </span>
      <span className="leading-snug">{signal.label}</span>
    </li>
  );
}

export function Confidence({ value, wide = false }: { value: number; wide?: boolean }) {
  const pct = Math.round(value * 100);
  const low = value < 0.55;
  return (
    <div className="flex items-center gap-2">
      <div
        className="h-[5px] overflow-hidden rounded-full"
        style={{ width: wide ? 80 : 44, background: "var(--line)" }}
      >
        <div
          className="h-full rounded-full transition-all duration-300"
          style={{ width: `${pct}%`, background: low ? "var(--t-unsure-ink)" : "var(--brand)" }}
        />
      </div>
      <span
        className="font-mono text-[11px] font-medium tabular-nums"
        style={{ color: low ? "var(--t-unsure-ink)" : "var(--ink-3)" }}
      >
        {pct}%
      </span>
    </div>
  );
}

export function UrgencyPill({ urgency }: { urgency: "high" | "medium" | "low" }) {
  const map = {
    high: { bg: "var(--t-reply-bg)", ink: "var(--t-reply-ink)", l: "Urgent" },
    medium: { bg: "var(--t-info-bg)", ink: "var(--t-info-ink)", l: "Medium" },
    low: { bg: "var(--t-noise-bg)", ink: "var(--t-noise-ink)", l: "Low" },
  }[urgency];
  return (
    <Pill bg={map.bg} ink={map.ink}>
      {map.l}
    </Pill>
  );
}

export function IconButton({
  children,
  onClick,
  label,
}: {
  children: React.ReactNode;
  onClick?: () => void;
  label: string;
}) {
  return (
    <button
      onClick={onClick}
      aria-label={label}
      title={label}
      className="btn btn-raised grid h-9 w-9 place-items-center rounded-full border hover:bg-[var(--surface)]"
      style={{ borderColor: "var(--line)", background: "#fff", color: "var(--ink-2)" }}
    >
      {children}
    </button>
  );
}
