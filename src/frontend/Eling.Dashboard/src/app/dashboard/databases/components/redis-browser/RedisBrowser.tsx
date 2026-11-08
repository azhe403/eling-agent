import { Card, CardHeader, CardTitle, CardDescription, CardContent } from "@/components/ui/card"
import { Input } from "@/components/ui/input"
import { Button } from "@/components/ui/button"
import { Dialog, DialogContent, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { X } from "lucide-react"
import { useMemo } from "react"
import { DbConn, DbTab, RedisPage } from "@/app/dashboard/databases/types"
import { RedisPrefixTree, formatTtl } from "@/app/dashboard/databases/redis-tree"
import { redisPageKey, redisPageScope } from "@/app/dashboard/databases/redis-browser"

/** The value a key holds, as the inspector reads it. */
export type RedisInspect = {
  key: string
  type: string
  ttlSeconds: number
  expiresAt: string | null
  length: number
  encoding: string | null
  preview: {
    kind: string
    entries: Array<{ field: string | null; value: string | null }>
    truncated: boolean
  }
}

export interface RedisBrowserProps {
  activeTable: DbTab | null
  activeRedisDb: number
  redisPrefix: string
  redisRootPrefix: string
  redisPages: Record<string, RedisPage>
  redisExpanded: Set<string>
  redisInspectKey: string
  redisLoading: boolean
  redisInspect: RedisInspect | null
  redisInspectOpen: boolean
  conns: DbConn[]
  isWide: boolean
  onPrefixChange: (prefix: string) => void
  onBrowsePrefix: (connId: string, db: number, prefix: string) => void
  onRootPrefix: (connId: string, db: number) => void
  onTogglePrefix: (connId: string, prefix: string) => void
  onSelectKey: (key: string) => void
  onLoadMore: (connId: string, db: number, prefix: string) => void
  onCollapseAll: (connId: string, db: number) => void
  onCloseInspect: () => void
  onInspectOpenChange: (open: boolean) => void
}

export const RedisBrowser = ({
  activeTable,
  activeRedisDb,
  redisPrefix,
  redisRootPrefix,
  redisPages,
  redisExpanded,
  redisInspectKey,
  redisLoading,
  redisInspect,
  redisInspectOpen,
  conns,
  isWide,
  onPrefixChange,
  onBrowsePrefix,
  onRootPrefix,
  onTogglePrefix,
  onSelectKey,
  onLoadMore,
  onCollapseAll,
  onCloseInspect,
  onInspectOpenChange,
}: RedisBrowserProps) => {
  const connId = activeTable?.connId ?? ""
  const rootKey = redisPageKey(connId, activeRedisDb, redisRootPrefix)
  const activePage = redisPages[rootKey]

  /**
   * The counts of the pages in view. A root prefix hides the rest of the
   * database, so a count of what is off-screen would read as if the filter
   * did nothing.
   */
  const counts = useMemo(() => {
    const scope = redisPageScope(connId, activeRedisDb)
    const dbPages = Object.entries(redisPages)
      .filter(([key]) => key.startsWith(scope))
      .map(([, page]) => page)
      .filter((page) => redisRootPrefix === "" || page.prefix.startsWith(redisRootPrefix))
    if (dbPages.length === 0) return null
    const prefixes = dbPages.reduce((sum, p) => sum + p.nodes.length, 0)
    const keys = dbPages.reduce((sum, p) => sum + p.keys.length, 0)
    const scanned = activePage ? ` · scanned ${activePage.scanned} so far` : ""
    const continued = activePage && !activePage.complete ? " (more)" : ""
    const label =
      redisRootPrefix === "" ? `${prefixes} prefixes · ${keys} keys (this level)` : `${prefixes} prefixes · ${keys} keys under ${redisRootPrefix}`
    return `${label}${continued}${scanned}`
  }, [redisPages, connId, activeRedisDb, redisRootPrefix, activePage])

  /**
   * The preview of the inspected key. Shared between the docked column and
   * the popup used on narrower screens, so both show exactly the same preview.
   *
   * Stacked field/value rows instead of a table: the docked column is 360px
   * and a hash field can be a long JSON blob, so table columns forced a
   * horizontal scrollbar and clipped the value mid-word.
   */
  const inspectBody = redisInspect ? (
    <>
      {redisInspect.preview.entries.length > 0 ? (
        <div className="space-y-1">
          {redisInspect.preview.entries.map((entry, i) => (
            <div key={`${entry.field ?? "item"}-${i}`} className="rounded border px-2 py-1">
              {entry.field !== null ? (
                <p className="break-all font-mono text-[11px] text-muted-foreground">{entry.field}</p>
              ) : null}
              <p className="whitespace-pre-wrap break-all text-xs">{entry.value ?? "null"}</p>
            </div>
          ))}
        </div>
      ) : (
        <p className="text-sm text-muted-foreground">No preview entries.</p>
      )}
      {redisInspect.preview.truncated ? <p className="text-xs text-amber-600">Preview truncated.</p> : null}
    </>
  ) : null

  const inspectMeta = redisInspect ? `${redisInspect.type} · len ${redisInspect.length} · ${formatTtl(redisInspect.ttlSeconds)}` : ""

  return (
    <Card className="flex-1 min-h-0">
      <CardHeader>
        <CardTitle>
          Redis · {conns.find((c) => c.id === activeTable?.connId)?.name ?? ""} · db{activeRedisDb}
        </CardTitle>
        <CardDescription>
          Browse keys for logical database {activeRedisDb}. Click a key to inspect its value.
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-1 flex-col min-h-0 gap-3">
        <div className="flex shrink-0 flex-wrap items-center gap-2">
          <Input
            className="flex-1"
            placeholder="prefix — Enter to browse under it"
            value={redisPrefix}
            onChange={(e) => onPrefixChange(e.target.value)}
            onKeyDown={(e) => {
              if (e.key !== "Enter") return
              if (!connId) return
              onBrowsePrefix(connId, activeRedisDb, redisPrefix)
            }}
            disabled={!connId}
          />
          <Button
            variant="outline"
            onClick={() => {
              if (!connId) return
              onBrowsePrefix(connId, activeRedisDb, redisPrefix)
            }}
            disabled={redisLoading || !connId}
          >
            Browse
          </Button>
          <Button
            variant="ghost"
            onClick={() => {
              if (!connId) return
              onRootPrefix(connId, activeRedisDb)
            }}
            disabled={redisLoading || !connId}
          >
            {redisLoading ? "Browsing…" : "Root"}
          </Button>
        </div>

        <div className={`grid min-h-0 flex-1 grid-rows-[1fr] gap-2 ${isWide && redisInspect ? "xl:grid-cols-[360px_minmax(0,1fr)]" : ""}`}>
          <div className="flex min-h-0 min-w-0 flex-col overflow-hidden rounded-md border p-3">
            <div className="flex shrink-0 items-center justify-between gap-2">
              <p className="text-xs text-muted-foreground">{counts ?? "No Redis key tree loaded yet."}</p>
              <div className="flex shrink-0 items-center gap-2">
                {activePage && !activePage.complete ? (
                  <Button
                    variant="outline"
                    size="sm"
                    onClick={() => connId && onLoadMore(connId, activeRedisDb, redisRootPrefix)}
                    disabled={redisLoading || !connId}
                  >
                    {redisLoading ? "Loading…" : "Load more"}
                  </Button>
                ) : null}
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => onCollapseAll(connId, activeRedisDb)}
                  disabled={!connId}
                >
                  Collapse all
                </Button>
              </div>
            </div>
            <div className="min-h-0 flex-1 overflow-y-auto">
              {redisPages[rootKey] ? (
                <RedisPrefixTree
                  connId={connId}
                  db={activeRedisDb}
                  prefix={redisRootPrefix}
                  pages={redisPages}
                  expanded={redisExpanded}
                  inspectKey={redisInspectKey}
                  onToggle={(prefix) => connId && onTogglePrefix(connId, prefix)}
                  onSelectKey={(key) => onSelectKey(key)}
                />
              ) : (
                <p className="text-sm text-muted-foreground">
                  {redisRootPrefix === "" ? "Press Root to load the key tree." : `Nothing loaded under ${redisRootPrefix} — press Browse.`}
                </p>
              )}
            </div>
          </div>

          {isWide && redisInspect ? (
            <div className="flex min-h-0 flex-col rounded-md border p-3">
              <div className="flex shrink-0 items-start justify-between gap-2">
                <p className="min-w-0 break-all font-mono text-xs font-medium">{redisInspect.key}</p>
                <Button
                  variant="ghost"
                  size="sm"
                  className="size-6 shrink-0 p-0"
                  aria-label="Close key inspector"
                  onClick={onCloseInspect}
                >
                  <X className="size-3.5" />
                </Button>
              </div>
              <p className="shrink-0 text-xs text-muted-foreground">{inspectMeta}</p>
              <div className="min-h-0 flex-1 overflow-y-auto">{inspectBody}</div>
            </div>
          ) : null}
        </div>

        {!isWide && redisInspectOpen && redisInspect !== null ? (
          <Dialog open onOpenChange={(o) => { if (!o) onInspectOpenChange(false) }}>
            <DialogContent className="max-h-[85dvh] overflow-y-auto sm:max-w-2xl">
              <DialogHeader>
                <DialogTitle className="break-all font-mono text-sm">{redisInspect.key}</DialogTitle>
              </DialogHeader>
              <p className="text-xs text-muted-foreground">{inspectMeta}</p>
              {inspectBody}
            </DialogContent>
          </Dialog>
        ) : null}
      </CardContent>
    </Card>
  )
}
