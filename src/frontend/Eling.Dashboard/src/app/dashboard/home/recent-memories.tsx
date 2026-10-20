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
import { statusBadge, typeBadge } from "@/lib/types"
import type { Section, MemoryRow } from "./data"

/**
 * The memories the platform learned most recently.
 *
 * This is the feed the dashboard is really about: Eling exists to remember
 * things, so the first thing worth showing is what was remembered. Every row
 * is scoped - a memory without its scope is ambiguous, because the same
 * content can live in the global scope, in a shared workspace scope, or in a
 * machine-only local scope.
 */
export function RecentMemories({ section }: { section: Section<MemoryRow[]> }) {
  const memories = section.value ?? []

  return (
    <Card>
      <CardHeader>
        <CardTitle>Recent memories</CardTitle>
        <CardAction>
          <Link
            href="/dashboard/memories"
            className="inline-flex items-center gap-1 text-xs font-medium text-muted-foreground transition-colors hover:text-foreground"
          >
            All memories
            <ArrowUpRight className="size-3" />
          </Link>
        </CardAction>
      </CardHeader>
      <CardContent>
        {section.error ? (
          <FeedError message={section.error} />
        ) : memories.length === 0 ? (
          <FeedEmpty
            title="No memories yet"
            hint="Anything an agent saves shows up here as it arrives."
          />
        ) : (
          <ul className="flex flex-col divide-y divide-border/60">
            {memories.map((memory) => (
              <MemoryRowItem key={memory.id} memory={memory} />
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  )
}

function MemoryRowItem({ memory }: { memory: MemoryRow }) {
  return (
    <li className="flex flex-col gap-1.5 py-3 first:pt-0 last:pb-0">
      <div className="flex flex-wrap items-center gap-1.5 text-xs">
        <span
          className={`rounded-md px-2 py-0.5 font-medium ${typeBadge[memory.type] ?? typeBadge.Note}`}
        >
          {memory.type}
        </span>
        {memory.status !== "Active" && (
          <span
            className={`rounded-md px-2 py-0.5 font-medium ${statusBadge[memory.status] ?? statusBadge.Archived}`}
          >
            {memory.status}
          </span>
        )}
        <ScopeBadge memory={memory} />
        <span className="ml-auto shrink-0 text-muted-foreground">
          {relativeTime(memory.createdAt)}
        </span>
      </div>
      <p className="line-clamp-2 text-sm leading-relaxed break-words">
        {memory.content}
      </p>
    </li>
  )
}

/**
 * Where the memory lives. The scope badges match the ones on the memories
 * page, so a row read here and the same memory read there look the same.
 */
function ScopeBadge({ memory }: { memory: MemoryRow }) {
  if (memory.scope === "global") {
    return (
      <span className="rounded-md bg-blue-500/10 px-2 py-0.5 font-medium text-blue-600">
        🌐 Global
      </span>
    )
  }

  return (
    <>
      <span
        className="rounded-md bg-amber-500/10 px-2 py-0.5 font-medium text-amber-600"
        title={memory.project?.root}
      >
        📁 {memory.project?.id ?? "Workspace"}
      </span>
      {memory.scope === "project-local" && (
        <span className="rounded-md bg-violet-500/10 px-2 py-0.5 font-medium text-violet-600">
          💻 Local
        </span>
      )}
    </>
  )
}

export function FeedError({ message }: { message: string }) {
  return (
    <p className="rounded-lg border border-dashed px-4 py-6 text-center text-sm text-muted-foreground">
      Could not load this feed ({message}).
    </p>
  )
}

export function FeedEmpty({ title, hint }: { title: string; hint: string }) {
  return (
    <div className="rounded-lg border border-dashed px-4 py-8 text-center">
      <p className="text-sm font-medium">{title}</p>
      <p className="mt-1 text-sm text-muted-foreground">{hint}</p>
    </div>
  )
}
