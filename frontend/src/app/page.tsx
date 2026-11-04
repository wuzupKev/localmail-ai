"use client";

import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from "react";
import { BUCKETS, EMAILS, MODEL_STATUS, isUnsure, type Bucket, type Email } from "@/lib/mock";
import { Sidebar, type Vista } from "@/components/Sidebar";
import { BucketTiles } from "@/components/BucketTiles";
import { EmailList } from "@/components/EmailList";
import { DetailPanel, DetailSheet } from "@/components/DetailPanel";
import { ConfirmDialog, HelpOverlay, SearchOverlay, SettingsPanel, Toast } from "@/components/Overlays";
import { IconButton } from "@/components/bits";

type Undo = { email: Email; examples: number };

/**
 * Next 16 does not allow reading the clock during prerender, so the date is a
 * client-only value: empty on the server, resolved and cached in the browser.
 */
let fechaCache: string | null = null;
const sinSuscripcion = () => () => {};
const fechaCliente = () => {
  fechaCache ??= new Date().toLocaleDateString("en-US", { month: "long", day: "numeric", year: "numeric" });
  return fechaCache;
};
const useFechaHoy = () => useSyncExternalStore(sinSuscripcion, fechaCliente, () => "");

const LUPA = (
  <svg width="15" height="15" viewBox="0 0 16 16" fill="none" aria-hidden>
    <circle cx="7.2" cy="7.2" r="4.6" stroke="currentColor" strokeWidth="1.5" />
    <path d="m10.7 10.7 2.8 2.8" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
  </svg>
);

export default function Page() {
  const [emails, setEmails] = useState<Email[]>(EMAILS);
  const [vista, setVista] = useState<Vista>("reply");
  const [selectedId, setSelectedId] = useState<string | null>("e1");
  const [modal, setModal] = useState<null | "buscar" | "ajustes" | "ayuda" | "confirmar">(null);
  const [navAbierta, setNavAbierta] = useState(false);
  const [hojaAbierta, setHojaAbierta] = useState(false);
  const [toast, setToast] = useState<{ texto: string; undo: boolean } | null>(null);
  const [examples, setExamples] = useState(MODEL_STATUS.examples);
  const [pendientes, setPendientes] = useState(0);
  const [sync, setSync] = useState<{ estado: "idle" | "syncing"; desde: number }>({ estado: "idle", desde: 2 });

  const undoRef = useRef<Undo | null>(null);

  // The split into five drawers is the server's decision: simulated here with the same rule.
  const dudosos = useMemo(() => emails.filter(isUnsure), [emails]);
  const visibles = useMemo(
    () => (vista === "unsure" ? dudosos : emails.filter((e) => e.bucket === vista && !isUnsure(e))),
    [emails, vista, dudosos]
  );
  const seleccionado = emails.find((e) => e.id === selectedId) ?? null;

  /**
   * In the real backend a correction is stored as an event
   * (email_id, bucket_before, bucket_after, source, timestamp), never overwriting the label.
   */
  const reclassify = useCallback(
    (id: string, bucket: Bucket) => {
      const anterior = emails.find((e) => e.id === id);
      if (!anterior || anterior.bucket === bucket) return;

      undoRef.current = { email: anterior, examples };
      setEmails((prev) =>
        prev.map((e) =>
          e.id === id
            ? { ...e, bucket, confidence: 1, signals: [{ label: "Corrected by you", sign: "pos" as const }, ...e.signals] }
            : e
        )
      );
      setExamples((n) => n + 1);
      setPendientes((n) => n + 1);
      setToast({ texto: `Moved to ${BUCKETS.find((b) => b.id === bucket)!.short} · ${examples + 1} examples`, undo: true });
    },
    [emails, examples]
  );

  // Heuristic 3: user control and freedom. Every reclassification is reversible.
  const deshacer = useCallback(() => {
    const u = undoRef.current;
    if (!u) return;
    setEmails((prev) => prev.map((e) => (e.id === u.email.id ? u.email : e)));
    setExamples(u.examples);
    setPendientes((n) => Math.max(0, n - 1));
    undoRef.current = null;
    setToast({ texto: "Change undone", undo: false });
  }, []);

  useEffect(() => {
    if (!toast) return;
    const t = setTimeout(() => setToast(null), toast.undo ? 5000 : 2200);
    return () => clearTimeout(t);
  }, [toast]);

  const cambiarVista = useCallback(
    (v: Vista) => {
      setVista(v);
      const lista = v === "unsure" ? dudosos : emails.filter((e) => e.bucket === v && !isUnsure(e));
      setSelectedId(lista[0]?.id ?? null);
    },
    [emails, dudosos]
  );

  // Heuristic 1: system status always visible, including while it works.
  const sincronizar = useCallback(() => {
    setSync({ estado: "syncing", desde: 0 });
    setTimeout(() => setSync({ estado: "idle", desde: 0 }), 1800);
  }, []);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        setModal(null);
        setNavAbierta(false);
        setHojaAbierta(false);
        return;
      }
      if (modal) return;

      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "z") {
        e.preventDefault();
        return deshacer();
      }
      if (e.key === "?") {
        e.preventDefault();
        return setModal("ayuda");
      }
      if (e.key === "/") {
        e.preventDefault();
        return setModal("buscar");
      }
      if (e.key === "j" || e.key === "k") {
        const i = visibles.findIndex((x) => x.id === selectedId);
        const n = e.key === "j" ? Math.min(i + 1, visibles.length - 1) : Math.max(i - 1, 0);
        if (visibles[n]) setSelectedId(visibles[n].id);
        return;
      }
      const b = BUCKETS.find((x) => x.key === e.key);
      if (b && selectedId) reclassify(selectedId, b.id);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [modal, visibles, selectedId, reclassify, deshacer]);

  const hoy = useFechaHoy();
  const nuevos = emails.filter((e) => e.unread).length;

  return (
    // h-dvh, not h-screen: on mobile the browser bar eats the 100vh.
    <div className="h-dvh overflow-hidden sm:p-3 lg:p-5">
      <div
        className="mx-auto flex h-full w-full max-w-[1680px] overflow-hidden sm:rounded-[26px] sm:shadow-shell"
        style={{ background: "var(--shell)" }}
      >
        <Sidebar
          dudosos={dudosos.length}
          abierta={navAbierta}
          onClose={() => setNavAbierta(false)}
          onSearch={() => setModal("buscar")}
          onSettings={() => setModal("ajustes")}
          onHelp={() => setModal("ayuda")}
        />

        <div className="flex min-w-0 flex-1 flex-col" style={{ background: "var(--surface)" }}>
          <header className="flex h-[68px] shrink-0 items-center gap-2 px-4 sm:gap-4 sm:px-6 lg:h-[76px]">
            <button
              onClick={() => setNavAbierta(true)}
              aria-label="Open navigation"
              className="btn btn-raised grid h-9 w-9 shrink-0 place-items-center rounded-full border lg:hidden"
              style={{ borderColor: "var(--line)", background: "#fff", color: "var(--ink-2)" }}
            >
              <svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden>
                <path d="M2.5 4h11M2.5 8h11M2.5 12h11" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />
              </svg>
            </button>

            <div className="min-w-0">
              <h1 className="truncate text-[17px] font-extrabold leading-tight tracking-[-0.02em] sm:text-[21px]">
                Today's inbox
              </h1>
              <p className="truncate text-[11.5px] sm:text-[12px]" style={{ color: "var(--ink-3)" }}>
                {hoy ? `${hoy} · ` : ""}
                {nuevos} unread
              </p>
            </div>

            <div className="ml-auto flex shrink-0 items-center gap-2">
              <button
                onClick={sincronizar}
                disabled={sync.estado === "syncing"}
                className="btn btn-raised hidden items-center gap-2 rounded-full border px-3.5 py-2 text-[12px] hover:bg-[var(--surface)] lg:flex"
                style={{ borderColor: "var(--line)", background: "#fff", color: "var(--ink-2)" }}
              >
                <span
                  className={`h-1.5 w-1.5 rounded-full ${sync.estado === "syncing" ? "animate-blink" : ""}`}
                  style={{ background: sync.estado === "syncing" ? "var(--brand)" : "var(--ok)" }}
                />
                {sync.estado === "syncing"
                  ? "Syncing…"
                  : sync.desde === 0
                    ? "Synced just now"
                    : `Synced ${sync.desde} min`}
              </button>

              {pendientes > 0 && (
                <button
                  onClick={() => setModal("confirmar")}
                  className="btn btn-dark rounded-full px-3 py-2 text-[12px] font-semibold sm:px-4"
                  style={{ background: "var(--dark)", color: "#fff" }}
                >
                  <span className="hidden sm:inline">Apply </span>
                  {pendientes}
                  <span className="hidden sm:inline"> {pendientes === 1 ? "label" : "labels"}</span>
                  <span className="sm:hidden"> lbl</span>
                </button>
              )}

              <button
                onClick={() => setModal("buscar")}
                className="btn btn-raised hidden items-center gap-2 rounded-full border px-4 py-2.5 text-[12.5px] hover:bg-[var(--surface)] xl:flex"
                style={{ borderColor: "var(--line)", background: "#fff", color: "var(--ink-3)" }}
              >
                {LUPA}
                Search email
                <span className="ml-6 font-mono text-[11px]">/</span>
              </button>

              <span className="xl:hidden">
                <IconButton label="Search email" onClick={() => setModal("buscar")}>
                  {LUPA}
                </IconButton>
              </span>

              <span className="hidden sm:inline">
                <IconButton label="Keyboard shortcuts" onClick={() => setModal("ayuda")}>
                  <span className="text-[13px] font-bold">?</span>
                </IconButton>
              </span>
            </div>
          </header>

          <BucketTiles emails={emails} dudosos={dudosos.length} vista={vista} onVista={cambiarVista} />

          <div className="flex min-h-0 flex-1 gap-4 px-4 pb-4 sm:px-6 sm:pb-6">
            <EmailList
              emails={visibles}
              vista={vista}
              selectedId={selectedId}
              onSelect={(id) => {
                setSelectedId(id);
                setHojaAbierta(true);
              }}
              onReclasificar={reclassify}
            />
            <DetailPanel email={seleccionado} onReclasificar={reclassify} />
          </div>
        </div>
      </div>

      {/* Below xl the detail opens as a sheet; above it the side panel is already there. */}
      {hojaAbierta && seleccionado && (
        <DetailSheet email={seleccionado} onClose={() => setHojaAbierta(false)} onReclasificar={reclassify} />
      )}

      {modal === "buscar" && (
        <SearchOverlay
          emails={emails}
          onClose={() => setModal(null)}
          onSelect={(id) => {
            const e = emails.find((x) => x.id === id);
            if (e) {
              setVista(isUnsure(e) ? "unsure" : e.bucket);
              setSelectedId(id);
            }
          }}
        />
      )}

      {modal === "ajustes" && <SettingsPanel examples={examples} onClose={() => setModal(null)} />}
      {modal === "ayuda" && <HelpOverlay onClose={() => setModal(null)} />}
      {modal === "confirmar" && (
        <ConfirmDialog
          n={pendientes}
          onCancel={() => setModal(null)}
          onConfirm={() => {
            setToast({ texto: `${pendientes} labels written to IMAP`, undo: false });
            setPendientes(0);
            setModal(null);
          }}
        />
      )}

      {toast && <Toast texto={toast.texto} onUndo={toast.undo ? deshacer : undefined} />}
    </div>
  );
}
