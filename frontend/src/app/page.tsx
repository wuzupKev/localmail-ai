"use client";

import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from "react";
import { BUCKETS, EMAILS, MODEL_STATUS, isUnsure, type Bucket, type Drawer, type Email } from "@/lib/mock";
import { api, ApiError, API_URL, type Counts } from "@/lib/api";
import { ModelStatusProvider } from "@/lib/model-status";
import { Sidebar } from "@/components/Sidebar";
import { BucketTiles } from "@/components/BucketTiles";
import { EmailList } from "@/components/EmailList";
import { DetailPanel, DetailSheet } from "@/components/DetailPanel";
import { ConfirmDialog, HelpOverlay, SearchOverlay, SettingsPanel, Toast } from "@/components/Overlays";
import { IconButton } from "@/components/bits";

/**
 * Next 16 does not allow reading the clock during prerender, so the date is a
 * client-only value: empty on the server, resolved and cached in the browser.
 */
let dateCache: string | null = null;
const noSubscription = () => () => {};
const clientDate = () => {
  dateCache ??= new Date().toLocaleDateString("en-US", { month: "long", day: "numeric", year: "numeric" });
  return dateCache;
};
const useToday = () => useSyncExternalStore(noSubscription, clientDate, () => "");

/**
 * Whether we are talking to the service.
 *
 * "offline" is a first-class state, not an error screen: running the interface without
 * the backend is normal during design work. It falls back to the mock and says so, so
 * nobody mistakes sample data for their own mail.
 */
type Connection = "connecting" | "live" | "offline";

const MAGNIFIER = (
  <svg width="15" height="15" viewBox="0 0 16 16" fill="none" aria-hidden>
    <circle cx="7.2" cy="7.2" r="4.6" stroke="currentColor" strokeWidth="1.5" />
    <path d="m10.7 10.7 2.8 2.8" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
  </svg>
);

/** Counts derived from the mock, for the offline fallback only. */
function mockCounts(emails: Email[]): Counts {
  const unsure = emails.filter(isUnsure);
  const of = (b: Bucket) => emails.filter((e) => e.bucket === b && !isUnsure(e)).length;
  return {
    reply: of("reply"),
    fyi: of("fyi"),
    automated: of("automated"),
    noise: of("noise"),
    unsure: unsure.length,
    unread: emails.filter((e) => e.unread).length,
  };
}

export default function Page() {
  const [connection, setConnection] = useState<Connection>("connecting");
  const [drawer, setDrawer] = useState<Drawer>("reply");
  const [emails, setEmails] = useState<Email[]>([]);
  const [counts, setCounts] = useState<Counts>(() => mockCounts(EMAILS));
  const [modelStatus, setModelStatus] = useState(MODEL_STATUS);
  const [loadingList, setLoadingList] = useState(true);
  const [failure, setFailure] = useState<string | null>(null);

  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [modal, setModal] = useState<null | "search" | "settings" | "help" | "confirm">(null);
  const [navOpen, setNavOpen] = useState(false);
  const [sheetOpen, setSheetOpen] = useState(false);
  const [toast, setToast] = useState<{ text: string; undo: boolean } | null>(null);
  const [examples, setExamples] = useState(MODEL_STATUS.examples);
  const [pending, setPending] = useState(0);
  const [sync, setSync] = useState<{ state: "idle" | "syncing"; fetched: number; total: number; lastSync: string | null }>(
    { state: "idle", fetched: 0, total: 0, lastSync: null }
  );

  // Guards against a slow drawer response landing after the user moved on.
  const requestId = useRef(0);

  const offline = connection === "offline";

  /** Counts and model status: the two things that describe the whole inbox. */
  const refreshSummary = useCallback(async () => {
    const [c, m, p] = await Promise.all([api.counts(), api.modelStatus(), api.pendingLabels()]);
    setCounts(c);
    setModelStatus(m);
    setExamples(m.examples);
    // Pending is the server's answer, not a local tally: a correction that returns a
    // message to the bucket already written to IMAP leaves nothing pending.
    setPending(p.count);
  }, []);

  /** First contact. Deciding live-versus-offline once keeps every later call simple. */
  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        await api.health();
        if (cancelled) return;
        await refreshSummary();
        if (cancelled) return;
        setConnection("live");
      } catch (e) {
        if (cancelled) return;
        setConnection("offline");
        setCounts(mockCounts(EMAILS));
        setEmails(EMAILS.filter((x) => x.bucket === "reply" && !isUnsure(x)));
        setSelectedId(EMAILS[0]?.id ?? null);
        setLoadingList(false);
        setFailure(e instanceof ApiError ? e.message : String(e));
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [refreshSummary]);

  /** The drawer's contents. The server decides what belongs in it; we just render it. */
  useEffect(() => {
    if (connection !== "live") return;
    const id = ++requestId.current;
    setLoadingList(true);
    (async () => {
      try {
        const page = await api.emails(drawer);
        if (id !== requestId.current) return;
        setEmails(page.items);
        setSelectedId(page.items[0]?.id ?? null);
        setFailure(null);
      } catch (e) {
        if (id !== requestId.current) return;
        setFailure(e instanceof ApiError ? e.message : String(e));
      } finally {
        if (id === requestId.current) setLoadingList(false);
      }
    })();
  }, [connection, drawer]);

  const selected = emails.find((e) => e.id === selectedId) ?? null;

  /**
   * A correction is an event on the server, not a label overwritten here. The response
   * carries the retrained example count, so the toast reports the real number.
   */
  const reclassify = useCallback(
    async (id: string, bucket: Bucket) => {
      const previous = emails.find((e) => e.id === id);
      if (!previous || previous.bucket === bucket) return;

      const short = BUCKETS.find((b) => b.id === bucket)!.short;

      if (offline) {
        setEmails((prev) => prev.map((e) => (e.id === id ? { ...e, bucket, confidence: 1 } : e)));
        setExamples((n) => n + 1);
        setPending((n) => n + 1);
        setToast({ text: `Moved to ${short} · ${examples + 1} examples`, undo: true });
        return;
      }

      try {
        const { examples: total } = await api.reclassify(id, bucket);
        setExamples(total);
        setToast({ text: `Moved to ${short} · ${total} examples`, undo: true });
        // It has usually left the drawer, so both the list and the tiles are stale.
        const [page] = await Promise.all([api.emails(drawer), refreshSummary()]);
        setEmails(page.items);
        setSelectedId((cur) => (page.items.some((e) => e.id === cur) ? cur : (page.items[0]?.id ?? null)));
      } catch (e) {
        setToast({ text: e instanceof ApiError ? e.message : "the correction failed", undo: false });
      }
    },
    [emails, offline, examples, drawer, refreshSummary]
  );

  /** Heuristic 3: user control and freedom. Every reclassification is reversible. */
  const undo = useCallback(async () => {
    if (offline) {
      setToast({ text: "Undo needs the service running", undo: false });
      return;
    }
    try {
      const { email, examples: total } = await api.undo();
      if (!email) {
        setToast({ text: "Nothing to undo", undo: false });
        return;
      }
      setExamples(total);
      const [page] = await Promise.all([api.emails(drawer), refreshSummary()]);
      setEmails(page.items);
      setToast({ text: "Change undone", undo: false });
    } catch (e) {
      setToast({ text: e instanceof ApiError ? e.message : "undo failed", undo: false });
    }
  }, [offline, drawer, refreshSummary]);

  /**
   * Opening a message marks it read, and that is not bookkeeping: the first
   * unread -> read transition increments sender_stats.opened, the only signal that
   * separates `automated` from `noise`. Reading your mail trains the system.
   */
  const open = useCallback(
    (id: string) => {
      setSelectedId(id);
      setSheetOpen(true);
      if (offline) return;
      const email = emails.find((e) => e.id === id);
      if (!email?.unread) return;
      api
        .markRead(id, false)
        .then((updated) => {
          setEmails((prev) => prev.map((e) => (e.id === id ? updated : e)));
          setCounts((c) => ({ ...c, unread: Math.max(0, c.unread - 1) }));
        })
        .catch(() => {
          /* Read state is not worth interrupting the user over. */
        });
    },
    [emails, offline]
  );

  useEffect(() => {
    if (!toast) return;
    const t = setTimeout(() => setToast(null), toast.undo ? 5000 : 2200);
    return () => clearTimeout(t);
  }, [toast]);

  const changeDrawer = useCallback(
    (d: Drawer) => {
      setDrawer(d);
      if (offline) {
        const list = d === "unsure" ? EMAILS.filter(isUnsure) : EMAILS.filter((e) => e.bucket === d && !isUnsure(e));
        setEmails(list);
        setSelectedId(list[0]?.id ?? null);
      }
    },
    [offline]
  );

  /**
   * Starts a pass and polls until it ends. The request returns at once because a
   * mailbox takes minutes, so progress lives in GET /api/sync/status.
   */
  const startSync = useCallback(async () => {
    if (offline || sync.state === "syncing") return;
    try {
      await api.startSync();
      setSync((v) => ({ ...v, state: "syncing" }));

      const poll = setInterval(async () => {
        try {
          const st = await api.syncStatus();
          setSync({ state: st.state, fetched: st.fetched, total: st.total, lastSync: st.lastSync });
          if (st.state === "idle") {
            clearInterval(poll);
            if (st.error) {
              setToast({ text: st.error, undo: false });
            } else {
              setToast({ text: `Synced ${st.fetched} messages`, undo: false });
            }
            const [page] = await Promise.all([api.emails(drawer), refreshSummary()]);
            setEmails(page.items);
          }
        } catch {
          clearInterval(poll);
          setSync((v) => ({ ...v, state: "idle" }));
        }
      }, 1200);
    } catch (e) {
      setToast({ text: e instanceof ApiError ? e.message : "sync failed", undo: false });
    }
  }, [offline, sync.state, drawer, refreshSummary]);

  /** The only call that writes to the mail server, and only after the dialog. */
  const applyLabels = useCallback(async () => {
    setModal(null);
    try {
      const r = await api.applyLabels();
      setToast({
        text: r.failed === 0
          ? `${r.applied} labels written to IMAP`
          : `${r.applied} written, ${r.failed} failed`,
        undo: false,
      });
      await refreshSummary();
    } catch (e) {
      setToast({ text: e instanceof ApiError ? e.message : "the write failed", undo: false });
    }
  }, [refreshSummary]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        setModal(null);
        setNavOpen(false);
        setSheetOpen(false);
        return;
      }
      if (modal) return;

      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "z") {
        e.preventDefault();
        void undo();
        return;
      }
      if (e.key === "?") {
        e.preventDefault();
        return setModal("help");
      }
      if (e.key === "/") {
        e.preventDefault();
        return setModal("search");
      }
      if (e.key === "j" || e.key === "k") {
        const i = emails.findIndex((x) => x.id === selectedId);
        const n = e.key === "j" ? Math.min(i + 1, emails.length - 1) : Math.max(i - 1, 0);
        if (emails[n]) setSelectedId(emails[n].id);
        return;
      }
      const b = BUCKETS.find((x) => x.key === e.key);
      if (b && selectedId) void reclassify(selectedId, b.id);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [modal, emails, selectedId, reclassify, undo]);

  const today = useToday();

  return (
    <ModelStatusProvider value={modelStatus}>
      {/* h-dvh, not h-screen: on mobile the browser bar eats the 100vh. */}
      <div className="h-dvh overflow-hidden sm:p-3 lg:p-5">
        <div
          className="mx-auto flex h-full w-full max-w-[1680px] overflow-hidden sm:rounded-[26px] sm:shadow-shell"
          style={{ background: "var(--shell)" }}
        >
          <Sidebar
            unsureCount={counts.unsure}
            open={navOpen}
            onClose={() => setNavOpen(false)}
            onSearch={() => setModal("search")}
            onSettings={() => setModal("settings")}
            onHelp={() => setModal("help")}
          />

          <div className="flex min-w-0 flex-1 flex-col" style={{ background: "var(--surface)" }}>
            <header className="flex h-[68px] shrink-0 items-center gap-2 px-4 sm:gap-4 sm:px-6 lg:h-[76px]">
              <button
                onClick={() => setNavOpen(true)}
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
                  Today&apos;s inbox
                </h1>
                <p className="truncate text-[11.5px] sm:text-[12px]" style={{ color: "var(--ink-3)" }}>
                  {today ? `${today} · ` : ""}
                  {counts.unread} unread
                </p>
              </div>

              <div className="ml-auto flex shrink-0 items-center gap-2">
                <span
                  className="hidden items-center gap-2 rounded-full border px-3.5 py-2 text-[12px] lg:flex"
                  style={{ borderColor: "var(--line)", background: "#fff", color: "var(--ink-2)" }}
                  title={offline ? `${failure ?? "service unreachable"} · showing sample data` : API_URL}
                >
                  <span
                    className={`h-1.5 w-1.5 rounded-full ${connection === "connecting" ? "animate-blink" : ""}`}
                    style={{
                      background:
                        connection === "live" ? "var(--ok)" : connection === "offline" ? "var(--warn, #c2410c)" : "var(--brand)",
                    }}
                  />
                  {connection === "live" ? "Connected" : connection === "offline" ? "Sample data" : "Connecting…"}
                </span>

                {!offline && (
                  <button
                    onClick={startSync}
                    disabled={sync.state === "syncing"}
                    className="btn btn-raised hidden items-center gap-2 rounded-full border px-3.5 py-2 text-[12px] hover:bg-[var(--surface)] lg:flex disabled:opacity-60"
                    style={{ borderColor: "var(--line)", background: "#fff", color: "var(--ink-2)" }}
                  >
                    <span
                      className={`h-1.5 w-1.5 rounded-full ${sync.state === "syncing" ? "animate-blink" : ""}`}
                      style={{ background: sync.state === "syncing" ? "var(--brand)" : "var(--ok)" }}
                    />
                    {sync.state === "syncing"
                      ? `Syncing ${sync.fetched}/${sync.total}…`
                      : sync.lastSync
                        ? "Synced"
                        : "Sync mail"}
                  </button>
                )}

                {pending > 0 && (
                  <button
                    onClick={() => setModal("confirm")}
                    className="btn btn-dark rounded-full px-3 py-2 text-[12px] font-semibold sm:px-4"
                    style={{ background: "var(--dark)", color: "#fff" }}
                  >
                    <span className="hidden sm:inline">Apply </span>
                    {pending}
                    <span className="hidden sm:inline"> {pending === 1 ? "label" : "labels"}</span>
                    <span className="sm:hidden"> lbl</span>
                  </button>
                )}

                <button
                  onClick={() => setModal("search")}
                  className="btn btn-raised hidden items-center gap-2 rounded-full border px-4 py-2.5 text-[12.5px] hover:bg-[var(--surface)] xl:flex"
                  style={{ borderColor: "var(--line)", background: "#fff", color: "var(--ink-3)" }}
                >
                  {MAGNIFIER}
                  Search email
                  <span className="ml-6 font-mono text-[11px]">/</span>
                </button>

                <span className="xl:hidden">
                  <IconButton label="Search email" onClick={() => setModal("search")}>
                    {MAGNIFIER}
                  </IconButton>
                </span>

                <span className="hidden sm:inline">
                  <IconButton label="Keyboard shortcuts" onClick={() => setModal("help")}>
                    <span className="text-[13px] font-bold">?</span>
                  </IconButton>
                </span>
              </div>
            </header>

            {offline && (
              <div
                className="mx-4 mb-3 rounded-xl px-3.5 py-2.5 text-[12px] sm:mx-6"
                style={{ background: "var(--t-unsure-bg)", color: "var(--t-unsure-ink)" }}
              >
                <strong>Showing sample data.</strong> The service at {API_URL} is not answering, so this is the
                mock inbox. Start it with <code>dotnet run --project src\LocalMailAI.Api</code> and reload.
              </div>
            )}

            <BucketTiles counts={counts} drawer={drawer} onDrawer={changeDrawer} />

            <div className="flex min-h-0 flex-1 gap-4 px-4 pb-4 sm:px-6 sm:pb-6">
              <EmailList
                emails={emails}
                drawer={drawer}
                selectedId={selectedId}
                onSelect={open}
                onReclassify={reclassify}
                loading={loadingList}
              />
              <DetailPanel email={selected} onReclassify={reclassify} />
            </div>
          </div>
        </div>

        {/* Below xl the detail opens as a sheet; above it the side panel is already there. */}
        {sheetOpen && selected && (
          <DetailSheet email={selected} onClose={() => setSheetOpen(false)} onReclassify={reclassify} />
        )}

        {modal === "search" && (
          <SearchOverlay
            emails={emails}
            offline={offline}
            onClose={() => setModal(null)}
            onSelect={(id) => {
              setSelectedId(id);
              setModal(null);
            }}
          />
        )}

        {modal === "settings" && <SettingsPanel examples={examples} onClose={() => setModal(null)} />}
        {modal === "help" && <HelpOverlay onClose={() => setModal(null)} />}
        {modal === "confirm" && (
          <ConfirmDialog
            n={pending}
            onCancel={() => setModal(null)}
            onConfirm={applyLabels}
          />
        )}

        {toast && <Toast text={toast.text} onUndo={toast.undo ? undo : undefined} />}
      </div>
    </ModelStatusProvider>
  );
}
