"use client"

import { Plus, X, Search, Folder, FileText } from "lucide-react"
import { Button } from "@/components/ui/button"
import { cn } from "@/lib/utils"
import { tabLabel, type SearchTab } from "./searchTabs"

interface SearchTabsBarProps {
  tabs: SearchTab[]
  activeId: string
  onSelectTab: (id: string) => void
  onCloseTab: (id: string) => void
  onAddTab: () => void
}

function ScopeIcon({ scope }: { scope: SearchTab["scope"] }) {
  if (scope === "path") {
    return <Folder className="size-3 shrink-0 text-muted-foreground" />
  }
  if (scope === "file") {
    return <FileText className="size-3 shrink-0 text-muted-foreground" />
  }
  return <Search className="size-3 shrink-0 text-muted-foreground" />
}

export function SearchTabsBar({
  tabs,
  activeId,
  onSelectTab,
  onCloseTab,
  onAddTab,
}: SearchTabsBarProps) {
  return (
    <div
      role="tablist"
      aria-label="Codebase search tabs"
      className="flex w-full items-center gap-1.5 overflow-x-auto py-1 text-xs select-none [scrollbar-width:none] [&::-webkit-scrollbar]:hidden"
    >
      <div className="flex items-center gap-1">
        {tabs.map((tab) => {
          const isActive = tab.id === activeId
          const label = tabLabel(tab)
          const workspaceCount =
            tab.selection.kind === "workspaces" ? tab.selection.roots.length : 0

          return (
            <div
              key={tab.id}
              role="tab"
              aria-selected={isActive}
              tabIndex={isActive ? 0 : -1}
              onClick={() => onSelectTab(tab.id)}
              onKeyDown={(e) => {
                if (e.key === "Enter" || e.key === " ") {
                  e.preventDefault()
                  onSelectTab(tab.id)
                }
              }}
              className={cn(
                "group relative flex h-7 w-36 shrink-0 cursor-pointer items-center justify-between gap-1.5 rounded-md border px-2 text-xs transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
                isActive
                  ? "border-border bg-background font-medium text-foreground shadow-xs"
                  : "border-transparent bg-muted/40 text-muted-foreground hover:bg-muted/80 hover:text-foreground"
              )}
              title={`${label} (${tab.scope})`}
            >
              <div className="flex min-w-0 flex-1 items-center gap-1.5">
                <ScopeIcon scope={tab.scope} />
                <span className="truncate">{label}</span>
                {workspaceCount > 0 && (
                  <span
                    className="shrink-0 rounded-xs bg-muted px-1 py-0.2 text-[10px] text-muted-foreground"
                    title={`${workspaceCount} workspace(s)`}
                  >
                    {workspaceCount}
                  </span>
                )}
              </div>

              {tabs.length > 1 && (
                <button
                  type="button"
                  aria-label={`Close tab ${label}`}
                  onClick={(e) => {
                    e.stopPropagation()
                    onCloseTab(tab.id)
                  }}
                  className={cn(
                    "flex size-4 shrink-0 items-center justify-center rounded-xs opacity-60 transition-opacity hover:bg-foreground/10 hover:opacity-100",
                    isActive ? "opacity-80" : "opacity-0 group-hover:opacity-60"
                  )}
                >
                  <X className="size-3" />
                </button>
              )}
            </div>
          )
        })}
      </div>

      <Button
        type="button"
        variant="ghost"
        size="icon-xs"
        onClick={onAddTab}
        aria-label="New search tab"
        title="Open new search tab"
        className="size-7 shrink-0 text-muted-foreground hover:text-foreground"
      >
        <Plus className="size-3.5" />
      </Button>
    </div>
  )
}
