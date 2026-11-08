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
        return (
          <div
            key={tabKey(t)}
            className={`flex cursor-pointer items-center gap-1 rounded border px-2 py-1 text-xs ${
              isActive ? "border-accent bg-accent font-medium" : "border-transparent hover:bg-accent/50"
            }`}
            onClick={() => onActivateTab(idx)}
            onContextMenu={(e) => onTabContextMenu(e, idx)}
          >
            <span className="max-w-[140px] truncate">
              {isRedisTab(t)
                ? `${conns.find((c) => c.id === t.connId)?.name ?? t.table} · db${t.redisDb ?? 0}`
                : t.table}
            </span>
            <button
              className="ml-1 text-muted-foreground hover:text-foreground"
              onClick={(e) => {
                e.stopPropagation()
                onCloseTab(idx)
              }}
            >
              ×
            </button>
          </div>
        )
      })}
    </div>
  )
}
