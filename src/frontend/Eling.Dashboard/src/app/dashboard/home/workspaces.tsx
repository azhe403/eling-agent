"use client"

import Link from "next/link"
import { ArrowUpRight } from "lucide-react"

import {
  Card,
  CardAction,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
import { relativeTime } from "@/lib/date-utils"
import { formatMegabytes } from "@/lib/format"
import { FeedEmpty, FeedError } from "./recent-memories"
import type { Section, WorkspaceSummary } from "./data"

/** How many workspaces the card lists before it stops and links out. */
const SHOWN = 6

/**
 * The workspaces this machine is working in, with the size of each one's
 * codebase index next to it.
 *
 * The metrics are the workspace's totals, not one process's: a folder can
 * host several sessions at once, and showing a single arbitrary one was the
 * mistake the Workspaces page already fixed. A workspace that is offline
 * stays listed, because its index is still on disk and still searchable.
 */
export function WorkspaceList({ section }: { section: Section<WorkspaceSummary[]> }) {
  const workspaces = section.value ?? []
  const shown = workspaces.slice(0, SHOWN)

  return (
    <Card>
      <CardHeader>
        <CardTitle>Workspaces</CardTitle>
        <CardAction>
          <Link
            href="/dashboard/workspaces"
            className="inline-flex items-center gap-1 text-xs font-medium text-muted-foreground transition-colors hover:text-foreground"
          >
            All workspaces
            <ArrowUpRight className="size-3" />
          </Link>
        </CardAction>
      </CardHeader>
      <CardContent>
        {section.error ? (
          <FeedError message={section.error} />
        ) : shown.length === 0 ? (
          <FeedEmpty
            title="No workspaces yet"
            hint="A workspace appears once an agent session runs in a folder."
          />
        ) : (
          <ul className="flex flex-col divide-y divide-border/60">
            {shown.map((workspace) => (
              <WorkspaceRow key={workspace.root} workspace={workspace} />
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  )
}

function WorkspaceRow({ workspace }: { workspace: WorkspaceSummary }) {
  const detail = workspaceDetail(workspace)
  return (
    <li className="flex items-center gap-3 py-2.5 first:pt-0 last:pb-0">
      <span
        className={`size-1.5 shrink-0 rounded-full ${
          workspace.running ? "bg-green-500" : "bg-muted-foreground/40"
        }`}
        title={workspace.running ? "running" : "offline"}
      />
      <div className="flex min-w-0 flex-col">
        <span className="truncate text-sm font-medium" title={workspace.root}>
          {workspace.name}
        </span>
        <span className="truncate text-xs text-muted-foreground" title={detail}>
          {detail}
        </span>
      </div>
      <span className="ml-auto shrink-0 text-xs text-muted-foreground tabular-nums">
        {metricsOf(workspace)}
      </span>
    </li>
  )
}

function workspaceDetail(workspace: WorkspaceSummary): string {
  const processes =
    workspace.processCount === 1
      ? "1 process"
      : `${workspace.processCount} processes`
  const indexed =
    workspace.files === 0
      ? "not indexed"
      : `${workspace.files.toLocaleString()} files · ${workspace.chunks.toLocaleString()} chunks`
  const when = workspace.lastIndexedAt
    ? `indexed ${relativeTime(workspace.lastIndexedAt)}`
    : null
  return [processes, indexed, when].filter(Boolean).join(" · ")
}

function metricsOf(workspace: WorkspaceSummary): string {
  const parts: string[] = []
  if (workspace.cpuPercent !== null) parts.push(`${workspace.cpuPercent}%`)
  if (workspace.memoryBytes !== null) parts.push(formatMegabytes(workspace.memoryBytes))
  return parts.join(" · ")
}
