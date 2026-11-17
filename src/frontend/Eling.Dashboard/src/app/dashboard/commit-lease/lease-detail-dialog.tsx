"use client"

import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import { workspaceName } from "@/components/workspaces/runtime-rows"
import { utcToLocal } from "@/lib/date-utils"
import { formatAge } from "./lease-format"

type LeaseDetailActive = {
  lease_id: string
  caller_id: string
  branch: string | null
  since: string
  last_heartbeat: string
  ttl_seconds: number
  paths: string[] | null
  intent: string | null
  checkout: string
}

type LeaseDetailReleased = {
  lease_id: string
  caller_id: string
  branch: string | null
  since: string
  released_at: string | null
  release_reason: string | null
  paths: string[] | null
  intent: string | null
  checkout: string
}

/**
 * The full detail of one lease, as a popup: the card keeps only the glanceable
 * facts (name, badges, checkout, since/age/heartbeat); everything else lives
 * here. Row values arrive preformatted where live ticking is needed — age and
 * TTL re-render on the parent's 1s ticker.
 */
export function LeaseDetailDialog({
  kind,
  lease,
  open,
  onOpenChange,
  ageSeconds,
  ttlRemainingSeconds,
}: {
  kind: "active" | "released"
  lease: LeaseDetailActive | LeaseDetailReleased
  open: boolean
  onOpenChange: (open: boolean) => void
  /** Live age in seconds (active leases only) — frame value + elapsed. */
  ageSeconds?: number
  /** Live TTL remaining in seconds (active leases only). */
  ttlRemainingSeconds?: number
}) {
  const paths = lease.paths ?? []
  const rows: Array<[string, string]> =
    kind === "active"
      ? [
          ["Lease ID", lease.lease_id],
          ["Caller ID", lease.caller_id],
          ["Workspace", lease.checkout],
          ["Branch", (lease as LeaseDetailActive).branch ?? "—"],
          ["Since", utcToLocal(lease.since) || lease.since],
          [
            "Last heartbeat",
            utcToLocal((lease as LeaseDetailActive).last_heartbeat) || (lease as LeaseDetailActive).last_heartbeat,
          ],
          ["Lease age", ageSeconds != null ? formatAge(ageSeconds) : "—"],
          [
            "TTL",
            ttlRemainingSeconds != null && ttlRemainingSeconds <= 0
              ? "0s · expired"
              : ttlRemainingSeconds != null
                ? `${formatAge(ttlRemainingSeconds)} remaining of ${(lease as LeaseDetailActive).ttl_seconds}s`
                : "—",
          ],
          ["Intent", lease.intent ?? "—"],
        ]
      : [
          ["Lease ID", lease.lease_id],
          ["Caller ID", lease.caller_id],
          ["Workspace", lease.checkout],
          ["Branch", (lease as LeaseDetailReleased).branch ?? "—"],
          ["Since", utcToLocal(lease.since) || lease.since],
          [
            "Released at",
            (lease as LeaseDetailReleased).released_at
              ? utcToLocal((lease as LeaseDetailReleased).released_at!) || (lease as LeaseDetailReleased).released_at!
              : "—",
          ],
          ["Release reason", (lease as LeaseDetailReleased).release_reason ?? "—"],
          ["Intent", lease.intent ?? "—"],
        ]

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-h-[85vh] w-[85vw] max-w-[1024px] overflow-y-auto sm:max-w-[1024px]">
        <DialogHeader>
          <DialogTitle className="break-words">{workspaceName(lease.checkout)}</DialogTitle>
          <DialogDescription className="break-all font-mono text-[11px]" title={lease.checkout}>
            {lease.checkout}
          </DialogDescription>
        </DialogHeader>
        <div className="flex flex-col gap-4 text-xs">
          <dl className="flex min-w-0 flex-col divide-y divide-border/60">
            {rows.map(([label, value]) => (
              <div key={label} className="flex items-baseline justify-between gap-4 py-1.5">
                <dt className="shrink-0 text-muted-foreground">{label}</dt>
                <dd
                  className={
                    label === "Intent"
                      ? "min-w-0 break-words text-right"
                      : "min-w-0 break-all text-right font-mono"
                  }
                  title={value}
                >
                  {label === "TTL" && value === "0s · expired" ? (
                    <span className="font-sans text-amber-600 dark:text-amber-400">{value}</span>
                  ) : (
                    value
                  )}
                </dd>
              </div>
            ))}
          </dl>
          <div className="min-w-0 border-t border-border/60 pt-4">
            <div className="text-muted-foreground">Paths</div>
            {paths.length === 0 ? (
              <div className="mt-1.5 text-muted-foreground">—</div>
            ) : (
              <ul className="mt-1.5 flex flex-col gap-1 font-mono">
                {paths.map((p, i) => (
                  <li key={`${i}-${p}`} className="break-all" title={p}>
                    {p}
                  </li>
                ))}
              </ul>
            )}
          </div>
        </div>
      </DialogContent>
    </Dialog>
  )
}
