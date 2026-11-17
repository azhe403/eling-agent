"use client"

import { useMemo, useState } from "react"
import { X, Plus, ArrowUp, ArrowDown } from "lucide-react"

import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import {
  opsFor,
  columnKind,
  type BrowseFilter,
  type BrowseSort,
} from "../../filter-model"

export interface FilterPanelProps {
  /** The table's columns, in grid order. */
  columns: string[]
  /** Column name → declared data type (e.g. "tinyint(1)"). Empty when the schema is unknown. */
  columnTypes: Record<string, string>
  /** The tab's committed state; drafts start from it on mount. */
  initial: { filters: BrowseFilter[]; sort: BrowseSort[]; manualCondition: string }
  /** The tab's committed sort, live. The panel's sort rows follow it until the reader edits them. */
  committedSort: BrowseSort[]
  /** Commit the current draft (grid refetches, page resets to 1). */
  onApply: (next: { filters: BrowseFilter[]; sort: BrowseSort[]; manualCondition: string }) => void
  /** Close the panel without applying - unsaved drafts are discarded. */
  onClose: () => void
}

const selectClass = "h-8 w-full text-xs"

/**
 * The condition builder for the Data tab. Draft state is local and the
 * parent mounts this only while the panel is open, so closing discards
 * unsaved edits - applying is explicit (Apply or Ctrl+Enter). The sort rows
 * are the exception to pure draft semantics: they render straight from the
 * tab's committed sort until the reader edits them here, so an out-of-band
 * header sort is never silently overwritten by Apply.
 */
export const FilterPanel = ({ columns, columnTypes, initial, committedSort, onApply, onClose }: FilterPanelProps) => {
  const [filters, setFilters] = useState<BrowseFilter[]>(initial.filters)
  const [draftSort, setDraftSort] = useState<BrowseSort[]>(initial.sort)
  const [sortDirty, setSortDirty] = useState(false)
  const [manualCondition, setManualCondition] = useState(initial.manualCondition)

  // One source of truth for the sort rows: committed until the reader edits
  // them here, the reader's draft afterwards. Header changes ride through on
  // the former path, never on the latter.
  const sort = sortDirty ? draftSort : committedSort
  const editSort = (updater: (current: BrowseSort[]) => BrowseSort[]) => {
    setSortDirty(true)
    setDraftSort((prev) => updater(sortDirty ? prev : committedSort))
  }

  const defaultColumn = useMemo(() => columns[0] ?? "", [columns])

  const apply = () => onApply({ filters, sort, manualCondition })

  const addCondition = () =>
    setFilters((f) => [...f, { column: defaultColumn, op: "eq", value: "", enabled: true, logic: f.length === 0 ? "first" : "and" }])

  const addSort = () =>
    editSort((s) => {
      const used = new Set(s.map((x) => x.column))
      const free = columns.find((c) => !used.has(c))
      return free ? [...s, { column: free, order: "asc" as const }] : s
    })

  const patchFilter = (i: number, patch: Partial<BrowseFilter>) =>
    setFilters((f) => f.map((item, j) => (j === i ? { ...item, ...patch } : item)))

  const patchSort = (i: number, patch: Partial<BrowseSort>) =>
    editSort((s) => s.map((item, j) => (j === i ? { ...item, ...patch } : item)))

  const setAllEnabled = (enabled: boolean) =>
    setFilters((f) => f.map((item) => ({ ...item, enabled })))

  const opOptions = (column: string) => opsFor(columnKind(columnTypes[column] ?? ""))

  const onKeyDown = (e: React.KeyboardEvent) => {
    if ((e.ctrlKey || e.metaKey) && e.key === "Enter") {
      e.preventDefault()
      apply()
    }
  }

  return (
    <div className="space-y-2 rounded-md border bg-muted/30 p-2" onKeyDown={onKeyDown}>
      <div className="flex items-center gap-2">
        <label className="w-44 shrink-0 text-xs font-medium text-muted-foreground" htmlFor="manual-condition">
          Manual query condition
        </label>
        <Input
          id="manual-condition"
          className="h-8 flex-1 font-mono text-xs"
          placeholder="status = 1 AND name LIKE 'A%'"
          spellCheck={false}
          value={manualCondition}
          onChange={(e) => setManualCondition(e.target.value)}
          aria-label="Manual query condition"
        />
        <button
          type="button"
          className="cursor-pointer text-xs text-muted-foreground hover:underline"
          onClick={() => setManualCondition("")}
          disabled={!manualCondition}
        >
          Clear
        </button>
      </div>
      <p className="text-[11px] text-muted-foreground">
        ANDed verbatim, raw SQL. Leave empty to filter only by the rows below.
      </p>

      {filters.length > 0 ? (
        <div className="space-y-1">
          {filters.map((f, i) => {
            const isFirst = i === 0
            const needsValue = f.op !== "isNull" && f.op !== "isNotNull"
            return (
              <div key={i} className={`flex items-center gap-1.5 ${f.enabled ? "" : "opacity-50"}`}>
                <input
                  type="checkbox"
                  className="size-4 shrink-0 cursor-pointer accent-emerald-600"
                  checked={f.enabled}
                  onChange={(e) => patchFilter(i, { enabled: e.target.checked })}
                  aria-label="Enabled"
                />
                <Select
                  value={isFirst ? "first" : f.logic}
                  onValueChange={(v) => patchFilter(i, { logic: v as BrowseFilter["logic"] })}
                  disabled={isFirst}
                >
                  <SelectTrigger className="h-8 w-28 text-xs" aria-label="Logic">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="first">First</SelectItem>
                    <SelectItem value="and">And (AND)</SelectItem>
                    <SelectItem value="or">Or (OR)</SelectItem>
                  </SelectContent>
                </Select>
                <Select value={f.column} onValueChange={(v) => v && patchFilter(i, { column: v })}>
                  <SelectTrigger className={`${selectClass} w-40`} aria-label="Column">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {columns.map((c) => (
                      <SelectItem key={c} value={c}>
                        {c}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                <Select value={f.op} onValueChange={(v) => patchFilter(i, { op: v as BrowseFilter["op"] })}>
                  <SelectTrigger className={`${selectClass} w-36`} aria-label="Operator">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {opOptions(f.column).map((o) => (
                      <SelectItem key={o.value} value={o.value}>
                        {o.label}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                {needsValue ? (
                  <Input
                    className="h-8 min-w-0 flex-1 font-mono text-xs"
                    value={f.value}
                    onChange={(e) => patchFilter(i, { value: e.target.value })}
                    placeholder="value"
                    spellCheck={false}
                    aria-label="Value"
                  />
                ) : (
                  <span className="flex-1" />
                )}
                <button
                  type="button"
                  className="cursor-pointer rounded p-1 text-muted-foreground hover:bg-accent hover:text-foreground"
                  onClick={() => setFilters((prev) => prev.filter((_, j) => j !== i))}
                  aria-label="Remove condition"
                >
                  <X className="size-4" />
                </button>
              </div>
            )
          })}
        </div>
      ) : null}

      {sort.length > 0 ? (
        <div className="space-y-1">
          {sort.map((s, i) => (
            <div key={i} className="flex items-center gap-1.5">
              <span className="flex size-4 shrink-0 items-center justify-center text-muted-foreground">
                {s.order === "asc" ? <ArrowUp className="size-4" /> : <ArrowDown className="size-4" />}
              </span>
              <Select value={s.column} onValueChange={(v) => v && patchSort(i, { column: v })}>
                <SelectTrigger className={`${selectClass} w-40`} aria-label="Sort column">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {columns.map((c) => (
                    <SelectItem key={c} value={c}>
                      {c}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
              <Select value={s.order} onValueChange={(v) => patchSort(i, { order: v as BrowseSort["order"] })}>
                <SelectTrigger className={`${selectClass} w-32`} aria-label="Sort order">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="asc">Ascending</SelectItem>
                  <SelectItem value="desc">Descending</SelectItem>
                </SelectContent>
              </Select>
              <span className="flex-1" />
              <button
                type="button"
                className="cursor-pointer rounded p-1 text-muted-foreground hover:bg-accent hover:text-foreground"
                onClick={() => editSort((prev) => prev.filter((_, j) => j !== i))}
                aria-label="Remove sort"
              >
                <X className="size-4" />
              </button>
            </div>
          ))}
        </div>
      ) : null}

      <div className="flex flex-wrap items-center gap-2 pt-1">
        <Button variant="outline" size="sm" onClick={addCondition} className="cursor-pointer">
          <Plus className="mr-1 size-3.5" /> Add condition
        </Button>
        <Button
          variant="outline"
          size="sm"
          onClick={addSort}
          className="cursor-pointer"
          disabled={sort.length >= columns.length}
          title={sort.length >= columns.length ? "All columns are already sorted" : undefined}
        >
          <Plus className="mr-1 size-3.5" /> Add sort
        </Button>
        <span className="ml-auto flex items-center gap-1.5">
          <Button variant="outline" size="sm" onClick={() => setAllEnabled(true)} disabled={filters.length === 0} className="cursor-pointer">
            Enable all
          </Button>
          <Button variant="outline" size="sm" onClick={() => setAllEnabled(false)} disabled={filters.length === 0} className="cursor-pointer">
            Disable all
          </Button>
          <Button
            variant="outline"
            size="sm"
            className="cursor-pointer"
            onClick={() => {
              setFilters([])
              setDraftSort([])
              setSortDirty(false)
              setManualCondition("")
              onApply({ filters: [], sort: [], manualCondition: "" })
            }}
          >
            Clear all
          </Button>
          <Button size="sm" onClick={apply} className="cursor-pointer">
            Apply
          </Button>
          <Button variant="ghost" size="sm" onClick={onClose} className="cursor-pointer" title="Close without applying">
            <X className="size-4" />
          </Button>
        </span>
      </div>
    </div>
  )
}
