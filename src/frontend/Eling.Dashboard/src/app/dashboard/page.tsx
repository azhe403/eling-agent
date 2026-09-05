import {
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbLink,
  BreadcrumbList,
  BreadcrumbPage,
  BreadcrumbSeparator,
} from "@/components/ui/breadcrumb"
import { Separator } from "@/components/ui/separator"
import { SidebarTrigger } from "@/components/ui/sidebar"

// Note: this page uses `output: "export"` in next.config.ts, so it is
// prerendered at build time. The stats below reflect the API state at
// `next build` time. For always-fresh stats, the data would need to
// live in a Client Component that fetches on the client.

type AggregatedMemory = {
  id: string
  type: string
  content: string
  status: string
}

type Stats = {
  total: number
  fact: number
  preference: number
  decision: number
  lesson: number
  note: number
}

const KNOWN_TYPES: (keyof Omit<Stats, "total">)[] = [
  "fact",
  "preference",
  "decision",
  "lesson",
  "note",
]

function computeStats(items: AggregatedMemory[]): Stats {
  const counts: Record<string, number> = {}
  for (const item of items) {
    const key = (item.type ?? "").toLowerCase()
    counts[key] = (counts[key] ?? 0) + 1
  }
  return {
    total: items.length,
    fact: counts["fact"] ?? 0,
    preference: counts["preference"] ?? 0,
    decision: counts["decision"] ?? 0,
    lesson: counts["lesson"] ?? 0,
    note: counts["note"] ?? 0,
  }
}

function StatCard({
  label,
  value,
}: {
  label: string
  value: number
}) {
  return (
    <div className="flex flex-col justify-between rounded-xl border bg-card p-5 text-card-foreground shadow-sm">
      <span className="text-sm font-medium text-muted-foreground">{label}</span>
      <span className="mt-2 text-3xl font-bold tracking-tight">{value}</span>
    </div>
  )
}

function StatsGrid({ stats }: { stats: Stats }) {
  // Show Total + 5 type cards = 6 cards; on few items still 6 cards (0s are meaningful)
  return (
    <div className="grid gap-4 md:grid-cols-3 lg:grid-cols-6">
      <StatCard label="Total" value={stats.total} />
      {KNOWN_TYPES.map((t) => (
        <StatCard
          key={t}
          label={t.charAt(0).toUpperCase() + t.slice(1)}
          value={stats[t]}
        />
      ))}
    </div>
  )
}

async function fetchAggregatedMemories(): Promise<{
  items: AggregatedMemory[] | null
  error: string | null
}> {
  const base = process.env.NEXT_PUBLIC_API_URL ?? ""
  const url = base
    ? `${base.replace(/\/$/, "")}/api/aggregated/memories`
    : "/api/aggregated/memories"

  try {
    const res = await fetch(url)
    if (!res.ok) {
      return { items: null, error: `API returned ${res.status}` }
    }
    const data: unknown = await res.json()
    if (!Array.isArray(data)) {
      return { items: null, error: "Unexpected response shape" }
    }
    // Normalize to the fields we need; tolerate casing variance.
    const items: AggregatedMemory[] = data.map((raw) => {
      const r = raw as Record<string, unknown>
      return {
        id: String(r["id"] ?? ""),
        type: String(r["type"] ?? r["Type"] ?? "note"),
        content: String(r["content"] ?? r["Content"] ?? ""),
        status: String(r["status"] ?? r["Status"] ?? "active"),
      }
    })
    return { items, error: null }
  } catch (err) {
    const msg = err instanceof Error ? err.message : "Fetch failed"
    return { items: null, error: msg }
  }
}

export default async function Page() {
  const { items, error } = await fetchAggregatedMemories()

  // Empty or error → friendly empty state; never crash.
  const showEmpty = error !== null || items === null || items.length === 0
  const stats: Stats | null =
    !showEmpty && items !== null ? computeStats(items) : null

  return (
    <>
      <header className="flex h-16 shrink-0 items-center gap-2 px-4">
        <SidebarTrigger className="-ml-1" />
        <Separator
          orientation="vertical"
          className="mr-2 data-vertical:h-4 data-vertical:self-auto"
        />
        <Breadcrumb>
          <BreadcrumbList>
            <BreadcrumbItem className="hidden md:block">
              <BreadcrumbLink href="#">Eling</BreadcrumbLink>
            </BreadcrumbItem>
            <BreadcrumbSeparator className="hidden md:block" />
            <BreadcrumbItem>
              <BreadcrumbPage>Dashboard</BreadcrumbPage>
            </BreadcrumbItem>
          </BreadcrumbList>
        </Breadcrumb>
      </header>

      <div className="flex flex-1 flex-col gap-4 p-4 pt-0">
        {showEmpty ? (
          <div className="flex flex-1 flex-col items-center justify-center rounded-xl border border-dashed bg-muted/30 px-6 py-16 text-center">
            <h2 className="text-lg font-semibold">No data yet</h2>
            <p className="mt-2 max-w-md text-sm text-muted-foreground">
              {error
                ? `Could not load stats from the aggregated memories endpoint (${error}). The backend may be offline or no memories have been saved yet.`
                : "No memories have been saved yet. Once you start saving memories they will appear here as stats by type."}
            </p>
          </div>
        ) : (
          <>
            <StatsGrid stats={stats!} />
            {/* Keep a placeholder area below stats so the page skeleton stays similar; lightweight so it doesn't look empty on data-present. */}
            <div className="flex min-h-[180px] flex-1 items-center justify-center rounded-xl bg-muted/30 p-6 text-sm text-muted-foreground">
              {stats!.total} memories across {KNOWN_TYPES.length} types.
            </div>
          </>
        )}
      </div>
    </>
  )
}
