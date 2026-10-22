"use client"

import Link from "next/link"
import { BookOpen, Brain, Cpu, Database, FolderGit2, MemoryStick } from "lucide-react"

import { Card } from "@/components/ui/card"
import { relativeTime } from "@/lib/date-utils"
import { formatBytes } from "@/lib/format"
import type { DashboardData, WorkspaceSummary } from "./data"

/**
 * The vitals row: six numbers that answer "how much of each thing is there",
 * each one a link into the page that owns the detail.
 *
 * The tiles double as the page's navigation. A dashboard that only reports
 * numbers makes the reader hunt for the page that explains them; a tile that
 * opens the page does both in one click.
 */
export function VitalsRow({ data }: { data: DashboardData }) {
  const stats = data.stats
  const memories = stats.value?.total ?? null

  const workspaces = data.workspaces.value ?? []
  const running = workspaces.filter((w) => w.running).length

  const files = workspaces.reduce((sum, w) => sum + w.files, 0)
  const chunks = workspaces.reduce((sum, w) => sum + w.chunks, 0)
  const lastIndexed = newestIndexDate(workspaces)

  const connections = data.connections.value ?? []
  const readOnly = connections.filter((c) => c.environment === "ReadOnly").length

  const resources = resourceUse(workspaces)

  return (
    <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
      <Vital
        href="/dashboard/memories"
        icon={<Brain className="size-4" />}
        label="Memories"
        value={memories === null ? "—" : String(memories)}
        detail={
          stats.error
            ? stats.error
            : memories === 0
              ? "Nothing saved yet"
              : typeBreakdown(stats.value?.byType)
        }
      />
      <Vital
        href="/dashboard/workspaces"
        icon={<FolderGit2 className="size-4" />}
        label="Workspaces"
        value={data.workspaces.value === null ? "—" : String(running)}
        detail={
          data.workspaces.error
            ? data.workspaces.error
            : workspaces.length === running
              ? "all running"
              : `${running} running · ${workspaces.length - running} offline`
        }
      />
      <Vital
        href="/dashboard/codebase"
        icon={<BookOpen className="size-4" />}
        label="Indexed"
        value={data.workspaces.value === null ? "—" : files.toLocaleString()}
        detail={
          data.workspaces.error
            ? data.workspaces.error
            : files === 0
              ? "No index built yet"
              : `${chunks.toLocaleString()} chunks · ${lastIndexed}`
        }
      />
      <Vital
        href="/dashboard/databases"
        icon={<Database className="size-4" />}
        label="Connections"
        value={data.connections.value === null ? "—" : String(connections.length)}
        detail={
          data.connections.error
            ? data.connections.error
            : connections.length === 0
              ? "None registered"
              : `${readOnly} read-only · ${connections.length - readOnly} writable`
        }
      />
      <Vital
        href="/dashboard/workspaces"
        icon={<Cpu className="size-4" />}
        label="CPU"
        value={resources.cpuPercent === null ? "—" : `${resources.cpuPercent}%`}
        detail={resourceDetail(resources)}
        hint="Total CPU used by every live Eling process, as a share of the machine's cores."
      />
      <Vital
        href="/dashboard/workspaces"
        icon={<MemoryStick className="size-4" />}
        label="Memory"
        value={resources.memoryBytes === null ? "—" : formatBytes(resources.memoryBytes)}
        detail={resourceDetail(resources)}
        hint="Total working set of every live Eling process."
      />
    </div>
  )
}

/**
 * What the running Eling processes cost the machine, summed over every live
 * process in every workspace.
 *
 * These are the same numbers the Workspaces page shows per workspace, rolled
 * up - the dashboard's own rule is that one folder can host several sessions,
 * so a single representative process is never the answer. CPU is already a
 * share of the machine's cores (100% means every core busy), so summing the
 * processes gives a share of the whole machine, not of one core.
 */
type ResourceUse = {
  cpuPercent: number | null
  memoryBytes: number | null
  processCount: number
  workspaceCount: number
}

function resourceUse(workspaces: WorkspaceSummary[]): ResourceUse {
  let cpu = 0
  let memory = 0
  let processCount = 0
  let workspaceCount = 0

  for (const workspace of workspaces) {
    // An offline workspace keeps its index on disk but has no process left
    // to charge, so it contributes nothing here.
    if (workspace.processCount === 0) continue

    workspaceCount += 1
    processCount += workspace.processCount
    if (workspace.cpuPercent !== null) cpu += workspace.cpuPercent
    if (workspace.memoryBytes !== null) memory += workspace.memoryBytes
  }

  return {
    cpuPercent: processCount > 0 ? Math.round(cpu * 10) / 10 : null,
    memoryBytes: processCount > 0 ? memory : null,
    processCount,
    workspaceCount,
  }
}

/** What the two resource tiles share: how many processes the number covers. */
function resourceDetail(use: ResourceUse): string {
  if (use.processCount === 0) return "no live processes"
  const processes = `${use.processCount} live process${use.processCount === 1 ? "" : "es"}`
  const folders = `${use.workspaceCount} workspace${use.workspaceCount === 1 ? "" : "s"}`
  return `across ${processes} · ${folders}`
}

function Vital({
  href,
  icon,
  label,
  value,
  detail,
  hint,
}: {
  href: string
  icon: React.ReactNode
  label: string
  value: string
  detail: string
  /** The full sentence behind the short line, for the tooltip and the label. */
  hint?: string
}) {
  return (
    <Link
      href={href}
      className="group rounded-xl focus-visible:outline-none"
      aria-label={`${label}: ${value}. ${hint ?? detail}`}
    >
      <Card className="h-full transition-colors group-hover:bg-accent/40">
        <div className="flex items-start justify-between gap-2 px-(--card-spacing)">
          <span className="text-sm font-medium text-muted-foreground">
            {label}
          </span>
          <span className="text-muted-foreground transition-colors group-hover:text-foreground">
            {icon}
          </span>
        </div>
        <div className="px-(--card-spacing)">
          <span className="text-3xl font-bold tracking-tight tabular-nums">
            {value}
          </span>
          <p className="mt-1 truncate text-xs text-muted-foreground" title={hint ?? detail}>
            {detail}
          </p>
        </div>
      </Card>
    </Link>
  )
}

/**
 * The three biggest memory types, in one line: "12 fact · 5 decision · 2 note".
 * The full breakdown lives on the memories page; this is the shape of the
 * corpus, not its content.
 */
function typeBreakdown(byType: Record<string, number> | undefined): string {
  if (!byType) return ""
  const entries = Object.entries(byType)
    .filter(([, count]) => count > 0)
    .sort((a, b) => b[1] - a[1])
    .slice(0, 3)
  if (entries.length === 0) return "Nothing saved yet"
  return entries
    .map(([type, count]) => `${count} ${type.toLowerCase()}`)
    .join(" · ")
}

/** When the most recently rebuilt index was written. */
function newestIndexDate(
  workspaces: { lastIndexedAt: string | null }[]
): string {
  let newest = 0
  for (const workspace of workspaces) {
    if (!workspace.lastIndexedAt) continue
    const at = Date.parse(workspace.lastIndexedAt)
    if (!Number.isNaN(at) && at > newest) newest = at
  }
  if (newest === 0) return "never indexed"
  return `indexed ${relativeTime(new Date(newest).toISOString())}`
}
