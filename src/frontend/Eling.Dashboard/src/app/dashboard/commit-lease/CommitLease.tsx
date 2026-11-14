"use client"

import { useCallback, useEffect, useMemo, useState } from "react"
import {
  AlertCircle,
  AlertTriangle,
  CheckCircle2,
  Clock,
  HeartPulse,
  RefreshCw,
  Timer,
  X,
} from "lucide-react"

import { DashboardHeader } from "@/components/dashboard-header"
import { PathText } from "@/components/path-text"
import { LeasePaths } from "./lease-paths"
import {
  selectAll,
  selectOnly,
  toggleRoots,
  type WorkspaceSelection,
} from "@/components/workspaces/selection"
import { useWorkspaceRows } from "@/components/workspaces/use-workspace-rows"
import { workspaceName } from "@/components/workspaces/runtime-rows"
import { WorkspaceScopePanel } from "@/components/workspaces/workspace-scope-panel"
import { WorkspaceSelect } from "@/components/workspaces/workspace-select"
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import {
  Card,
  CardAction,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
import { Skeleton } from "@/components/ui/skeleton"
import { apiFetch, ApiError } from "@/lib/api"
import { relativeTime, utcToLocal } from "@/lib/date-utils"

/**
 * Commit Lease panel.
 *
 * A dumb renderer of the backend's snapshot: one full-snapshot frame rides
 * the SSE stream on every change (plus an initial frame on connect and on
 * reconnect), and `GET /api/commit-lease/active?scope=all` answers with the
 * same machine-global shape for the first paint. Stale leases never appear
 * here - the store sweeps them into recently-released on read. Frames span
 * every checkout on the machine, checkout = null at the top level and a
 * per-row checkout on each lease; the workspace picker narrows the view
 * client-side, so changing the selection never refetches.
 *
 * The envelope split matters: the REST routes go through the standard
 * envelope (apiFetch unwraps it) while the SSE frames are raw snapshot JSON,
 * so the two paths deserialize the same wire shape differently.
 */

type CommitLeaseRow = {
  lease_id: string
  session_id: string
  branch: string | null
  since: string
  last_heartbeat: string
  lease_age_seconds: number
  ttl_seconds: number
  paths: string[] | null
  intent: string | null
  checkout: string
}

type CommitLeaseReleasedRow = {
  lease_id: string
  session_id: string
  since: string
  released_at: string | null
  release_reason: string | null
  intent: string | null
  paths: string[] | null
  checkout: string
}

type CommitLeaseSnapshot = {
  checkout: string | null
  branch: string | null
  commit_leases: CommitLeaseRow[]
  recently_released: CommitLeaseReleasedRow[]
}

type StreamStatus = "connected" | "connecting" | "error"

type Notification = {
  id: number
  type: "success" | "error"
  title: string
  text?: string
}

const MAX_NOTIFICATIONS = 4
const NOTIFICATION_TTL_MS = 4000

let nextNotificationId = 0

function shortSession(sessionId: string): string {
  return sessionId.length > 12 ? `${sessionId.slice(0, 8)}…` : sessionId
}

function formatAge(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return "—"
  const s = Math.floor(seconds)
  if (s < 60) return `${s}s`
  const m = Math.floor(s / 60)
  if (m < 60) return `${m}m ${s % 60}s`
  const h = Math.floor(m / 60)
  if (h < 24) return `${h}h ${m % 60}m`
  const d = Math.floor(h / 24)
  return `${d}d ${h % 24}h`
}

function errorMessage(e: unknown): string {
  if (e instanceof ApiError) return e.message
  if (e instanceof Error) return e.message
  return String(e)
}

// Path comparison for the client-side selection filter. Folds separator
// style, trailing slashes and case so a lease recorded as "C:/A/" matches a
// workspace listed as "C:\a". Case is folded unconditionally: the lease store
// itself compares case-insensitively on the platform that wrote the record,
// and an over-match here is far better than hiding a live lease.
function normalizeRoot(root: string | null | undefined): string {
  if (!root) return ""
  return root.replace(/\\/g, "/").replace(/\/+$/, "").toLowerCase()
}

export function CommitLease() {
  const [snapshot, setSnapshot] = useState<CommitLeaseSnapshot | null>(null)
  // When the last snapshot frame landed. Age/TTL are rendered as frame value
  // + elapsed wall-clock since this moment, so the numbers tick between
  // frames instead of freezing at whatever the last frame computed.
  const [snapshotAt, setSnapshotAt] = useState<number>(() => Date.now())
  const [now, setNow] = useState<number>(() => Date.now())
  const [loading, setLoading] = useState(true)
  const [refreshing, setRefreshing] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [streamStatus, setStreamStatus] = useState<StreamStatus>("connecting")
  const [forceTarget, setForceTarget] = useState<CommitLeaseRow | null>(null)
  const [releasing, setReleasing] = useState(false)
  const [notifications, setNotifications] = useState<Notification[]>([])
  const [selection, setSelection] = useState<WorkspaceSelection>(() => selectAll())

  const { rows, error: runtimesError } = useWorkspaceRows()
  const allRoots = useMemo(() => rows.map((r) => r.root), [rows])

  const dismissNotification = useCallback((id: number) => {
    setNotifications((current) => current.filter((item) => item.id !== id))
  }, [])

  const applySnapshot = useCallback((next: CommitLeaseSnapshot | null) => {
    const receivedAt = Date.now()
    setSnapshot(next)
    setSnapshotAt(receivedAt)
    setNow(receivedAt)
  }, [])

  const notify = useCallback((type: Notification["type"], title: string, text?: string) => {
    nextNotificationId += 1
    const id = nextNotificationId
    setNotifications((current) => [
      ...current.slice(-(MAX_NOTIFICATIONS - 1)),
      { id, type, title, text },
    ])
  }, [])

  const load = useCallback(async () => {
    setRefreshing(true)
    setError(null)
    try {
      const data = await apiFetch<CommitLeaseSnapshot>("/api/commit-lease/active?scope=all")
      applySnapshot(data ?? null)
    } catch (e) {
      setError(`Failed to load the lease snapshot (${errorMessage(e)})`)
    } finally {
      setLoading(false)
      setRefreshing(false)
    }
  }, [applySnapshot])

  useEffect(() => {
    const t = setTimeout(() => {
      void load()
    }, 0)
    return () => clearTimeout(t)
  }, [load])

  // SSE carries full snapshots; a reconnect resyncs from the stream itself,
  // so there is nothing to refetch and no polling anywhere on this page.
  useEffect(() => {
    if (typeof window === "undefined") return
    let es: EventSource | null = null
    let initErrorTimer: ReturnType<typeof setTimeout> | null = null
    try {
      es = new EventSource("/api/events/commit-lease")

      es.onopen = () => setStreamStatus("connected")

      es.onmessage = (event) => {
        if (!event.data || event.data === "connected") return
        try {
          applySnapshot(JSON.parse(event.data) as CommitLeaseSnapshot)
          setError(null)
        } catch (e) {
          // A malformed frame must never tear down a live stream, but it
          // should not vanish silently either.
          console.warn("[COMMIT-LEASE SSE] malformed snapshot frame", event.data, e)
        }
      }

      es.onerror = () => {
        if (es?.readyState === EventSource.CONNECTING) {
          setStreamStatus("connecting")
        } else if (es?.readyState === EventSource.CLOSED) {
          setStreamStatus("error")
        }
      }
    } catch (e) {
      console.error("[COMMIT-LEASE SSE INIT ERROR]", e)
      // Deferred: calling setState synchronously in an effect body cascades renders.
      initErrorTimer = setTimeout(() => setStreamStatus("error"), 0)
    }

    return () => {
      if (initErrorTimer) clearTimeout(initErrorTimer)
      es?.close()
    }
  }, [applySnapshot])

  // One-second ticker: frame values are anchored to snapshotAt, so Age and
  // the TTL countdown advance with the wall clock instead of freezing at
  // whatever the last frame computed.
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(id)
  }, [])

  const confirmForceRelease = useCallback(async () => {
    if (!forceTarget) return
    setReleasing(true)
    try {
      const fresh = await apiFetch<CommitLeaseSnapshot>(
        `/api/commit-lease/${encodeURIComponent(forceTarget.lease_id)}/release?checkout=${encodeURIComponent(forceTarget.checkout)}`,
        { method: "POST" }
      )
      if (fresh) applySnapshot(fresh)
      notify(
        "success",
        "Lease released",
        `Commit lease held by ${shortSession(forceTarget.session_id)} was force-released.`
      )
      setForceTarget(null)
    } catch (e) {
      const text =
        e instanceof ApiError && e.status === 404
          ? "No live lease with that id in that workspace - already released or swept as stale."
          : errorMessage(e)
      notify("error", "Force release failed", text)
    } finally {
      setReleasing(false)
    }
  }, [forceTarget, notify, applySnapshot])

  const contentColumn = "flex min-w-0 w-full flex-1 flex-col gap-4 p-4"
  const elapsedSinceFrame = Math.max(0, (now - snapshotAt) / 1000)
  const liveAge = (lease: CommitLeaseRow) => lease.lease_age_seconds + elapsedSinceFrame
  const liveTtlRemaining = (lease: CommitLeaseRow) => Math.max(0, lease.ttl_seconds - liveAge(lease))
  // Frames span every checkout; narrow them to the selection client-side.
  // "all" keeps both lists untouched, so nothing refetches on a switch.
  const selectedRoots = useMemo(
    () => (selection.kind === "all" ? null : new Set(selection.roots.map(normalizeRoot))),
    [selection],
  )
  const isIncluded = useCallback(
    (checkout: string) => selectedRoots === null || selectedRoots.has(normalizeRoot(checkout)),
    [selectedRoots],
  )
  const active = (snapshot?.commit_leases ?? []).filter((lease) => isIncluded(lease.checkout))
  const released = (snapshot?.recently_released ?? []).filter((lease) => isIncluded(lease.checkout))
  const showSkeleton = loading && snapshot === null

  return (
    <div className="flex w-full flex-col">
      <DashboardHeader label="Commit Lease">
        {/* Narrow screens keep the dropdown in the header row; the side
            panel covers wide ones. */}
        <div className="lg:hidden">
          <WorkspaceSelect
            rows={rows}
            selection={selection}
            onSelectAll={() => setSelection(selectAll())}
            onSelectOnly={(root) => setSelection(selectOnly(root))}
            onToggleRoots={(roots) => setSelection(toggleRoots(selection, roots, allRoots))}
          />
        </div>
        <span
          className={
            streamStatus === "connected"
              ? "rounded-full bg-emerald-500/15 px-2 py-0.5 text-xs font-medium text-emerald-600 dark:text-emerald-400"
              : "rounded-full bg-muted px-2 py-0.5 text-xs font-medium text-muted-foreground"
          }
          title={streamStatus}
        >
          {streamStatus === "connected" ? "● Live" : streamStatus === "connecting" ? "○ connecting" : "○ offline"}
        </span>
        <Button
          variant="outline"
          size="sm"
          onClick={() => void load()}
          disabled={refreshing}
        >
          <RefreshCw className={refreshing ? "size-4 animate-spin" : "size-4"} />
          <span className="hidden sm:inline-block">Refresh</span>
        </Button>
      </DashboardHeader>

      <div className="flex min-h-0 w-full flex-1">
        <WorkspaceScopePanel
          rows={rows}
          selection={selection}
          onSelectAll={() => setSelection(selectAll())}
          onSelectOnly={(root) => setSelection(selectOnly(root))}
          onToggleRoots={(roots) => setSelection(toggleRoots(selection, roots, allRoots))}
        />
        <div className={contentColumn}>
        {runtimesError && (
          <div className="rounded-lg border border-amber-500/40 bg-amber-500/10 p-3 text-xs text-amber-600 dark:text-amber-400">
            The workspace list failed to load ({runtimesError}); the picker may be stale. Reload the page to retry.
          </div>
        )}
        {snapshot && (
          <div className="flex min-w-0 flex-wrap items-center gap-x-4 gap-y-1 text-xs text-muted-foreground">
            <span className="flex min-w-0 items-center gap-1.5">
              <span className="shrink-0 font-medium text-foreground">Workspaces</span>
              <span
                className="min-w-0 truncate font-mono"
                title={selection.kind === "all" ? "Every registered workspace" : selection.roots.join("\n")}
              >
                {selection.kind === "all"
                  ? "All workspaces"
                  : selection.roots.length === 1
                    ? workspaceName(selection.roots[0])
                    : `${selection.roots.length} workspaces selected`}
              </span>
            </span>
            <span className="flex shrink-0 items-center gap-1.5">
              <span className="font-medium text-foreground">Branch</span>
              {snapshot.branch ?? "all branches"}
            </span>
          </div>
        )}

        {error && (
          <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-3 text-sm text-destructive">
            {error}
          </div>
        )}

        <Card>
          <CardHeader>
            <CardTitle>Active</CardTitle>
            <CardAction>
              <Badge variant="secondary">{active.length}</Badge>
            </CardAction>
          </CardHeader>
          <CardContent>
            <div className="grid min-w-0 grid-cols-1 gap-3 2xl:grid-cols-2">
              {showSkeleton &&
                Array.from({ length: 3 }).map((_, i) => (
                  <Skeleton key={i} className="h-28 w-full rounded-xl" />
                ))}
              {!showSkeleton && active.length === 0 && (
                <div className="flex min-h-[140px] flex-col items-center justify-center rounded-xl border border-dashed p-6 text-center text-sm text-muted-foreground 2xl:col-span-2">
                  {selection.kind === "all"
                    ? "No active leases in any workspace. The slot is open."
                    : "No active leases in the selected workspaces."}
                </div>
              )}
              {active.map((lease) => {
                const paths = lease.paths ?? []
                return (
                  <div
                    key={lease.lease_id}
                    className="flex flex-col justify-between gap-3 rounded-xl border bg-card p-4 sm:flex-row sm:items-start sm:gap-4"
                  >
                    <div className="flex min-w-0 flex-1 flex-col gap-1.5">
                      <div className="flex flex-wrap items-center gap-2">
                        <span className="text-base font-semibold" title={lease.checkout}>
                          {workspaceName(lease.checkout)}
                        </span>
                        <Badge variant="secondary" className="font-mono">
                          {lease.branch ?? "—"}
                        </Badge>
                        {liveTtlRemaining(lease) <= 0 ? (
                          <span
                            className="inline-flex items-center rounded-full border border-amber-500/40 bg-amber-500/10 px-2.5 py-0.5 text-xs font-medium text-amber-600 dark:text-amber-400"
                            title={`TTL remaining (${lease.ttl_seconds}s total)`}
                          >
                            0s · expired
                          </span>
                        ) : (
                          <Badge
                            variant="secondary"
                            className="tabular-nums"
                            title={`TTL remaining (${lease.ttl_seconds}s total)`}
                          >
                            {formatAge(liveTtlRemaining(lease))} left
                          </Badge>
                        )}
                      </div>
                      <PathText prefix="📁" path={lease.checkout} className="text-xs" />
                      <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-muted-foreground">
                        <span className="inline-flex items-center gap-1" title={lease.since}>
                          <Clock className="size-3.5" />
                          Since {utcToLocal(lease.since) || lease.since}
                        </span>
                        <span
                          className="inline-flex items-center gap-1 tabular-nums"
                          title="Live age: frame value + elapsed wall-clock"
                        >
                          <Timer className="size-3.5" />
                          Age {formatAge(liveAge(lease))}
                        </span>
                        <span
                          className="inline-flex items-center gap-1"
                          title={utcToLocal(lease.last_heartbeat) || lease.last_heartbeat}
                        >
                          <HeartPulse className="size-3.5" />
                          Heartbeat {relativeTime(lease.last_heartbeat)}
                        </span>
                      </div>
                      <div className="mt-0.5 break-all font-mono text-[11px] leading-5 text-muted-foreground">
                        <div title={lease.lease_id}>Lease {lease.lease_id}</div>
                        <div title={lease.session_id}>Session {lease.session_id}</div>
                      </div>
                      <div className="break-words text-sm">{lease.intent ?? "—"}</div>
                      <LeasePaths paths={paths} />
                    </div>
                    <div className="flex shrink-0 flex-col justify-center self-stretch sm:w-40">
                      <Button
                        variant="outline"
                        size="sm"
                        className="justify-start border-destructive/40 text-destructive hover:bg-destructive/10 hover:text-destructive"
                        onClick={() => setForceTarget(lease)}
                      >
                        Force release
                      </Button>
                    </div>
                  </div>
                )
              })}
            </div>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Recently released</CardTitle>
            <CardAction>
              <Badge variant="secondary">{released.length}</Badge>
            </CardAction>
          </CardHeader>
          <CardContent>
            <div className="grid min-w-0 grid-cols-1 gap-3 2xl:grid-cols-2">
              {showSkeleton &&
                Array.from({ length: 2 }).map((_, i) => (
                  <Skeleton key={i} className="h-28 w-full rounded-xl" />
                ))}
              {!showSkeleton && released.length === 0 && (
                <div className="flex min-h-[140px] flex-col items-center justify-center rounded-xl border border-dashed p-6 text-center text-sm text-muted-foreground 2xl:col-span-2">
                  {selection.kind === "all"
                    ? "Nothing released yet. Leases land here for a while after they let go."
                    : "Nothing released in the selected workspaces yet."}
                </div>
              )}
              {released.map((lease) => (
                <div
                  key={lease.lease_id}
                  className="flex flex-col gap-1.5 rounded-xl border bg-card p-4"
                >
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="text-base font-semibold" title={lease.checkout}>
                      {workspaceName(lease.checkout)}
                    </span>
                    <ReleaseReasonBadge reason={lease.release_reason} />
                  </div>
                  <PathText prefix="📁" path={lease.checkout} className="text-xs" />
                  <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-muted-foreground">
                    <span
                      className="inline-flex items-center gap-1"
                      title={lease.released_at ?? undefined}
                    >
                      <CheckCircle2 className="size-3.5" />
                      {lease.released_at
                        ? `Released ${utcToLocal(lease.released_at) || lease.released_at} (${relativeTime(lease.released_at)})`
                        : "Released —"}
                    </span>
                    <span className="inline-flex items-center gap-1" title={lease.since}>
                      <Clock className="size-3.5" />
                      Since {utcToLocal(lease.since) || lease.since}
                    </span>
                  </div>
                  <div className="mt-0.5 break-all font-mono text-[11px] leading-5 text-muted-foreground">
                    <div title={lease.lease_id}>Lease {lease.lease_id}</div>
                    <div title={lease.session_id}>Session {lease.session_id}</div>
                  </div>
                  <div className="break-words text-sm">{lease.intent ?? "—"}</div>
                  <LeasePaths paths={lease.paths ?? []} />
                </div>
              ))}
            </div>
          </CardContent>
        </Card>
        </div>
      </div>

      {/* Confirmation for the one destructive action on this page. */}
      <AlertDialog
        open={forceTarget !== null}
        onOpenChange={(open) => {
          if (!open && !releasing) setForceTarget(null)
        }}
      >
        <AlertDialogContent
          className="w-[94vw] sm:max-w-lg"
          onOverlayClick={() => !releasing && setForceTarget(null)}
        >
          <AlertDialogHeader>
            <div className="flex items-center gap-2 text-destructive">
              <AlertTriangle className="size-5" />
              <AlertDialogTitle>Force release lease?</AlertDialogTitle>
            </div>
            <AlertDialogDescription>
              This releases the lease regardless of which session holds it. The owning
              session does not get a say; use this only for a wedged lease or when the
              slot must open now.
            </AlertDialogDescription>
          </AlertDialogHeader>
          {forceTarget && (
            <div className="rounded-lg border bg-muted/40 p-3 text-xs">
              <div className="font-semibold text-foreground">
                Session {shortSession(forceTarget.session_id)} · {workspaceName(forceTarget.checkout)}
              </div>
              <div className="mt-0.5 break-all font-mono text-muted-foreground" title={forceTarget.checkout}>
                {forceTarget.checkout}
              </div>
              <div className="mt-0.5 break-all font-mono text-muted-foreground" title={forceTarget.session_id}>
                {forceTarget.session_id}
              </div>
              {forceTarget.intent && (
                <div className="mt-2 text-foreground">{forceTarget.intent}</div>
              )}
              {forceTarget.paths && forceTarget.paths.length > 0 && (
                <ul className="mt-2 list-inside list-disc text-muted-foreground">
                  {forceTarget.paths.map((p) => (
                    <li key={p} className="break-all font-mono">{p}</li>
                  ))}
                </ul>
              )}
            </div>
          )}
          <AlertDialogFooter>
            <AlertDialogCancel disabled={releasing}>Cancel</AlertDialogCancel>
            <AlertDialogAction
              onClick={(e) => {
                e.preventDefault()
                void confirmForceRelease()
              }}
              disabled={releasing}
              className="bg-destructive text-destructive-foreground hover:bg-destructive/90"
            >
              {releasing ? "Releasing…" : "Force release"}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

      {/* Toast stack, bottom-right; mirrors the Tools page notifications. */}
      {notifications.length > 0 && (
        <div className="pointer-events-none fixed bottom-4 right-4 z-50 flex w-80 max-w-[calc(100vw-2rem)] flex-col gap-2">
          {notifications.map((notification) => (
            <ToastItem key={notification.id} notification={notification} onDismiss={dismissNotification} />
          ))}
        </div>
      )}
    </div>
  )
}

function ReleaseReasonBadge({ reason }: { reason: string | null }) {
  if (!reason) return <span className="text-muted-foreground">—</span>
  return (
    <Badge variant={reason === "forced" ? "destructive" : "secondary"} className="font-mono">
      {reason}
    </Badge>
  )
}

function ToastItem({
  notification,
  onDismiss,
}: {
  notification: Notification
  onDismiss: (id: number) => void
}) {
  useEffect(() => {
    const timer = setTimeout(() => onDismiss(notification.id), NOTIFICATION_TTL_MS)
    return () => clearTimeout(timer)
  }, [notification.id, onDismiss])

  return (
    <div
      role="status"
      className="pointer-events-auto flex items-start gap-2.5 rounded-lg border bg-background p-3 shadow-lg animate-in fade-in-0 slide-in-from-right-8 duration-300"
    >
      <span
        className={
          notification.type === "error"
            ? "flex size-7 shrink-0 items-center justify-center rounded-full bg-destructive/15 text-destructive"
            : "flex size-7 shrink-0 items-center justify-center rounded-full bg-emerald-500/15 text-emerald-600 dark:text-emerald-400"
        }
      >
        {notification.type === "error" ? (
          <AlertCircle className="size-4" />
        ) : (
          <CheckCircle2 className="size-4" />
        )}
      </span>
      <div className="min-w-0 flex-1">
        <p className="text-sm font-medium">{notification.title}</p>
        {notification.text ? (
          <p className="mt-0.5 text-xs break-words text-muted-foreground">{notification.text}</p>
        ) : null}
      </div>
      <button
        type="button"
        onClick={() => onDismiss(notification.id)}
        aria-label="Dismiss notification"
        className="shrink-0 rounded-md p-1 text-muted-foreground transition-colors hover:bg-muted hover:text-foreground"
      >
        <X className="size-3.5" />
      </button>
    </div>
  )
}
