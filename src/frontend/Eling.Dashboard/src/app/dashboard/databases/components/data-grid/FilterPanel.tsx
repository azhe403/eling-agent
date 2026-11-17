"use client"

import { useMemo, useState } from "react"
import { X, Plus, ArrowUp, ArrowDown, Loader2 } from "lucide-react"

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
  /** True while the committed state is being refetched - locks Apply so a spam-click does not fire duplicate /browse calls. */
  applyPending?: boolean
  /** Close the panel without applying - unsaved drafts are discarded. */
  onClose: () => void
}

/** Trigger sizing: width follows the label (autosize), floored, and the
 * column trigger gets a cap so long names clamp instead of stretching. */
const selectClass = "h-8 w-fit cursor-pointer text-xs whitespace-nowrap"

const LOGIC_ITEMS = [
  { value: "first", label: "First" },
  { value: "and", label: "And (AND)" },
  { value: "or", label: "Or (OR)" },
] as const

const SORT_ORDER_ITEMS = [
  { value: "asc", label: "Ascending" },
  { value: "desc", label: "Descending" },
] as const

/** Identity mapping value -> label, for selects whose list text equals the stored value. */
const identityItems = (values: string[]) => values.map((v) => ({ value: v, label: v }))

/**
 * The condition builder for the Data tab. Draft state is local and the
 * parent mounts this only while the panel is open, so closing discards
 * unsaved edits - applying is explicit (Apply or Ctrl+Enter). The sort rows
 * are the exception to pure draft semantics: they render straight from the
 * tab's committed sort until the reader edits them here, so an out-of-band
 * header sort is never silently overwritten by Apply.
 */
export const FilterPanel = ({ columns, columnTypes, initial, committedSort, onApply, onClose, applyPending = false }: FilterPanelProps) => {
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

  const defaultColumn = useMemo(() => columns.find((c) => !/^id$/i.test(c)) ?? columns[0] ?? "", [columns])

  const apply = () => {
    if (applyPending) return
    onApply({ filters, sort, manualCondition })
  }

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
    if (e.nativeEvent.isComposing) return // an IME candidate commit is not an Apply
    if ((e.ctrlKey || e.metaKey) && e.key === "Enter") {
      e.preventDefault()
      apply()
      return
    }
    // Plain Enter commits only from a text field - never from a select,
    // checkbox or button, where Enter has its own native meaning.
    const t = e.target as EventTarget | null
    const textField =
      t instanceof HTMLInputElement &&
      ["text", "search", "url", "email", "tel", "password", "number"].includes(t.type)
    if (e.key === "Enter" && !e.ctrlKey && !e.metaKey && !e.altKey && !e.shiftKey && textField) {
      e.preventDefault()
      apply()
    }
  }

  return (
    <div className="space-y-2 rounded-md border bg-muted/30 p-2" onKeyDown={onKeyDown}>
      <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:gap-2">
        <label className="w-full shrink-0 text-xs font-medium text-muted-foreground sm:w-44" htmlFor="manual-condition">
          Manual query condition
        </label>
        <Input
          id="manual-condition"
          className="h-8 w-full flex-1 font-mono text-xs"
          placeholder="status = 1 AND name LIKE 'A%'"
          spellCheck={false}
          value={manualCondition}
          onChange={(e) => setManualCondition(e.target.value)}
          aria-label="Manual query condition"
        />
        <button
          type="button"
          className="cursor-pointer text-xs text-muted-foreground hover:underline sm:static self-end sm:self-auto"
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
              <div key={i} className={`flex flex-wrap items-center gap-1.5 rounded-md border border-border/30 bg-muted/20 p-1.5 ${f.enabled ? "" : "opacity-50"}`}>
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
                  items={LOGIC_ITEMS}
                >
                  <SelectTrigger className={`${selectClass} min-w-36`} aria-label="Logic">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent align="start" alignItemWithTrigger={false}>
                    <SelectItem value="first">First</SelectItem>
                    <SelectItem value="and">And (AND)</SelectItem>
                    <SelectItem value="or">Or (OR)</SelectItem>
                  </SelectContent>
                </Select>
                <Select value={f.column} onValueChange={(v) => v && patchFilter(i, { column: v })} items={identityItems(columns)}>
                  <SelectTrigger className={`${selectClass} min-w-36 max-w-56 truncate`} aria-label="Column">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent align="start" alignItemWithTrigger={false} className="w-auto min-w-56 max-w-72">
                    {columns.map((c) => (
                      <SelectItem key={c} value={c}>
                        {c}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                <Select value={f.op} onValueChange={(v) => patchFilter(i, { op: v as BrowseFilter["op"] })} items={opOptions(f.column)}>
                  <SelectTrigger className={`${selectClass} min-w-36`} aria-label="Operator">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent align="start" alignItemWithTrigger={false}>
                    {opOptions(f.column).map((o) => (
                      <SelectItem key={o.value} value={o.value}>
                        {o.label}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                {needsValue ? (
                  <Input
                    className="h-8 w-full min-w-0 flex-1 basis-[calc(100%-1.875rem)] font-mono text-xs lg:w-auto lg:basis-auto"
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
                  className="ml-auto cursor-pointer rounded p-1 text-muted-foreground hover:bg-accent hover:text-foreground"
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
            <div key={i} className="flex flex-wrap items-center gap-1.5 rounded-md border border-border/30 bg-muted/20 p-1.5">
              <span className="flex size-4 shrink-0 items-center justify-center text-muted-foreground">
                {s.order === "asc" ? <ArrowUp className="size-4" /> : <ArrowDown className="size-4" />}
              </span>
              <Select value={s.column} onValueChange={(v) => v && patchSort(i, { column: v })} items={identityItems(columns)}>
                <SelectTrigger className={`${selectClass} min-w-36 max-w-56 truncate`} aria-label="Sort column">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent align="start" alignItemWithTrigger={false} className="w-auto min-w-56 max-w-72">
                  {columns.map((c) => (
                    <SelectItem key={c} value={c}>
                      {c}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
              <Select value={s.order} onValueChange={(v) => patchSort(i, { order: v as BrowseSort["order"] })} items={SORT_ORDER_ITEMS}>
                <SelectTrigger className={`${selectClass} min-w-36`} aria-label="Sort order">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent align="start" alignItemWithTrigger={false}>
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
            disabled={applyPending}
            size="sm"
            className="cursor-pointer"
            onClick={() => {
              if (applyPending) return
              setFilters([])
              setDraftSort([])
              setSortDirty(false)
              setManualCondition("")
              onApply({ filters: [], sort: [], manualCondition: "" })
            }}
          >
            Clear all
          </Button>
          <Button size="sm" onClick={apply} className="cursor-pointer" disabled={applyPending} title={applyPending ? "Applying…" : undefined}>
            {applyPending ? (
              <>
                <Loader2 className="mr-1.5 size-3.5 animate-spin" aria-hidden />
                Applying…
              </>
            ) : (
              "Apply"
            )}
          </Button>
          <Button variant="ghost" size="sm" onClick={onClose} className="cursor-pointer" title="Close without applying">
            <X className="size-4" />
          </Button>
        </span>
      </div>
    </div>
  )
}
