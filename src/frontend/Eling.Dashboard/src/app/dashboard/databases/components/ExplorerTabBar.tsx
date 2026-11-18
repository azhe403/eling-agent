import { X } from "lucide-react"
import { DbConn, DbTab } from "@/app/dashboard/databases/types"
import { isRedisTab, tabKey } from "@/app/dashboard/databases/db-tabs"

export interface ExplorerTabBarProps {
  conns: DbConn[]
  openTabs: DbTab[]
  activeTable: DbTab | null
  onActivateTab: (idx: number) => void
  onCloseTab: (idx: number) => void
  onTabContextMenu: (e: React.MouseEvent, idx: number) => void
}

/**
 * The strip of open tables, spanning the top of the explorer.
 *
 * It sits above both the tree and the content, so the tabs never scroll away
 * with the table they opened.
 */
export const ExplorerTabBar = ({
  conns,
  openTabs,
  activeTable,
  onActivateTab,
  onCloseTab,
  onTabContextMenu,
}: ExplorerTabBarProps) => {
  if (openTabs.length === 0) return null

  return (
    <div className="flex shrink-0 items-center gap-1 overflow-x-auto border-b pb-2">
      {openTabs.map((t, idx) => {
        const isActive = activeTable ? tabKey(activeTable) === tabKey(t) : false
        const connName = conns.find((c) => c.id === t.connId)?.name
        const lineTwo = isRedisTab(t) ? `db${t.redisDb ?? 0}` : t.table
        return (
          <div
            key={tabKey(t)}
            className={`flex min-w-[208px] cursor-pointer items-center gap-1 rounded border px-2 py-1 text-xs ${
              isActive ? "border-accent bg-accent font-medium" : "border-transparent hover:bg-accent/50"
            }`}
            title={connName ? `${connName} · ${lineTwo}` : lineTwo}
            onClick={() => onActivateTab(idx)}
            onContextMenu={(e) => onTabContextMenu(e, idx)}
          >
            <span className="flex max-w-[160px] flex-col leading-tight">
              <span className="text-[10px] font-normal text-muted-foreground truncate">
                {connName ?? t.connId}
              </span>
              <span className="truncate">{lineTwo}</span>
            </span>
            <button
              className="ml-auto self-center rounded p-1 text-muted-foreground transition-colors hover:bg-foreground/10 hover:text-foreground"
              aria-label={`Close tab ${lineTwo}`}
              onClick={(e) => {
                e.stopPropagation()
                onCloseTab(idx)
              }}
            >
              <X className="size-5" strokeWidth={2.25} />
            </button>
          </div>
        )
      })}
    </div>
  )
}
