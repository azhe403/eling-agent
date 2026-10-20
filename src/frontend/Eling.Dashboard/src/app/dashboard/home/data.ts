"use client"

import { useCallback, useEffect, useRef, useState } from "react"

import { useMemoriesSse, type SseStatus } from "@/hooks/use-memories-sse"
import {
  group,
  type WorkspaceGroup,
} from "@/app/dashboard/workspaces/aggregate"

/**
 * The dashboard's data layer: every read the home page makes, and the shapes
 * those reads return.
 *
 * The home page is a client component on purpose. The dashboard ships as a
 * static export, so a server component's fetch runs at BUILD time and its
 * numbers are frozen into the HTML - a page whose vitals never move, and that
 * ships its build-time error state forever when the backend is not reachable
 * during the build. Reading from the browser is what makes the page live.
 *
 * Every read is independent and separately fault-tolerant. One endpoint being
 * down must not blank the whole page, so a failed read keeps its own error and
 * every other section still renders.
 */

/** One read: either a value, or the reason there is none. */
export type Section<T> = {
  value: T | null
  error: string | null
}

export type MemoryStats = {
  total: number
  byType: Record<string, number>
  byStatus: Record<string, number>
  byScope: Record<string, number>
}

export type MemoryRow = {
  id: string
  type: string
  status: string
  content: string
  tags: string[]
  createdAt: string
  updatedAt: string
  scope?: string
  project?: { id: string; root: string } | null
}

export type AuditEvent = {
  timestamp: string
  trail: string
  category: string
  action: string
  actor: string
  source: string
  outcome: string
  scope?: string
  projectRoot?: string
  target?: string
  durationMs?: number
}

export type RuntimeRow = {
  processId: number
  headScopeRoot: string
  workspaceRoot: string
  dataDirectory: string
  codebaseEnabled: boolean
  isAlive: boolean
  lastHeartbeat: string
  startTime: string
  mcpEnabled: boolean
  mcpTransport: string
  memoryBytes?: number | null
  cpuPercent?: number | null
  isDashboardOwner: boolean
  dashboardPort?: number | null
}

export type CodebaseStatusRow = {
  workspaceRoot: string
  files: number
  chunks: number
  lastIndexedAt: string | null
}

export type DatabaseConnection = {
  id: string
  name: string
  provider: string
  environment: string
  hasConnectionString: boolean
  allowAgentMutations: boolean
  requireApprovalForMutations: boolean
  createdAt: string
  lastConnectedAt: string | null
}

export type ToolRow = {
  name: string
  group: string
  description: string
  enabled: boolean
  isProtected: boolean
}

export type JudgeView = {
  enabled: boolean
  baseUrl: string | null
  model: string | null
  hasApiKey: boolean
  isConfigured: boolean
  timeoutSeconds: number | null
}

export type HealthInfo = {
  status: string
  pid: number
}

/** One workspace, with the process metrics and the index size rolled together. */
export type WorkspaceSummary = {
  root: string
  name: string
  running: boolean
  processCount: number
  cpuPercent: number | null
  memoryBytes: number | null
  files: number
  chunks: number
  lastIndexedAt: string | null
}

export type DashboardData = {
  stats: Section<MemoryStats>
  memories: Section<MemoryRow[]>
  activity: Section<AuditEvent[]>
  workspaces: Section<WorkspaceSummary[]>
  connections: Section<DatabaseConnection[]>
  health: Section<HealthInfo>
  tools: Section<ToolRow[]>
  judge: Section<JudgeView>
}

/** How many rows the two feeds show. Enough to read at a glance, not a list. */
const FEED_LIMIT = 8

/**
 * The activity feed shows the last day. A wider window would bury today's
 * events under a week of noise; a narrower one would hide a slow change that
 * still happened.
 */
const ACTIVITY_WINDOW_HOURS = 24

function emptySection<T>(): Section<T> {
  return { value: null, error: null }
}

function emptyData(): DashboardData {
  return {
    stats: emptySection<MemoryStats>(),
    memories: emptySection<MemoryRow[]>(),
    activity: emptySection<AuditEvent[]>(),
    workspaces: emptySection<WorkspaceSummary[]>(),
    connections: emptySection<DatabaseConnection[]>(),
    health: emptySection<HealthInfo>(),
    tools: emptySection<ToolRow[]>(),
    judge: emptySection<JudgeView>(),
  }
}

/**
 * Where the API lives. The dashboard is served by the same process as the API,
 * so a relative path is the normal case; the absolute one is for a split
 * deployment that points the static bundle at a different backend.
 */
function apiUrl(path: string): string {
  const base = (process.env.NEXT_PUBLIC_API_URL ?? "").replace(/\/$/, "")
  return base ? `${base}${path}` : path
}

async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const res = await fetch(apiUrl(path), { cache: "no-store", signal })
  if (!res.ok) throw new Error(`HTTP ${res.status}`)

  // The dashboard's unknown routes fall through to the SPA shell, and that
  // shell answers with 200 and an HTML body. So a route this dashboard knows
  // but the running backend does not - an older backend, or one that was not
  // restarted after a release - comes back as HTML, and "Unexpected token
  // '<'" tells the reader nothing. Saying what actually happened is worth
  // more than the parse error.
  const contentType = res.headers.get("content-type") ?? ""
  if (!contentType.includes("json")) {
    const kind = contentType.split(";")[0].trim() || "an unknown type"
    throw new Error(`${kind} instead of JSON - the backend may predate this dashboard`)
  }

  return (await res.json()) as T
}

function messageOf(reason: unknown): string {
  if (reason instanceof Error) return reason.message
  return String(reason)
}

async function loadStats(signal?: AbortSignal): Promise<MemoryStats> {
  return getJson<MemoryStats>("/api/stats/memories", signal)
}

async function loadRecentMemories(signal?: AbortSignal): Promise<MemoryRow[]> {
  const body = await getJson<{ items?: unknown }>(
    `/api/aggregated/memories?limit=${FEED_LIMIT}`,
    signal
  )
  const rows = Array.isArray(body?.items) ? body.items : []
  return rows.map((raw) => {
    const row = raw as Record<string, unknown>
    return {
      id: String(row.id ?? ""),
      type: String(row.type ?? "Note"),
      status: String(row.status ?? "Active"),
      content: String(row.content ?? ""),
      tags: Array.isArray(row.tags) ? row.tags.map(String) : [],
      createdAt: String(row.createdAt ?? ""),
      updatedAt: String(row.updatedAt ?? ""),
      scope: row.scope ? String(row.scope) : undefined,
      project: (row.project ?? null) as MemoryRow["project"],
    }
  })
}

async function loadRecentActivity(signal?: AbortSignal): Promise<AuditEvent[]> {
  const to = new Date()
  const from = new Date(to.getTime() - ACTIVITY_WINDOW_HOURS * 60 * 60 * 1000)
  const query = `from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}&limit=${FEED_LIMIT}`
  const body = await getJson<{ items?: unknown }>(
    `/api/audit/changes?${query}`,
    signal
  )
  const rows = Array.isArray(body?.items) ? body.items : []
  return rows.map((raw) => {
    const row = raw as Record<string, unknown>
    return {
      timestamp: String(row.timestamp ?? ""),
      trail: String(row.trail ?? "change"),
      category: String(row.category ?? ""),
      action: String(row.action ?? ""),
      actor: String(row.actor ?? ""),
      source: String(row.source ?? ""),
      outcome: String(row.outcome ?? "success"),
      scope: row.scope ? String(row.scope) : undefined,
      projectRoot: row.projectRoot ? String(row.projectRoot) : undefined,
      target: row.target ? String(row.target) : undefined,
      durationMs: typeof row.durationMs === "number" ? row.durationMs : undefined,
    }
  })
}

/**
 * One row per workspace: the running processes rolled up, plus the size of
 * that workspace's codebase index. The two come from different endpoints -
 * the coordinator knows the processes, the codebase status knows the index -
 * and they are joined on the workspace root, the same join the Workspaces page
 * makes.
 */
async function loadWorkspaces(signal?: AbortSignal): Promise<WorkspaceSummary[]> {
  const [runtimes, statuses] = await Promise.all([
    getJson<RuntimeRow[]>(
      "/api/coordinator/runtimes?includeRegistered=true&allProcesses=true",
      signal
    ),
    getJson<CodebaseStatusRow[]>(
      "/api/coordinator/projects/codebase-statuses",
      signal
    ).catch(() => [] as CodebaseStatusRow[]),
  ])

  const statusByRoot = new Map<string, CodebaseStatusRow>()
  for (const status of statuses ?? []) {
    if (!status?.workspaceRoot) continue
    statusByRoot.set(status.workspaceRoot.toLowerCase(), status)
  }

  return group(Array.isArray(runtimes) ? runtimes : [])
    .filter((entry) => !isUserScope(entry.root))
    .map((entry) => toSummary(entry, statusByRoot))
    .sort(
      (a, b) =>
        Number(b.running) - Number(a.running) || a.name.localeCompare(b.name)
    )
}

/** The user-home scope is not a workspace; it has no folder to name. */
function isUserScope(root: string): boolean {
  return !root || root === "UserScope"
}

function toSummary(
  entry: WorkspaceGroup,
  statusByRoot: Map<string, CodebaseStatusRow>
): WorkspaceSummary {
  const status = statusByRoot.get(entry.root.toLowerCase())
  const parts = entry.root.split(/[/\\]+/).filter(Boolean)
  return {
    root: entry.root,
    name: parts.length ? parts[parts.length - 1] : entry.root,
    running: entry.running,
    processCount: entry.processes.length,
    cpuPercent: entry.totalCpuPercent,
    memoryBytes: entry.totalMemoryBytes,
    files: status?.files ?? 0,
    chunks: status?.chunks ?? 0,
    lastIndexedAt: status?.lastIndexedAt ?? null,
  }
}

async function loadConnections(signal?: AbortSignal): Promise<DatabaseConnection[]> {
  const rows = await getJson<DatabaseConnection[]>("/api/databases/", signal)
  if (!Array.isArray(rows)) return []
  return rows.map((row) => ({
    id: String(row.id ?? ""),
    name: String(row.name ?? ""),
    provider: String(row.provider ?? ""),
    environment: String(row.environment ?? ""),
    hasConnectionString: Boolean(row.hasConnectionString),
    allowAgentMutations: Boolean(row.allowAgentMutations),
    requireApprovalForMutations: Boolean(row.requireApprovalForMutations),
    createdAt: String(row.createdAt ?? ""),
    lastConnectedAt: row.lastConnectedAt ? String(row.lastConnectedAt) : null,
  }))
}

async function loadHealth(signal?: AbortSignal): Promise<HealthInfo> {
  // Under /api, not at the root: the dashboard's dev proxy only forwards
  // the /api prefix to the backend, so a read of "/health" would be answered
  // by the frontend server and never reach the API. The backend serves the
  // same payload at both paths.
  const body = await getJson<{ status?: unknown; pid?: unknown }>(
    "/api/health",
    signal
  )
  return {
    status: String(body?.status ?? "Healthy"),
    pid: typeof body?.pid === "number" ? body.pid : 0,
  }
}

async function loadTools(signal?: AbortSignal): Promise<ToolRow[]> {
  const rows = await getJson<ToolRow[]>("/api/tools/", signal)
  return Array.isArray(rows) ? rows : []
}

async function loadJudge(signal?: AbortSignal): Promise<JudgeView> {
  return getJson<JudgeView>("/api/judge/config", signal)
}

/**
 * The home page's data, plus the live-stream state and a manual refresh.
 *
 * The SSE stream is what keeps the page honest: a memory saved in another
 * window, a workspace that starts, an index that rebuilds - each of those
 * broadcasts a topic, and the page reloads. Reloads are debounced, because a
 * rebuild broadcasts a burst and a page that reloads per event would hammer
 * the API for nothing.
 */
export function useDashboardData(): {
  data: DashboardData
  loading: boolean
  refreshing: boolean
  streamStatus: SseStatus
  refresh: () => void
} {
  const [data, setData] = useState<DashboardData>(emptyData)
  const [loading, setLoading] = useState(true)
  const [refreshing, setRefreshing] = useState(false)
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const requestIdRef = useRef(0)
  const abortRef = useRef<AbortController | null>(null)
  const unmountedRef = useRef(false)

  const load = useCallback(async () => {
    const requestId = ++requestIdRef.current
    const controller = new AbortController()
    abortRef.current = controller
    const signal = controller.signal

    const [
      stats,
      memories,
      activity,
      workspaces,
      connections,
      health,
      tools,
      judge,
    ] = await Promise.allSettled([
      loadStats(signal),
      loadRecentMemories(signal),
      loadRecentActivity(signal),
      loadWorkspaces(signal),
      loadConnections(signal),
      loadHealth(signal),
      loadTools(signal),
      loadJudge(signal),
    ])

    abortRef.current = null

    // A newer load started while this one was in flight: its result is the
    // one the page should show, so this one is dropped rather than written.
    if (unmountedRef.current || requestId !== requestIdRef.current) return

    setData({
      stats: toSection(stats),
      memories: toSection(memories),
      activity: toSection(activity),
      workspaces: toSection(workspaces),
      connections: toSection(connections),
      health: toSection(health),
      tools: toSection(tools),
      judge: toSection(judge),
    })
    setLoading(false)
    setRefreshing(false)
  }, [])

  const scheduleReload = useCallback(() => {
    if (timerRef.current) return
    timerRef.current = setTimeout(() => {
      timerRef.current = null
      setRefreshing(true)
      void load()
    }, 350)
  }, [load])

  useEffect(() => {
    unmountedRef.current = false
    void load()
    return () => {
      if (timerRef.current) clearTimeout(timerRef.current)
      // Discard the in-flight result: the page is going away, and a write
      // after unmount would update state nobody is looking at.
      unmountedRef.current = true
      abortRef.current?.abort()
    }
  }, [load])

  const { status: streamStatus } = useMemoriesSse(
    scheduleReload,
    scheduleReload,
    ["dashboard", "coordinator", "runtimes", "codebase", "audit:changes"]
  )

  return {
    data,
    loading,
    refreshing,
    streamStatus,
    refresh: () => {
      setRefreshing(true)
      void load()
    },
  }
}

function toSection<T>(result: PromiseSettledResult<T>): Section<T> {
  if (result.status === "fulfilled") return { value: result.value, error: null }
  return { value: null, error: messageOf(result.reason) }
}
