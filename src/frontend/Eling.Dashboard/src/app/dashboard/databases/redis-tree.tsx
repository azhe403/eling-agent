"use client"

import { Button } from "@/components/ui/button"

import { RedisPage, redisPageKey } from "./redis-browser"

/**
 * The Redis key tree, as it is drawn.
 *
 * Rendering lives here rather than in the page because the page is already the
 * explorer for every connection, and the tree is one subject with three parts:
 * a prefix row, a key row, and the recursion that puts them together.
 */

/**
 * A compact TTL. A raw `1113893s` says nothing at a glance and eats the row;
 * `13d` fits next to the key.
 */
export const formatTtl = (seconds: number): string => {
  if (seconds <= 0) return "no ttl"
  if (seconds < 60) return `${seconds}s`
  if (seconds < 3600) return `${Math.round(seconds / 60)}m`
  if (seconds < 86400) return `${Math.round(seconds / 3600)}h`
  return `${Math.round(seconds / 86400)}d`
}

/**
 * One key row, shared by the root list and every prefix level. They used to be
 * three copies that drifted apart: the root list kept the old layout long
 * after the nested ones were fixed.
 */
export function RedisKeyRow({
  item,
  active,
  onSelect,
}: {
  item: { key: string; type: string; ttlSeconds: number }
  active: boolean
  onSelect: (key: string) => void
}) {
  return (
    <Button
      variant="ghost"
      size="sm"
      title={item.key}
      className={`flex w-full items-center justify-start gap-1.5 rounded px-1.5 py-1 text-left hover:bg-accent ${active ? "border border-primary" : ""}`}
      onClick={() => onSelect(item.key)}
    >
      <span className="shrink-0 text-xs">🔑</span>
      <span className="min-w-0 flex-1 truncate font-mono text-xs">{item.key}</span>
      <span className="shrink-0 pl-2 text-xs tabular-nums text-muted-foreground">
        {item.type} · {formatTtl(item.ttlSeconds)}
      </span>
    </Button>
  )
}

/**
 * A prefix node. The full prefix lives in the tooltip: the segment alone is
 * what changes per level, and printing both repeated the same text
 * ("references references:").
 */
export function RedisNodeRow({
  item,
  expanded,
  onToggle,
}: {
  item: { segment: string; prefix: string }
  expanded: boolean
  onToggle: () => void
}) {
  return (
    <Button
      variant="ghost"
      size="sm"
      title={item.prefix}
      className="flex w-full items-center justify-start gap-1.5 rounded px-1.5 py-1 text-left hover:bg-accent"
      onClick={onToggle}
    >
      <span className="shrink-0 text-xs">{expanded ? "▾" : "▸"}</span>
      <span className="min-w-0 flex-1 truncate text-xs font-medium">📁 {item.segment}</span>
    </Button>
  )
}

/**
 * Renders the tree recursively: nodes expand into their own children, which
 * load lazily on first expand. Each level adds one indent.
 *
 * The page cache is keyed `connId:db:prefix`, so one tree knows which
 * connection and database it is drawing and two of either cannot share a
 * level.
 */
export function RedisPrefixTree({
  connId,
  db,
  prefix,
  pages,
  expanded,
  inspectKey,
  onToggle,
  onSelectKey,
}: {
  connId: string
  db: number
  prefix: string
  pages: Record<string, RedisPage>
  expanded: Set<string>
  inspectKey: string
  onToggle: (prefix: string) => void
  onSelectKey: (key: string) => void
}) {
  const page = pages[redisPageKey(connId, db, prefix)]
  if (!page) return <p className="px-2 text-xs text-muted-foreground">Loading…</p>
  return (
    <div className="space-y-0.5">
      {page.nodes.map((n) => {
        const isExpanded = expanded.has(redisPageKey(connId, db, n.prefix))
        return (
          <div key={n.prefix} className="space-y-0.5">
            <RedisNodeRow item={n} expanded={isExpanded} onToggle={() => onToggle(n.prefix)} />
            {isExpanded ? (
              <div className="ml-2 space-y-0.5 border-l pl-1.5">
                <RedisPrefixTree
                  connId={connId}
                  db={db}
                  prefix={n.prefix}
                  pages={pages}
                  expanded={expanded}
                  inspectKey={inspectKey}
                  onToggle={onToggle}
                  onSelectKey={onSelectKey}
                />
              </div>
            ) : null}
          </div>
        )
      })}
      {page.keys.map((k) => (
        <RedisKeyRow
          key={k.key}
          item={k}
          active={inspectKey === k.key}
          onSelect={onSelectKey}
        />
      ))}
    </div>
  )
}
