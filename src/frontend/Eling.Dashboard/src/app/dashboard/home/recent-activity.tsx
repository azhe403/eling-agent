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
import { FeedEmpty, FeedError } from "./recent-memories"
import type { AuditEvent, Section } from "./data"

/**
 * What the platform changed in the last day.
 *
 * The memory feed above shows what was learned; this shows who did it. An
 * agent saving a memory is a change like any other, and the audit trail is
 * where it is recorded - so the dashboard surfaces the same trail the Audit
 * Trail page shows, just the recent tail of it.
 */
export function RecentActivity({ section }: { section: Section<AuditEvent[]> }) {
  const events = section.value ?? []

  return (
    <Card>
      <CardHeader>
        <CardTitle>Recent activity</CardTitle>
        <CardAction>
          <Link
            href="/dashboard/audit-trail"
            className="inline-flex items-center gap-1 text-xs font-medium text-muted-foreground transition-colors hover:text-foreground"
          >
            Audit trail
            <ArrowUpRight className="size-3" />
          </Link>
        </CardAction>
      </CardHeader>
      <CardContent>
        {section.error ? (
          <FeedError message={section.error} />
        ) : events.length === 0 ? (
          <FeedEmpty
            title="No changes in the last 24 hours"
            hint="Saves, updates and deletions are recorded here as they happen."
          />
        ) : (
          <ul className="flex flex-col divide-y divide-border/60">
            {events.map((event, index) => (
              <ActivityRow key={`${event.timestamp}-${event.action}-${index}`} event={event} />
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  )
}

function ActivityRow({ event }: { event: AuditEvent }) {
  return (
    <li className="flex items-center gap-3 py-2.5 first:pt-0 last:pb-0">
      <span
        className={`size-1.5 shrink-0 rounded-full ${outcomeDot(event.outcome)}`}
        title={event.outcome}
      />
      <span className="font-mono text-xs font-medium">{event.action}</span>
      <span className="truncate text-xs text-muted-foreground">
        {event.actor}
        {event.scope ? ` · ${event.scope}` : ""}
      </span>
      <span className="ml-auto shrink-0 text-xs text-muted-foreground">
        {relativeTime(event.timestamp)}
      </span>
    </li>
  )
}

function outcomeDot(outcome: string): string {
  switch (outcome) {
    case "success":
      return "bg-green-500"
    case "error":
      return "bg-red-500"
    case "denied":
      return "bg-amber-500"
    default:
      return "bg-muted-foreground/40"
  }
}
