/**
 * One row per live eling process (or one per remembered workspace). Several
 * stdio sessions can run in one folder — one per opencode/openchamber
 * session, plus peers and the desktop app — so the workspaces page groups
 * the rows back per folder and sums the metrics instead of following the
 * old `Alive()` dedupe, which kept a single, arbitrary representative
 * process. Nothing here touches React: same input, same output.
 */

export type RuntimeDto = {
  processId: number
  headScopeRoot: string
  workspaceRoot: string
  codebaseEnabled: boolean
  dataDirectory: string
  startTime: string
  mcpEnabled: boolean
  mcpTransport: string
  lastHeartbeat: string
  isAlive: boolean
  memoryBytes?: number | null
  cpuPercent?: number | null
  isDashboardOwner: boolean
  dashboardPort?: number | null
}

export type WorkspaceProcess = {
  pid: number
  startTime: string
  transport: string
  owner: boolean
  ownerPort: number | null
  memoryBytes: number | null
  cpuPercent: number | null
}

export type WorkspaceGroup = {
  root: string
  processes: WorkspaceProcess[]
  running: boolean
  /** Summed working set, or null until at least one process reports metrics. */
  totalMemoryBytes: number | null
  /** Summed CPU percent across processes; 100% here means every core busy. */
  totalCpuPercent: number | null
  lastHeartbeat: string
  codebaseEnabled: boolean
  transport: string | null
  ownerPort: number | null
}

/** Coarse elapsed time since the process started: `3h 02m`, `12m 30s`, `4s`. */
export function uptime(iso: string): string {
  const started = new Date(iso).getTime()
  if (Number.isNaN(started)) return "—"
  const seconds = Math.max(0, Math.floor((Date.now() - started) / 1000))
  const days = Math.floor(seconds / 86400)
  const hours = Math.floor((seconds % 86400) / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  const secs = seconds % 60
  if (days > 0) return `${days}d ${String(hours).padStart(2, "0")}h`
  if (hours > 0) return `${hours}h ${String(minutes).padStart(2, "0")}m`
  if (minutes > 0) return `${minutes}m ${String(secs).padStart(2, "0")}s`
  return `${secs}s`
}

export function group(runtimes: RuntimeDto[]): WorkspaceGroup[] {
  const byRoot = new Map<string, RuntimeDto[]>()
  for (const runtime of runtimes) {
    const root = runtime.workspaceRoot || runtime.headScopeRoot || ""
    const bucket = byRoot.get(root.toLowerCase())
    if (bucket) bucket.push(runtime)
    else byRoot.set(root.toLowerCase(), [runtime])
  }

  return Array.from(byRoot.values()).map((rows) => {
    const processes = rows
      .filter((r) => r.isAlive && r.processId > 0)
      .sort((a, b) => a.startTime.localeCompare(b.startTime))
      .map((r) => ({
        pid: r.processId,
        startTime: r.startTime,
        transport: r.mcpTransport,
        owner: r.isDashboardOwner,
        ownerPort: r.dashboardPort ?? null,
        memoryBytes: r.memoryBytes ?? null,
        cpuPercent: r.cpuPercent ?? null,
      }))

    const memories = processes.map((p) => p.memoryBytes).filter((m): m is number => m !== null)
    const cpus = processes.map((p) => p.cpuPercent).filter((c): c is number => c !== null)
    const heartbeats = rows
      .map((r) => Date.parse(r.lastHeartbeat))
      .filter((t) => !Number.isNaN(t))

    const ownerPort = processes.find((p) => p.owner)?.ownerPort ?? null
    return {
      root: rows[0].workspaceRoot || rows[0].headScopeRoot,
      processes,
      running: processes.length > 0,
      totalMemoryBytes: memories.length > 0 ? memories.reduce((sum, m) => sum + m, 0) : null,
      totalCpuPercent: cpus.length > 0 ? Math.round(cpus.reduce((sum, c) => sum + c, 0) * 10) / 10 : null,
      lastHeartbeat: heartbeats.length > 0 ? new Date(Math.max(...heartbeats)).toISOString() : rows[0].lastHeartbeat,
      codebaseEnabled: rows[0].codebaseEnabled,
      transport: rows.find((r) => r.mcpEnabled)?.mcpTransport ?? null,
      ownerPort,
    }
  })
}
