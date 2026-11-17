/**
 * The wire model for the Data tab's condition builder, and the pure helpers
 * the panel and the grid header use to edit it.
 *
 * Nothing here touches the network or the DOM: same input, same output. The
 * values are exactly what the backend expects to receive at
 * `POST /api/databases/{id}/browse` - including the `order` property name
 * (never `direction`) and the `first|and|or` logic vocabulary.
 */

export type BrowseOp =
  | "eq"
  | "neq"
  | "contains"
  | "notContains"
  | "startsWith"
  | "endsWith"
  | "gt"
  | "gte"
  | "lt"
  | "lte"
  | "isNull"
  | "isNotNull"

/** `first` on the first enabled row; `and`/`or` everywhere after. */
export type BrowseLogic = "first" | "and" | "or"

/** Which operator list a column gets. Type resolution uses the first word only (e.g. "double precision" → "double"). */
export type ColumnKind = "text" | "numeric" | "datetime" | "boolean"

export interface BrowseFilter {
  column: string
  op: BrowseOp
  value: string
  enabled: boolean
  logic: BrowseLogic
}

export interface BrowseSort {
  column: string
  order: "asc" | "desc"
}

/** The `/browse` payload. Mirrors the backend record, camelCase end to end. */
export interface BrowseResponse {
  columns: string[]
  rows: unknown[][]
  truncated: boolean
  total: number | null
  fromCache?: boolean
  cachedAt?: string | null
}

const firstWord = (dataType: string): string => {
  const t = (dataType ?? "").toLowerCase().trim()
  const cut = t.search(/[\s(]/)
  return cut === -1 ? t : t.slice(0, cut)
}

const DATETIME_TYPES = new Set(["date", "datetime", "timestamp", "time", "year"])
const BOOLEAN_TYPES = new Set(["tinyint", "bool", "boolean", "bit"])
const NUMERIC_TYPES = new Set(["int", "integer", "smallint", "mediumint", "bigint", "decimal", "numeric", "float", "double", "real"])

/** Which operator list a column gets. Token-matched on the first word; anything unrecognized is text. */
export const columnKind = (dataType: string): ColumnKind => {
  const t = firstWord(dataType)
  if (DATETIME_TYPES.has(t)) return "datetime"
  if (BOOLEAN_TYPES.has(t)) return "boolean"
  if (NUMERIC_TYPES.has(t)) return "numeric"
  return "text"
}

export interface OpOption {
  value: BrowseOp
  label: string
}

const IS_NULL: OpOption = { value: "isNull", label: "Is null" }
const IS_NOT_NULL: OpOption = { value: "isNotNull", label: "Is not null" }

const BOOLEAN_OPS: OpOption[] = [
  { value: "eq", label: "Equals" },
  { value: "neq", label: "Not equals" },
  IS_NULL,
  IS_NOT_NULL,
]

const TEXT_OPS: OpOption[] = [
  { value: "contains", label: "Contains" },
  { value: "notContains", label: "Not contains" },
  { value: "startsWith", label: "Starts with" },
  { value: "endsWith", label: "Ends with" },
  { value: "eq", label: "Equals" },
  { value: "neq", label: "Not equals" },
  IS_NULL,
  IS_NOT_NULL,
]

const NUMERIC_OPS: OpOption[] = [
  { value: "eq", label: "=" },
  { value: "neq", label: "≠" },
  { value: "gt", label: ">" },
  { value: "gte", label: "≥" },
  { value: "lt", label: "<" },
  { value: "lte", label: "≤" },
  IS_NULL,
  IS_NOT_NULL,
]

const DATETIME_OPS: OpOption[] = [
  { value: "eq", label: "On" },
  { value: "neq", label: "Not on" },
  { value: "gt", label: "After" },
  { value: "gte", label: "On or after" },
  { value: "lt", label: "Before" },
  { value: "lte", label: "On or before" },
  IS_NULL,
  IS_NOT_NULL,
]

/** The operators a column of the given kind offers. Every list ends with isNull/isNotNull. */
export const opsFor = (kind: ColumnKind): OpOption[] => {
  switch (kind) {
    case "boolean":
      return BOOLEAN_OPS
    case "numeric":
      return NUMERIC_OPS
    case "datetime":
      return DATETIME_OPS
    default:
      return TEXT_OPS
  }
}

/** Cycle a sort direction on click: asc -> desc -> none. */
export const cycleSort = (current: "asc" | "desc" | null | undefined): "asc" | "desc" | null => {
  if (current === "asc") return "desc"
  if (current === "desc") return null
  return "asc"
}

/**
 * Header-click sort as a single-column sort: unsorted column gets asc, an
 * ascending column gets desc, a descending column is unsorted - and whatever
 * the panel had sorted is replaced either way. Multi-column sort lives only
 * in the panel.
 */
export const toggleSortColumn = (sort: BrowseSort[], column: string): BrowseSort[] => {
  const current = sort.find((s) => s.column === column)
  const next = cycleSort(current?.order ?? null)
  if (next === null) return []
  return [{ column, order: next }]
}

/** How many committed things are active: enabled conditions + a non-blank manual condition. */
export const activeCount = (filters: BrowseFilter[], manualCondition: string | null | undefined): number =>
  filters.filter((f) => f.enabled).length + (manualCondition?.trim() ? 1 : 0)

/** The `/browse` request body, keys in wire order. */
export const serializeBrowse = (
  table: string,
  database: string | null | undefined,
  page: number,
  limit: number,
  filters: BrowseFilter[],
  sort: BrowseSort[],
  manualCondition: string | null | undefined,
) => ({
  table,
  database: database ?? null,
  page,
  limit,
  filters,
  sort,
  manualCondition: manualCondition ?? null,
})
