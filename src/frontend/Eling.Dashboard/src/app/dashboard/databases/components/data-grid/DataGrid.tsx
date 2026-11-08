import { Table, TableHeader, TableBody, TableHead, TableRow, TableCell } from "@/components/ui/table"
import { Card, CardHeader, CardTitle, CardContent } from "@/components/ui/card"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { DatePicker } from "@/components/ui/date-picker"
import { useCallback, useMemo } from "react"
import { DbTab, QueryResult, SchemaInfo, ColumnSchema } from "@/app/dashboard/databases/types"
import { formatCacheAge, isStaleCache } from "@/app/dashboard/databases/utils/format-helpers"

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
  onRefresh: () => void
  onAddRow: () => void
  onStartEdit: (row: number, col: number, value: unknown) => void
  onEditValueChange: (value: string) => void
  onCommitEdit: (overrideValue?: string) => void
  onCancelEdit: () => void
  onNewRowChange: (row: Record<string, string> | null) => void
  onSubmitNewRow: () => void
  onPromptDeleteRow: (row: number) => void
  onCellContextMenu: (e: React.MouseEvent, row: number, col: number) => void
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
  onRefresh,
  onAddRow,
  onStartEdit,
  onEditValueChange,
  onCommitEdit,
  onCancelEdit,
  onNewRowChange,
  onSubmitNewRow,
  onPromptDeleteRow,
  onCellContextMenu,
}: DataGridProps) => {
  const columns = useMemo(() => browseResult?.columns ?? [], [browseResult])
  const rows = useMemo(() => browseResult?.rows ?? [], [browseResult])

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
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          Data - {activeTable?.table}
          {browseResult?.fromCache ? (
            <Badge
              variant="outline"
              className={
                isStaleCache(browseResult.cachedAt)
                  ? "border-orange-500/40 bg-orange-500/10 text-orange-700 dark:text-orange-400"
                  : "border-amber-500/30 bg-amber-500/10 text-amber-700 dark:text-amber-400"
              }
            >
              {isStaleCache(browseResult.cachedAt) ? "stale" : "cached"} · {formatCacheAge(browseResult.cachedAt)}
            </Badge>
          ) : null}
        </CardTitle>
      </CardHeader>
      <CardContent>
        <div className="space-y-2">
          {selectedIsReadOnly && !browseResult?.fromCache ? (
            <p className="text-xs text-muted-foreground">
              Read-only connection - cells can be edited, but never persisted.
            </p>
          ) : null}
          {browseResult?.fromCache ? (
            <p className="text-xs text-amber-700 dark:text-amber-400">
              Cached data - the database is unreachable, so edits are disabled.
            </p>
          ) : null}

          <div className="mb-2 flex items-center gap-3">
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
            {pkColumnNames.length === 0 ? (
              <span className="text-xs text-muted-foreground">No primary key - read only</span>
            ) : null}
          </div>

          {browseResult ? (
            <>
              <Table className="text-sm" containerClassName="h-[calc(100vh-280px)] rounded-md border">
                <TableHeader>
                  <TableRow className="sticky top-0 z-20 bg-background">
                    {columns.map((col) => (
                      <TableHead
                        key={col}
                        className={`border px-2 py-1 text-left ${/^id$/i.test(col) ? "w-px whitespace-nowrap" : ""}`}
                      >
                        {col}
                      </TableHead>
                    ))}
                    <TableHead className="sticky right-0 z-40 w-16 border px-2 py-1 text-left bg-background">act</TableHead>
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
                            <TableCell key={j} className="border px-1 py-0.5">
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
                                {!selectedIsReadOnly ? (
                                  <button
                                    type="button"
                                    className="shrink-0 rounded border px-1.5 py-0.5 text-xs text-green-600 hover:bg-accent"
                                    onClick={() => void onCommitEdit()}
                                    disabled={savingCell || mutating || !!browseResult?.fromCache}
                                    title="Apply edit"
                                  >
                                    Apply
                                  </button>
                                ) : null}
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
                      <TableCell className="sticky right-0 z-10 border px-2 py-1 bg-background">
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
            <Table className="text-sm" containerClassName="h-[calc(100vh-280px)] rounded-md border">
              <TableHeader>
                <TableRow className="sticky top-0 z-20 bg-background">
                  {(activeSchema()?.columns ?? []).map((col) => (
                    <TableHead key={col.name} className="border px-2 py-1 text-left">
                      {col.name}
                    </TableHead>
                  ))}
                  <TableHead className="sticky right-0 z-40 w-16 border px-2 py-1 text-left bg-background">act</TableHead>
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

        <div className="sticky bottom-0 z-20 flex items-center gap-2 rounded-md bg-muted/80 px-3 py-1.5 text-xs font-medium text-muted-foreground backdrop-blur">
          {browseResult
            ? browseTotal !== null
              ? `Total rows: ${browseTotal} (showing ${browseResult.rows.length}${browseResult.truncated ? ", truncated" : ""})`
              : `Total rows: ${browseResult.rows.length}`
            : "Total rows: -"}
        </div>
      </CardContent>
    </Card>
  )
}
