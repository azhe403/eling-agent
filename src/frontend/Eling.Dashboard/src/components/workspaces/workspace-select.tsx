"use client"

import { useSyncExternalStore, useState } from "react"
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover"
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"
import { FolderGit2, Layers } from "lucide-react"
import { cn } from "cn"
import { workspaceName, type WorkspaceRow } from "./runtime-rows"
import { selectionSummary, type WorkspaceSelection } from "./selection"

/**
 * Narrow-screen fallback for `WorkspaceScopePanel`. When `onToggleRoot` is
 * provided it renders a checkbox list (multi-select) inside a popover, so the
 * narrow view keeps the same include/exclude affordance as the side panel.
 * Otherwise it falls back to the single-select shadcn Select.
 */
export function WorkspaceSelect({
  rows,
  selection,
  onSelectAll,
  onSelectOnly,
  onToggleRoot,
  label,
}: {
  rows: readonly WorkspaceRow[]
  selection: WorkspaceSelection
  onSelectAll: () => void
  onSelectOnly: (root: string) => void
  onToggleRoot?: (root: string) => void
  label?: string
}) {
  const [open, setOpen] = useState(false)
  const isNarrow = useSyncExternalStore(
    (cb) => {
      window.addEventListener("resize", cb)
      return () => window.removeEventListener("resize", cb)
    },
    () => window.innerWidth < 1024,
    () => true,
  )

  if (!isNarrow) return null

  if (!onToggleRoot) {
    return <SingleSelect rows={rows} selection={selection} onSelectAll={onSelectAll} onSelectOnly={onSelectOnly} label={label} open={open} onOpenChange={setOpen} />
  }

  const included =
    selection.kind === "all"
      ? new Set(rows.map((r) => r.root))
      : new Set(selection.roots)

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger
        className="inline-flex h-9 w-full max-w-44 shrink-0 items-center justify-between truncate rounded-md border border-input bg-transparent px-3 text-sm shadow-xs sm:max-w-72"
        title={selectionSummary(selection)}
      >
        {selection.kind === "all"
          ? "🌐 All workspaces"
          : (label ?? `📁 ${selectionSummary(selection)}`)}
      </PopoverTrigger>
      <PopoverContent align="start" className="w-72 max-h-[60vh] overflow-y-auto p-2">
        <div className="flex flex-col gap-1">
          <button
            type="button"
            onClick={() => {
              onSelectAll()
              setOpen(false)
            }}
            className={cn(
              "flex cursor-pointer items-center gap-2 rounded-md px-2 py-1.5 text-sm text-left",
              selection.kind === "all" ? "bg-accent font-medium" : "hover:bg-accent/60",
            )}
          >
            <Layers className="size-4 shrink-0" />
            All workspaces
          </button>
          <ul className="flex flex-col gap-0.5">
            {rows.map(({ root, alive }) => {
              const name = workspaceName(root)
              return (
                <li key={root} className="flex items-center gap-2 rounded-md px-1 py-1 hover:bg-accent/60">
                  <input
                    type="checkbox"
                    className="accent-primary"
                    checked={included.has(root)}
                    onChange={() => onToggleRoot(root)}
                    aria-label={`Include ${name}`}
                  />
                  <button
                    type="button"
                    onClick={() => {
                      onSelectOnly(root)
                      setOpen(false)
                    }}
                    title={root}
                    className="flex min-w-0 flex-1 items-center gap-1.5 text-left text-sm"
                  >
                    <FolderGit2 className="size-4 shrink-0 text-muted-foreground" />
                    <span className="truncate">{name}</span>
                  </button>
                  <span
                    className={cn(
                      "size-2 shrink-0 rounded-full",
                      alive ? "bg-emerald-500" : "bg-muted-foreground/40",
                    )}
                    title={alive ? "Runtime is live" : "Offline"}
                  />
                </li>
              )
            })}
            {rows.length === 0 && (
              <li className="p-2 text-sm text-muted-foreground">No indexed workspaces.</li>
            )}
          </ul>
        </div>
      </PopoverContent>
    </Popover>
  )
}

function SingleSelect({
  rows,
  selection,
  onSelectAll,
  onSelectOnly,
  label,
  open,
  onOpenChange,
}: {
  rows: readonly WorkspaceRow[]
  selection: WorkspaceSelection
  onSelectAll: () => void
  onSelectOnly: (root: string) => void
  label?: string
  open: boolean
  onOpenChange: (open: boolean) => void
}) {
  return (
    <Select
      open={open}
      onOpenChange={onOpenChange}
      value={selectValue(selection)}
      onValueChange={(v: string | null) => (!v || v === "all" ? onSelectAll() : onSelectOnly(v as string))}
    >
      <SelectTrigger
        aria-label="Workspace scope"
        title={selectionSummary(selection)}
        className="w-full max-w-44 shrink-0 truncate sm:max-w-72"
      >
        <SelectValue placeholder="Select workspace...">
          {() =>
            selection.kind === "all"
              ? "🌐 All workspaces"
              : (label ?? `📁 ${selectionSummary(selection)}`)}
        </SelectValue>
      </SelectTrigger>
      <SelectContent
        side="bottom"
        align="start"
        alignItemWithTrigger={false}
        style={{ width: "auto", maxWidth: "min(36rem, calc(100vw - 2rem))" }}
      >
        <SelectItem value="all">🌐 All workspaces</SelectItem>
        {rows.map((r) => (
          <SelectItem key={r.root} value={r.root} title={r.root}>
            <span className="flex min-w-0 flex-col items-start">
              <span>📁 {workspaceName(r.root)}</span>
              <span className="text-xs whitespace-normal break-all text-muted-foreground">
                {r.root}
              </span>
            </span>
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  )
}

function selectValue(selection: WorkspaceSelection): string {
  if (selection.kind === "all") return "all"
  if (selection.roots.length === 1) return selection.roots[0]
  return "multiple"
}