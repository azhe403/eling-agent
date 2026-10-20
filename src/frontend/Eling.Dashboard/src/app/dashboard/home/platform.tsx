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
import type { SseStatus } from "@/hooks/use-memories-sse"
import type { DashboardData, JudgeView } from "./data"

/**
 * The state of the platform itself.
 *
 * Everything else on the dashboard is content; this is the part that tells
 * the reader whether the content is current. A live stream that is not
 * connected, or a judge that is switched on without a provider, is the kind
 * of thing that looks fine on a page and quietly breaks the thing behind it.
 */
export function PlatformStatus({
  data,
  streamStatus,
}: {
  data: DashboardData
  streamStatus: SseStatus
}) {
  const tools = data.tools.value ?? []
  const disabled = tools.filter((tool) => !tool.enabled).length

  return (
    <Card>
      <CardHeader>
        <CardTitle>Platform</CardTitle>
        <CardAction>
          <Link
            href="/dashboard/tools"
            className="inline-flex items-center gap-1 text-xs font-medium text-muted-foreground transition-colors hover:text-foreground"
          >
            Tools
            <ArrowUpRight className="size-3" />
          </Link>
        </CardAction>
      </CardHeader>
      <CardContent>
        <dl className="flex flex-col divide-y divide-border/60">
          <StatusRow
            label="Backend"
            value={backendValue(data.health.error, data.health.value?.pid)}
            state={backendState(data.health.error, data.health.value !== null)}
          />
          <StatusRow
            label="Live stream"
            value={streamLabel(streamStatus)}
            state={
              streamStatus === "connected"
                ? "ok"
                : streamStatus === "connecting"
                  ? "warn"
                  : "bad"
            }
          />
          <StatusRow
            label="Semantic judge"
            value={judgeValue(data.judge.error, data.judge.value)}
            state={judgeState(data.judge.error, data.judge.value)}
          />
          <StatusRow
            label="MCP tools"
            value={
              data.tools.error
                ? data.tools.error
                : `${tools.length - disabled} enabled${disabled ? ` · ${disabled} disabled` : ""}`
            }
            state={data.tools.error ? "bad" : disabled ? "warn" : "ok"}
          />
        </dl>
      </CardContent>
    </Card>
  )
}

type StatusState = "ok" | "warn" | "bad"

function StatusRow({
  label,
  value,
  state,
}: {
  label: string
  value: string
  state: StatusState
}) {
  return (
    <div className="flex items-center justify-between gap-3 py-2.5 first:pt-0 last:pb-0">
      <dt className="text-sm text-muted-foreground">{label}</dt>
      <dd className="flex min-w-0 items-center gap-2 text-sm">
        <span
          className={`size-1.5 shrink-0 rounded-full ${
            state === "ok"
              ? "bg-green-500"
              : state === "warn"
                ? "bg-amber-500"
                : "bg-red-500"
          }`}
        />
        <span className="truncate" title={value}>
          {value}
        </span>
      </dd>
    </div>
  )
}

function backendValue(error: string | null, pid: number | undefined): string {
  if (error) {
    return isMissingRoute(error) ? "route missing - restart the backend" : error
  }
  if (pid === undefined) return "unreachable"
  return `healthy · pid ${pid}`
}

/**
 * A route the backend does not have is not the same as a backend that is
 * down: an older backend answers an unknown route with the dashboard's own
 * HTML shell, so the dashboard is talking to a live process that simply does
 * not know the new route yet. That is a warning - restart the backend - and
 * not a red failure.
 */
function isMissingRoute(error: string): boolean {
  return error.includes("not JSON") || error.includes("HTTP 404")
}

function backendState(error: string | null, answered: boolean): StatusState {
  if (answered) return "ok"
  if (error && isMissingRoute(error)) return "warn"
  return "bad"
}

function streamLabel(status: SseStatus): string {
  switch (status) {
    case "connected":
      return "connected"
    case "connecting":
      return "reconnecting"
    default:
      return "disconnected"
  }
}

/**
 * A judge that is switched on but has no provider would silently degrade
 * every save into a heuristic guess, so that state is worth naming rather
 * than collapsing into "enabled".
 */
function judgeValue(error: string | null, judge: JudgeView | null): string {
  if (error) return error
  if (!judge) return "unknown"
  if (!judge.enabled) return "off"
  if (!judge.isConfigured) return "on · no provider"
  return judge.model ? `on · ${judge.model}` : "on"
}

function judgeState(error: string | null, judge: JudgeView | null): StatusState {
  if (error || !judge) return "bad"
  if (!judge.enabled) return "warn"
  return judge.isConfigured ? "ok" : "warn"
}
