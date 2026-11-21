import { Table, TableHeader, TableBody, TableHead, TableRow, TableCell } from "@/components/ui/table"
import { Card, CardHeader, CardTitle, CardContent } from "@/components/ui/card"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { DatePicker } from "@/components/ui/date-picker"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { useCallback, useMemo } from "react"
import { ListFilter, ArrowUp, ArrowDown } from "lucide-react"
import { DbTab, QueryResult, SchemaInfo, ColumnSchema } from "@/app/dashboard/databases/types"
import { formatCacheAge, formatCacheExpiry, isStaleCache } from "@/app/dashboard/databases/utils/format-helpers"
import { BROWSE_LIMIT_OPTIONS, totalPages } from "@/app/dashboard/databases/utils/browse-paging"
import {
  activeCount,
  type BrowseFilter,
  type BrowseSort,
} from "@/app/dashboard/databases/filter-model"
import { FilterPanel } from "./FilterPanel"

export interface DataGridProps {
  activeTable: DbTab | null
  schemaInfo: SchemaInfo | null
  browseResult: QueryResult | null
  browseLoading: boolean
  browseTotal: number | null
  selectedIsReadOnly: boolean
  mutating: boolean
  editingCell: { row: number; col: number } | null
  editValue: string
  savingCell: boolean
  newRow: Record<string, string> | null
  /** The primary key columns of the active table; empty means read-only. */
  pkColumnNames: string[]
  /** Effective page size of this tab; one of BROWSE_LIMIT_OPTIONS. */
  browseLimit: number
  /** Effective 1-based page of this tab. */
  browsePage: number
  onBrowseLimitChange: (limit: number) => void
  onBrowsePageChange: (page: number) => void
  onRefresh: () => void
  onAddRow: () => void
  /** The tab's committed conditions. Undefined = none. */
  browseFilters?: BrowseFilter[]
  /** The tab's committed sort columns. Undefined = unsorted. */
  browseSort?: BrowseSort[]
  /** The tab's committed raw manual WHERE fragment. */
  manualCondition?: string
  /** Whether the committed manual condition participates. Absent = enabled. */
  manualEnabled?: boolean
  /** Commit a draft: filters/sort/manual condition for this tab; refetch happens in the parent. */
  onApplyBrowse?: (next: { filters: BrowseFilter[]; sort: BrowseSort[]; manualCondition: string; manualEnabled: boolean }) => void
  /** Header click: cycle single-column sort for one column. */
  onCycleSortColumn?: (column: string) => void
  onStartEdit: (row: number, col: number, value: unknown) => void
  onEditValueChange: (value: string) => void
  onCommitEdit: (overrideValue?: string) => void
  onCancelEdit: () => void
  onNewRowChange: (row: Record<string, string> | null) => void
  onSubmitNewRow: () => void
  onPromptDeleteRow: (row: number) => void
  onCellContextMenu: (e: React.MouseEvent, row: number, col: number) => void
  /** Whether the filter panel is open. Comes from the tab and persists with it. */
  filterOpen?: boolean
  /** Called when the reader opens or closes the filter panel. Parent commits to the tab. */
  onFilterOpenChange?: (open: boolean) => void
}

export const DataGrid = ({
  activeTable,
  schemaInfo,
  browseResult,
  browseLoading,
  browseTotal,
  selectedIsReadOnly,
  mutating,
  editingCell,
  editValue,
  savingCell,
  newRow,
  pkColumnNames,
  browseLimit,
  browsePage,
  onBrowseLimitChange,
  onBrowsePageChange,
  onRefresh,
  onAddRow,
  browseFilters,
  browseSort,
  manualCondition,
  manualEnabled = true,
  onApplyBrowse,
  onCycleSortColumn,
  onStartEdit,
  onEditValueChange,
  onCommitEdit,
  onCancelEdit,
  onNewRowChange,
  onSubmitNewRow,
  onPromptDeleteRow,
  onCellContextMenu,
  filterOpen = false,
  onFilterOpenChange,
}: DataGridProps) => {
  const setFilterOpen = (v: boolean | ((prev: boolean) => boolean)) => {
    const next = typeof v === "function" ? (v as (prev: boolean) => boolean)(filterOpen) : v
    if (next !== filterOpen && onFilterOpenChange) onFilterOpenChange(next)
  }
  const committedFilters = useMemo(() => browseFilters ?? [], [browseFilters])
  const committedSort = useMemo(() => browseSort ?? [], [browseSort])
  const activeFilterCount = activeCount(committedFilters, manualCondition, manualEnabled)
  const filteredColumns = useMemo(() => new Set(committedFilters.filter((f) => f.enabled).map((f) => f.column)), [committedFilters])
  const sortOrderFor = useCallback(
    (col: string) => committedSort.find((s) => s.column === col)?.order ?? null,
    [committedSort]
  )
  const columns = useMemo(() => browseResult?.columns ?? [], [browseResult])
  const rows = useMemo(() => browseResult?.rows ?? [], [browseResult])
  /** How many pages the total spans; null while the count is unknown. */
  const pages = totalPages(browseTotal, browseLimit)
  // With an unknown count, offer Next while a full page came back; an empty
  // grid has nothing to page into.
  const nextDisabled = pages !== null ? browsePage >= pages : !browseResult || browseResult.rows.length < browseLimit

  /** The schema of the table the grid shows, or null when it is not known. */
  const activeSchema = useCallback(
    () => schemaInfo?.tables?.find((t) => t.name === activeTable?.table) ?? null,
    [schemaInfo, activeTable]
  )

  /** The schema of one column, or null when the schema is not known. */
  const columnSchema = useCallback(
    (colName: string): ColumnSchema | null =>
      activeSchema()?.columns?.find((c) => c.name === colName) ?? null,
    [activeSchema]
  )

  /**
   * The classes a cell carries: the id column is narrow, a primary key cannot
   * be edited, and an integer id column sticks to the left edge.
   */
  const cellClass = useCallback((colName: string, isPk: boolean, isIdInt: boolean): string => {
    let cls = "border px-2 py-1 whitespace-nowrap"
    if (/^id$/i.test(colName)) cls += " w-px"
    if (isPk && !isIdInt) cls += " bg-muted/40"
    if (!isPk) cls += " cursor-pointer hover:bg-accent/40"
    if (isIdInt) {
      cls += " sticky left-0 z-10"
      cls += isPk ? " bg-muted" : " bg-background"
      cls += " shadow-[2px_0_4px_-2px_rgba(0,0,0,0.15)]"
    }
    return cls
  }, [])

  /**
   * True when the column holds integers, so the grid can pin it to the left.
   * The declared type decides; without one, every value has to look like a
   * number before the column is treated as one.
   */
  const isIntegerColumn = useCallback(
    (colName: string): boolean => {
      const declared = columnSchema(colName)?.dataType?.toLowerCase() ?? ""
      if (declared) return /int|serial|bigint|smallint/.test(declared)
      if (rows.length === 0) return false
      const idx = columns.indexOf(colName)
      return rows.every((r) => {
        const v = r[idx]
        return v === null || v === undefined || v === "" || /^-?\d+$/.test(String(v))
      })
    },
    [columnSchema, rows, columns]
  )

  return (
    <Card className="flex min-h-0 flex-1 flex-col">
      <CardHeader className="shrink-0">
        <CardTitle className="flex items-center gap-2">
          Data · {activeTable?.table}
          {browseResult ? (
            <Badge
              variant="outline"
              className={
                isStaleCache(browseResult.cachedAt)
                  ? "border-orange-500/40 bg-orange-500/10 text-orange-700 dark:text-orange-400"
                  : browseResult.fromCache
                  ? "border-amber-500/30 bg-amber-500/10 text-amber-700 dark:text-amber-400"
                  : "border-emerald-600/30 bg-emerald-600/10 text-emerald-700 dark:text-emerald-400"
              }
              title={formatCacheExpiry(browseResult.cachedAt, browseResult.cacheTtlSeconds)}
            >
              {browseResult.fromCache ? (isStaleCache(browseResult.cachedAt) ? "stale" : "cached") : "live"} · {formatCacheAge(browseResult.cachedAt)}
              {formatCacheExpiry(browseResult.cachedAt, browseResult.cacheTtlSeconds) && (
                <> · {formatCacheExpiry(browseResult.cachedAt, browseResult.cacheTtlSeconds)}</>
              )}
            </Badge>
          ) : null}
          {selectedIsReadOnly && (
            <Badge variant="outline" className="border-slate-500/30 bg-slate-500/10 text-slate-700 dark:text-slate-400">
              read-only
            </Badge>
          )}
        </CardTitle>
      </CardHeader>
      <CardContent className="flex min-h-0 flex-1 flex-col">
        <div className="flex min-h-0 flex-1 flex-col space-y-2">          <div className="mb-2 flex shrink-0 items-center gap-3">
            <Button
              size="sm"
              variant="outline"
              onClick={onRefresh}
              disabled={browseLoading || mutating}
              title="Refresh table data"
            >
              <span className={browseLoading ? "animate-spin" : ""}>⟳</span>
              <span className="ml-1">Refresh</span>
            </Button>
            <Button
              size="sm"
              variant="outline"
              onClick={onAddRow}
              title={
                selectedIsReadOnly
                  ? "read-only connection - editing disabled"
                  : pkColumnNames.length === 0
                    ? "no primary key - read only"
                    : undefined
              }
              disabled={mutating || pkColumnNames.length === 0 || selectedIsReadOnly || !!browseResult?.fromCache || !browseResult}
            >
              + Add row
            </Button>
            <Button
              size="sm"
              variant={activeFilterCount > 0 ? "secondary" : "outline"}
              onClick={() => setFilterOpen((v) => !v)}
              title="Filter and sort"
              aria-pressed={filterOpen}
              className="cursor-pointer"
            >
              <ListFilter className="size-4" />
              <span className="ml-1">Filter</span>
              {activeFilterCount > 0 ? (
                <Badge variant="secondary" className="ml-1.5 h-5 min-w-5 justify-center px-1 text-[10px] tabular-nums">
                  {activeFilterCount}
                </Badge>
              ) : null}
            </Button>
            {pkColumnNames.length === 0 ? (
              <span className="text-xs text-muted-foreground">No primary key - read only</span>
            ) : null}
          </div>

          {filterOpen ? (
            <FilterPanel
              columns={browseResult?.columns ?? (activeSchema()?.columns ?? []).map((c) => c.name)}
              columnTypes={Object.fromEntries(
                (activeSchema()?.columns ?? []).map((c) => [c.name, c.dataType ?? ""])
              )}
              initial={{
                filters: committedFilters,
                sort: committedSort,
                manualCondition: manualCondition ?? "",
                manualEnabled,
              }}
              committedSort={committedSort}
              applyPending={browseLoading}
              onApply={(next) => onApplyBrowse?.(next)}
              onClose={() => setFilterOpen(false)}
            />
          ) : null}

          {browseResult ? (
            <>
              <Table className="text-sm" containerClassName="min-h-0 flex-1 rounded-md border">
                <TableHeader>
                  <TableRow className="sticky top-0 z-20 bg-background">
                    {columns.map((col) => (
                      <TableHead
                        key={col}
                        className={`border px-2 py-1 text-left ${/^id$/i.test(col) ? "w-px whitespace-nowrap" : ""}`}
                      >
                        {onCycleSortColumn ? (
                          <button
                            type="button"
                            className="flex cursor-pointer items-center gap-1 rounded hover:text-foreground/80 hover:underline"
                            onClick={() => onCycleSortColumn(col)}
                            title="Cycle sort: none → ascending → descending"
                          >
                            {col}
                            <span className="flex w-7 shrink-0 items-center justify-start gap-0.5" aria-hidden={!sortOrderFor(col) && !filteredColumns.has(col)}>
                              {sortOrderFor(col) === "asc" ? (
                                <ArrowUp className="size-3 text-emerald-600" />
                              ) : sortOrderFor(col) === "desc" ? (
                                <ArrowDown className="size-3 text-emerald-600" />
                              ) : null}
                              {filteredColumns.has(col) ? <ListFilter className="size-3 text-emerald-600" /> : null}
                            </span>
                          </button>
                        ) : (
                          <span className="flex items-center gap-1">
                            {col}
                            <span className="flex w-7 shrink-0 items-center justify-start gap-0.5" aria-hidden={!sortOrderFor(col) && !filteredColumns.has(col)}>
                              {sortOrderFor(col) === "asc" ? (
                                <ArrowUp className="size-3 text-emerald-600" />
                              ) : sortOrderFor(col) === "desc" ? (
                                <ArrowDown className="size-3 text-emerald-600" />
                              ) : null}
                              {filteredColumns.has(col) ? <ListFilter className="size-3 text-emerald-600" /> : null}
                            </span>
                          </span>
                        )}
                      </TableHead>
                    ))}
                    <TableHead className="sticky right-0 z-40 w-20 border px-2 py-1 text-left bg-background">act</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {rows.map((row, i) => (
                    <TableRow key={i}>
                      {row.map((v, j) => {
                        const colName = columns[j]
                        const isPk = pkColumnNames.includes(colName)
                        const isEditing = editingCell?.row === i && editingCell?.col === j
                        const isIdInt = /^id$/i.test(colName) && isIntegerColumn(colName)

                        if (isEditing) {
                          const colType = columnSchema(colName)?.dataType?.toLowerCase() ?? ""
                          const isDate = colType.includes("date") || colType.includes("time")
                          if (isDate && !selectedIsReadOnly) {
                            return (
                              <TableCell key={j} className="border px-1 py-0.5">
                                <DatePicker
                                  value={editValue}
                                  withTime={colType.includes("time")}
                                  onChange={(v) => onEditValueChange(v)}
                                />
                              </TableCell>
                            )
                          }
                          return (
                            <TableCell key={j} className="border px-1 py-0.5 min-w-64 whitespace-nowrap">
                              <div className="flex items-center gap-1">
                                <input
                                  type="text"
                                  className="w-full rounded border px-1 py-0.5 text-sm"
                                  value={editValue}
                                  autoFocus
                                  onChange={(e) => onEditValueChange(e.target.value)}
                                  onKeyDown={(e) => {
                                    if (e.key === "Enter") {
                                      e.preventDefault()
                                      void onCommitEdit()
                                    }
                                    if (e.key === "Escape") onCancelEdit()
                                  }}
                                  disabled={savingCell}
                                  readOnly={selectedIsReadOnly}
                                />
                              </div>
                            </TableCell>
                          )
                        }

                        return (
                          <TableCell
                            key={j}
                            className={cellClass(colName, isPk, isIdInt)}
                            title={v === null || v === undefined ? "null" : String(v)}
                            onClick={() => {
                              if (!isPk && pkColumnNames.length > 0) onStartEdit(i, j, v)
                            }}
                            onContextMenu={(e) => {
                              e.preventDefault()
                              e.stopPropagation()
                              onCellContextMenu(e, i, j)
                            }}
                          >
                            {v === null || v === undefined ? (
                              <span className="italic text-muted-foreground/70">null</span>
                            ) : (
                              String(v)
                            )}
                          </TableCell>
                        )
                      })}
                      <TableCell className="sticky right-0 z-10 border px-2 py-1 w-20 bg-background">
                        {editingCell?.row === i ? (
                          <div className="flex gap-1">
                            {!selectedIsReadOnly ? (
                              <button
                                className="text-xs text-green-600 hover:underline"
                                onClick={() => void onCommitEdit()}
                                disabled={savingCell || mutating || !!browseResult?.fromCache}
                              >
                                apply
                              </button>
                            ) : null}
                            <button
                              className="text-xs text-muted-foreground hover:underline"
                              onClick={() => onCancelEdit()}
                              disabled={savingCell}
                            >
                              cancel
                            </button>
                          </div>
                        ) : (
                          <button
                            className="text-xs text-destructive hover:underline"
                            onClick={() => onPromptDeleteRow(i)}
                            title={
                              selectedIsReadOnly
                                ? "read-only connection - delete disabled"
                                : pkColumnNames.length === 0
                                  ? "no primary key - read only"
                                  : undefined
                            }
                            disabled={mutating || pkColumnNames.length === 0}
                          >
                            del
                          </button>
                        )}
                      </TableCell>
                    </TableRow>
                  ))}
                  {newRow ? (
                    <TableRow>
                      {columns.map((col) => {
                        const colType = columnSchema(col)?.dataType?.toLowerCase() ?? ""
                        const isDate = colType.includes("date") || colType.includes("time")
                        if (isDate) {
                          return (
                            <TableCell key={col} className="border px-1 py-0.5">
                              <DatePicker
                                value={newRow[col] ?? ""}
                                withTime={colType.includes("time")}
                                onChange={(v) => onNewRowChange({ ...newRow, [col]: v })}
                              />
                            </TableCell>
                          )
                        }
                        return (
                          <TableCell key={col} className="border px-1 py-0.5">
                            <input
                              className="w-full rounded border px-1 py-0.5 text-sm"
                              value={newRow[col] ?? ""}
                              onChange={(e) => onNewRowChange({ ...newRow, [col]: e.target.value })}
                            />
                          </TableCell>
                        )
                      })}
                      <TableCell className="flex gap-1 border px-2 py-1">
                        <button className="text-xs text-green-600 hover:underline" onClick={() => void onSubmitNewRow()} disabled={mutating}>
                          save
                        </button>
                        <button className="text-xs text-muted-foreground hover:underline" onClick={() => onNewRowChange(null)}>
                          cancel
                        </button>
                      </TableCell>
                    </TableRow>
                  ) : null}
                </TableBody>
              </Table>
              {browseResult.truncated ? <p className="mt-1 text-xs text-muted-foreground">Truncated.</p> : null}
            </>
          ) : (
            <Table className="text-sm" containerClassName="min-h-0 flex-1 rounded-md border">
              <TableHeader>
                <TableRow className="sticky top-0 z-20 bg-background">
                  {(activeSchema()?.columns ?? []).map((col) => (
                    <TableHead key={col.name} className="border px-2 py-1 text-left">
                      {col.name}
                    </TableHead>
                  ))}
                  <TableHead className="sticky right-0 z-40 w-20 border px-2 py-1 text-left bg-background">act</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                <TableRow>
                  <TableCell
                    colSpan={(activeSchema()?.columns?.length ?? 0) + 1}
                    className="py-8 text-center text-sm text-muted-foreground"
                  >
                    Loading...
                  </TableCell>
                </TableRow>
              </TableBody>
            </Table>
          )}
        </div>

        <div className="sticky bottom-0 z-20 flex shrink-0 items-center gap-2 rounded-md bg-muted/80 px-3 py-1.5 text-xs font-medium text-muted-foreground backdrop-blur">
          <span>
            {browseResult
              ? browseTotal !== null
                ? `Total rows: ${browseTotal} (showing ${browseResult.rows.length}${browseResult.truncated ? ", truncated" : ""})`
                : `Total rows: ${browseResult.rows.length}`
              : "Total rows: -"}
          </span>
          <span className="ml-auto flex items-center gap-2">
            <Select
              value={String(browseLimit)}
              disabled={browseLoading}
              onValueChange={(v) => {
                if (v) onBrowseLimitChange(Number(v))
              }}
            >
              <SelectTrigger className="h-6 w-24 text-xs" aria-label="Rows per page">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {BROWSE_LIMIT_OPTIONS.map((n) => (
                  <SelectItem key={n} value={String(n)}>
                    Rows {n}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <Button
              variant="outline"
              size="sm"
              className="h-6 w-6 p-0"
              disabled={browseLoading || browsePage <= 1}
              onClick={() => onBrowsePageChange(browsePage - 1)}
              aria-label="Previous page"
            >
              ‹
            </Button>
            <span className="tabular-nums">
              {pages === null ? `Halaman ${browsePage} dari ?` : `Halaman ${browsePage} dari ${pages}`}
            </span>
            <Button
              variant="outline"
              size="sm"
              className="h-6 w-6 p-0"
              disabled={browseLoading || nextDisabled}
              onClick={() => onBrowsePageChange(browsePage + 1)}
              aria-label="Next page"
            >
              ›
            </Button>
          </span>
        </div>
      </CardContent>
    </Card>
  )
}


