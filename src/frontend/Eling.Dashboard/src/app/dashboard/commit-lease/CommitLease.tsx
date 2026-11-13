"use client"

import { useCallback, useEffect, useMemo, useState } from "react"
import {
  AlertCircle,
  AlertTriangle,
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  RefreshCw,
  X,
} from "lucide-react"

import { DashboardHeader } from "@/components/dashboard-header"
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
  const [loading, setLoading] = useState(true)
  const [refreshing, setRefreshing] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [streamStatus, setStreamStatus] = useState<StreamStatus>("connecting")
  const [expanded, setExpanded] = useState<Record<string, boolean>>({})
  const [forceTarget, setForceTarget] = useState<CommitLeaseRow | null>(null)
  const [releasing, setReleasing] = useState(false)
  const [notifications, setNotifications] = useState<Notification[]>([])
  const [selection, setSelection] = useState<WorkspaceSelection>(() => selectAll())

  const { rows, error: runtimesError } = useWorkspaceRows()
  const allRoots = useMemo(() => rows.map((r) => r.root), [rows])

  const dismissNotification = useCallback((id: number) => {
    setNotifications((current) => current.filter((item) => item.id !== id))
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
      setSnapshot(data ?? null)
    } catch (e) {
      setError(`Failed to load the lease snapshot (${errorMessage(e)})`)
    } finally {
      setLoading(false)
      setRefreshing(false)
    }
  }, [])

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
          setSnapshot(JSON.parse(event.data) as CommitLeaseSnapshot)
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
  }, [])

  const toggleExpanded = useCallback((leaseId: string) => {
    setExpanded((current) => ({ ...current, [leaseId]: !current[leaseId] }))
  }, [])

  const confirmForceRelease = useCallback(async () => {
    if (!forceTarget) return
    setReleasing(true)
    try {
      const fresh = await apiFetch<CommitLeaseSnapshot>(
        `/api/commit-lease/${encodeURIComponent(forceTarget.lease_id)}/release?checkout=${encodeURIComponent(forceTarget.checkout)}`,
        { method: "POST" }
      )
      if (fresh) setSnapshot(fresh)
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
  }, [forceTarget, notify])

  const contentColumn = "flex min-w-0 w-full flex-1 flex-col gap-4 p-4"
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
  const showWorkspace = selection.kind === "all" || selection.roots.length > 1
  const activeColSpan = showWorkspace ? 7 : 6
  const releasedColSpan = showWorkspace ? 6 : 5
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
            <div className="overflow-x-auto rounded-xl border">
              <table className="w-full min-w-max text-sm">
                <thead>
                  <tr className="border-b text-left text-muted-foreground">
                    <th scope="col" className="w-8 p-3" aria-label="Expand row" />
                    {showWorkspace && <th scope="col" className="p-3">Workspace</th>}
                    <th scope="col" className="p-3">Since</th>
                    <th scope="col" className="p-3">Age</th>
                    <th scope="col" className="p-3">TTL</th>
                    <th scope="col" className="p-3">Intent</th>
                    <th scope="col" className="p-3" />
                  </tr>
                </thead>
                <tbody>
                  {showSkeleton && (
                    <tr>
                      <td className="p-3" colSpan={activeColSpan}>
                        <Skeleton className="h-6 w-full" />
                      </td>
                    </tr>
                  )}
                  {!showSkeleton && active.length === 0 && (
                    <tr>
                      <td className="p-6 text-center text-muted-foreground" colSpan={activeColSpan}>
                        {selection.kind === "all"
                          ? "No active leases in any workspace. The slot is open."
                          : "No active leases in the selected workspaces."}
                      </td>
                    </tr>
                  )}
                  {active.map((lease) => {
                    const isExpanded = Boolean(expanded[lease.lease_id])
                    const paths = lease.paths ?? []
                    return [
                      <tr key={lease.lease_id} className="border-b last:border-0 align-top">
                        <td className="p-2">
                          <button
                            type="button"
                            onClick={() => toggleExpanded(lease.lease_id)}
                            aria-expanded={isExpanded}
                            aria-label={isExpanded ? "Hide lease details" : "Show lease details"}
                            className="rounded-md p-1 text-muted-foreground transition-colors hover:bg-muted hover:text-foreground"
                          >
                            {isExpanded ? <ChevronDown className="size-4" /> : <ChevronRight className="size-4" />}
                          </button>
                        </td>
                        {showWorkspace && (
                          <td className="p-3 whitespace-nowrap" title={lease.checkout}>
                            {workspaceName(lease.checkout)}
                          </td>
                        )}
                        <td className="p-3 whitespace-nowrap" title={lease.since}>
                          {utcToLocal(lease.since) || lease.since}
                        </td>
                        <td className="p-3 whitespace-nowrap tabular-nums" title="Age at the last snapshot frame">
                          {formatAge(lease.lease_age_seconds)}
                        </td>
                        <td className="p-3 whitespace-nowrap tabular-nums">
                          {formatAge(lease.ttl_seconds)}
                        </td>
                        <td className="max-w-56 p-3">
                          <span className="block truncate" title={lease.intent ?? undefined}>
                            {lease.intent ?? "—"}
                          </span>
                        </td>
                        <td className="p-3 whitespace-nowrap text-right">
                          <Button
                            variant="outline"
                            size="sm"
                            className="border-destructive/40 text-destructive hover:bg-destructive/10 hover:text-destructive"
                            onClick={() => setForceTarget(lease)}
                          >
                            Force release
                          </Button>
                        </td>
                      </tr>,
                      isExpanded && (
                        <tr key={`${lease.lease_id}-detail`} className="border-b last:border-0 bg-muted/30">
                          <td className="p-3" colSpan={activeColSpan}>
                            <CommitLeaseDetail
                              rows={[
                                ["Lease ID", lease.lease_id],
                                ["Session ID", lease.session_id],
                                ["Workspace", lease.checkout],
                                ["Branch", lease.branch ?? "—"],
                                ["Since", `${utcToLocal(lease.since) || lease.since} (${lease.since})`],
                                ["Last heartbeat", `${utcToLocal(lease.last_heartbeat) || lease.last_heartbeat} (${lease.last_heartbeat})`],
                                ["Lease age", `${formatAge(lease.lease_age_seconds)} (${lease.lease_age_seconds.toFixed(1)}s at the last frame)`],
                                ["TTL", `${lease.ttl_seconds}s`],
                                ["Intent", lease.intent ?? "—"],
                              ]}
                              paths={paths}
                            />
                          </td>
                        </tr>
                      ),
                    ]
                  })}
                </tbody>
              </table>
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
            <div className="overflow-x-auto rounded-xl border">
              <table className="w-full min-w-max text-sm">
                <thead>
                  <tr className="border-b text-left text-muted-foreground">
                    <th scope="col" className="w-8 p-3" aria-label="Expand row" />
                    {showWorkspace && <th scope="col" className="p-3">Workspace</th>}
                    <th scope="col" className="p-3">Released</th>
                    <th scope="col" className="p-3">Reason</th>
                    <th scope="col" className="p-3">Intent</th>
                  </tr>
                </thead>
                <tbody>
                  {showSkeleton && (
                    <tr>
                      <td className="p-3" colSpan={releasedColSpan}>
                        <Skeleton className="h-6 w-full" />
                      </td>
                    </tr>
                  )}
                  {!showSkeleton && released.length === 0 && (
                    <tr>
                      <td className="p-6 text-center text-muted-foreground" colSpan={releasedColSpan}>
                        {selection.kind === "all"
                          ? "Nothing released yet. Leases land here for a while after they let go."
                          : "Nothing released in the selected workspaces yet."}
                      </td>
                    </tr>
                  )}
                  {released.map((lease) => {
                    const isExpanded = Boolean(expanded[lease.lease_id])
                    const paths = lease.paths ?? []
                    return [
                      <tr key={lease.lease_id} className="border-b last:border-0 align-top">
                        <td className="p-2">
                          <button
                            type="button"
                            onClick={() => toggleExpanded(lease.lease_id)}
                            aria-expanded={isExpanded}
                            aria-label={isExpanded ? "Hide lease details" : "Show lease details"}
                            className="rounded-md p-1 text-muted-foreground transition-colors hover:bg-muted hover:text-foreground"
                          >
                            {isExpanded ? <ChevronDown className="size-4" /> : <ChevronRight className="size-4" />}
                          </button>
                        </td>
                        {showWorkspace && (
                          <td className="p-3 whitespace-nowrap" title={lease.checkout}>
                            {workspaceName(lease.checkout)}
                          </td>
                        )}
                        <td className="p-3 whitespace-nowrap" title={lease.released_at ?? undefined}>
                          {lease.released_at
                            ? `${utcToLocal(lease.released_at) || lease.released_at} (${relativeTime(lease.released_at)})`
                            : "—"}
                        </td>
                        <td className="p-3">
                          <ReleaseReasonBadge reason={lease.release_reason} />
                        </td>
                        <td className="max-w-56 p-3">
                          <span className="block truncate" title={lease.intent ?? undefined}>
                            {lease.intent ?? "—"}
                          </span>
                        </td>
                      </tr>,
                      isExpanded && (
                        <tr key={`${lease.lease_id}-detail`} className="border-b last:border-0 bg-muted/30">
                          <td className="p-3" colSpan={releasedColSpan}>
                            <CommitLeaseDetail
                              rows={[
                                ["Lease ID", lease.lease_id],
                                ["Session ID", lease.session_id],
                                ["Workspace", lease.checkout],
                                ["Since", `${utcToLocal(lease.since) || lease.since} (${lease.since})`],
                                ["Released at", lease.released_at ? `${utcToLocal(lease.released_at) || lease.released_at} (${lease.released_at})` : "—"],
                                ["Release reason", lease.release_reason ?? "—"],
                                ["Intent", lease.intent ?? "—"],
                              ]}
                              paths={paths}
                            />
                          </td>
                        </tr>
                      ),
                    ]
                  })}
                </tbody>
              </table>
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

function CommitLeaseDetail({
  rows,
  paths,
}: {
  rows: Array<[string, string]>
  paths: string[]
}) {
  return (
    <div className="grid gap-3 text-xs sm:grid-cols-2">
      <dl className="flex flex-col divide-y divide-border/60">
        {rows.map(([label, value]) => (
          <div key={label} className="flex items-baseline justify-between gap-4 py-1.5">
            <dt className="shrink-0 text-muted-foreground">{label}</dt>
            <dd className="min-w-0 break-all text-right font-mono" title={value}>
              {value}
            </dd>
          </div>
        ))}
      </dl>
      <div className="min-w-0">
        <div className="text-muted-foreground">Paths</div>
        {paths.length === 0 ? (
          <div className="mt-1.5 text-muted-foreground">—</div>
        ) : (
          <ul className="mt-1.5 flex flex-col gap-1 font-mono">
            {paths.map((p) => (
              <li key={p} className="break-all" title={p}>
                {p}
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
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
