import { Card, CardHeader, CardTitle, CardContent } from "@/components/ui/card"
import { Button } from "@/components/ui/button"
import { Loader2, ArrowUp, ArrowDown } from "lucide-react"
import { DbConn, DbTab, SchemaInfo, RedisDbInfo } from "@/app/dashboard/databases/types"
import { isRedisTab } from "@/app/dashboard/databases/db-tabs"
import { CONN_SORT_KEYS, CONN_SORT_COPY, type ConnSortKey, type ConnSortOrder } from "@/app/dashboard/databases/conn-sorting"
import { dbRowLabel, dbRowCount, dbRowAsOf, visibleRedisDbs } from "@/app/dashboard/databases/redis-browser"

export interface ConnectionTreeProps {
  conns: DbConn[]
  expandedConns: Set<string>
  expandedDbs: Set<string>
  connSchemas: Record<string, SchemaInfo>
  dbSchemas: Record<string, SchemaInfo>
  redisDbs: Record<string, RedisDbInfo[]>
  activeTable: DbTab | null
  connSort: ConnSortKey
  connOrder: ConnSortOrder
  loading: boolean
  isFading: boolean
  /** The countdown a schema read shows while it retries, or null. */
  retryLabel: (key: string) => string | null
  onSortChange: (sort: ConnSortKey) => void
  onOrderChange: (order: ConnSortOrder) => void
  onToggleConn: (id: string) => void
  onToggleRedisConn: (id: string) => void
  onToggleDb: (connId: string, database: string) => void
  onSelectTreeTable: (connId: string, table: string, database?: string | null) => void
  onOpenRedisDb: (connId: string, dbIndex: number) => void
  onContextMenu: (e: React.MouseEvent, connId: string, table?: string, database?: string) => void
  onRegisterTreeItem: (key: string, el: HTMLButtonElement | null) => void
}

export const ConnectionTree = ({
  conns,
  expandedConns,
  expandedDbs,
  connSchemas,
  dbSchemas,
  redisDbs,
  activeTable,
  connSort,
  connOrder,
  loading,
  isFading,
  retryLabel,
  onSortChange,
  onOrderChange,
  onToggleConn,
  onToggleRedisConn,
  onToggleDb,
  onSelectTreeTable,
  onOpenRedisDb,
  onContextMenu,
  onRegisterTreeItem,
}: ConnectionTreeProps) => {
  const isActiveRedis = isRedisTab(activeTable)

  return (
    <Card>
      <CardHeader className="space-y-2 pb-2">
        <div className="flex items-center justify-between gap-2">
          <CardTitle className="flex items-center gap-2">
            Connections
            {loading ? <Loader2 className="size-3.5 animate-spin text-muted-foreground" aria-label="Loading." /> : null}
          </CardTitle>
          <span className="text-xs text-muted-foreground">{conns.length}</span>
        </div>

        {/* The list is arranged by the server, so picking a key or a
            direction here only sets the pair the next fetch asks for. The
            control sits under the panel header and above the rows, so it
            stays in place while a long tree scrolls. */}
        <div role="group" aria-label="Sort connections" className="flex flex-wrap items-center gap-1">
          {CONN_SORT_KEYS.map((key) => (
            <Button
              key={key}
              size="sm"
              variant={connSort === key ? "default" : "outline"}
              className="h-6 px-2 text-xs"
              aria-pressed={connSort === key}
              title={CONN_SORT_COPY[key].title}
              onClick={() => onSortChange(key)}
            >
              {CONN_SORT_COPY[key].label}
            </Button>
          ))}
          <Button
            size="sm"
            variant="outline"
            className="h-6 px-2 text-xs"
            aria-pressed={connOrder === "desc"}
            aria-label={connOrder === "asc" ? "Sort descending" : "Sort ascending"}
            title={connOrder === "asc" ? "Sort descending" : "Sort ascending"}
            onClick={() => onOrderChange(connOrder === "asc" ? "desc" : "asc")}
          >
            {connOrder === "asc" ? <ArrowUp className="h-3.5 w-3.5" /> : <ArrowDown className="h-3.5 w-3.5" />}
          </Button>
        </div>
      </CardHeader>

      <CardContent
        className={`space-y-1 p-2 transition-opacity duration-150 ease-out ${isFading ? "opacity-0" : "opacity-100"}`}
      >
        {/* Tree rows stay plain <button>s on purpose: shadcn's Button bakes in
            a minimum height and text-sm, and these rows are one line at 20px
            (db rows) and 28px (connection rows) so a deep tree stays readable.
            Every interactive control outside the tree uses Button. */}
        {conns.map((c) => (
          <div key={c.id} className="space-y-1">
            <button
              className="flex w-full items-center gap-1 rounded px-1 py-1 text-left text-sm hover:bg-accent"
            title={c.name}
              onClick={() => (c.provider === "Redis" ? onToggleRedisConn(c.id) : onToggleConn(c.id))}
              onContextMenu={(e) => {
                e.preventDefault()
                onContextMenu(e, c.id)
              }}
            >
              <span className="text-xs">{expandedConns.has(c.id) ? "▾" : "▸"}</span>
              <span className="truncate font-medium">{c.name}</span>
            </button>

            {expandedConns.has(c.id) ? (
              <div className="ml-3 space-y-0.5">
                {c.provider === "Redis" ? (
                  redisDbs[c.id] ? (
                    visibleRedisDbs(
                      redisDbs[c.id],
                      isActiveRedis && activeTable?.connId === c.id ? (activeTable?.redisDb ?? 0) : null,
                    ).map((db) => {
                      const isActive =
                        isActiveRedis && activeTable?.connId === c.id && activeTable?.redisDb === db.index
                      return (
                        <button
                          key={db.index}
                          className={`flex w-full items-center gap-1 rounded px-1 py-0.5 text-left text-xs hover:bg-accent ${isActive ? "bg-accent font-medium" : ""}`}
                          onClick={() => onOpenRedisDb(c.id, db.index)}
                        >
                          <span>🗄</span>
                          <span className="min-w-0 flex-1 truncate">{dbRowLabel(db)}</span>
                          <span className="shrink-0 tabular-nums text-muted-foreground">{dbRowCount(db)}</span>
                          {dbRowAsOf(db) ? (
                            <span className="shrink-0 text-[10px] text-amber-700 dark:text-amber-400">{dbRowAsOf(db)}</span>
                          ) : null}
                        </button>
                      )
                    })
                  ) : (
                    <p className="ml-2 text-xs text-muted-foreground">Loading.</p>
                  )
                ) : !connSchemas[c.id] || retryLabel(c.id) ? (
                  <p className="ml-2 flex items-center gap-1.5 text-xs text-muted-foreground">
                    <Loader2 className="size-3 animate-spin" />
                    {retryLabel(c.id) ?? "Loading…"}
                  </p>
                ) : null}

                {c.provider !== "Redis" && c.isServerLevel ? (
                  /* A server-level connection reaches every database on the
                     server, so the tree lists them before the tables. */
                  (connSchemas[c.id]?.databases ?? []).map((db) => {
                    const key = `${c.id}:${db}`
                    return (
                      <div key={key} className="space-y-0.5">
                        <button
                          className="flex w-full items-center gap-1 rounded px-1 py-0.5 text-left text-xs hover:bg-accent"
                          onClick={() => onToggleDb(c.id, db)}
                        >
                          <span className="text-xs">{expandedDbs.has(key) ? "▾" : "▸"}</span>
                          <span className="truncate font-medium">{db}</span>
                        </button>
                        {expandedDbs.has(key) ? (
                          <div className="ml-3 space-y-0.5">
                            {(dbSchemas[key]?.tables ?? []).map((t) => (
                              <button
                                key={`${key}:${t.name}`}
                                ref={(el) => onRegisterTreeItem(`${key}:${t.name}`, el)}
                                className={`flex w-full items-center gap-1 rounded px-1 py-0.5 text-left text-xs hover:bg-accent ${activeTable?.connId === c.id && activeTable?.table === t.name && activeTable?.database === db ? "bg-accent font-medium" : ""}`}
                                onClick={() => onSelectTreeTable(c.id, t.name, db)}
                                onContextMenu={(e) => {
                                  e.preventDefault()
                                  onContextMenu(e, c.id, t.name, db)
                                }}
                              >
                                <span>📄</span>
                                <span className="truncate">{t.name}</span>
                              </button>
                            ))}
                            {!dbSchemas[key] || retryLabel(key) ? (
                              <p className="ml-2 flex items-center gap-1.5 text-xs text-muted-foreground">
                                <Loader2 className="size-3 animate-spin" />
                                {retryLabel(key) ?? "Loading…"}
                              </p>
                            ) : null}
                          </div>
                        ) : null}
                      </div>
                    )
                  })
                ) : (
                  (connSchemas[c.id]?.tables ?? []).map((t) => (
                    <button
                      key={t.name}
                      ref={(el) => onRegisterTreeItem(`${c.id}:${t.name}`, el)}
                      className={`flex w-full items-center gap-1 rounded px-1 py-0.5 text-left text-xs hover:bg-accent ${activeTable?.connId === c.id && activeTable?.table === t.name ? "bg-accent font-medium" : ""}`}
                      onClick={() => onSelectTreeTable(c.id, t.name)}
                      onContextMenu={(e) => {
                        e.preventDefault()
                        onContextMenu(e, c.id, t.name)
                      }}
                    >
                      <span>📄</span>
                      <span className="truncate">{t.name}</span>
                    </button>
                  ))
                )}
              </div>
            ) : null}
          </div>
        ))}
        {conns.length === 0 && !loading ? <p className="text-sm text-muted-foreground">No connections.</p> : null}
      </CardContent>
    </Card>
  )
}
