/**
 * Schema helpers: finding primary keys, the active table, date columns.
 */

import type { ColumnSchema, SchemaInfo, TableSchema } from "../types"

/** The schema of the table that is on screen, or null. */
export const activeSchemaTable = (
  schemaInfo: SchemaInfo | null,
  activeTable: { table?: string } | null
): TableSchema | null => {
  if (!activeTable?.table) return null
  return schemaInfo?.tables?.find((t) => t.name === activeTable.table) ?? null
}

/** The primary key column names of the table that is on screen. */
export const pkColumnNames = (
  schemaInfo: SchemaInfo | null,
  activeTable: { table?: string } | null
): string[] =>
  activeSchemaTable(schemaInfo, activeTable)?.columns?.filter((c) => c.isPrimaryKey).map((c) => c.name) ?? []

/** The schema of one column of the table that is on screen, or null. */
export const activeColumnSchema = (
  schemaInfo: SchemaInfo | null,
  activeTable: { table?: string } | null,
  colName: string
): ColumnSchema | null =>
  activeSchemaTable(schemaInfo, activeTable)?.columns?.find((c) => c.name === colName) ?? null

/**
 * True when a column's declared type mentions date or time.
 *
 * The schema drives the date picker in the grid, so the context menu follows
 * the same rule for the "Set now" action.
 */
export const isDateTimeColumn = (
  schemaInfo: SchemaInfo | null,
  activeTable: { table?: string } | null,
  colName: string
): boolean => {
  const dt = activeColumnSchema(schemaInfo, activeTable, colName)?.dataType?.toLowerCase() ?? ""
  return dt.includes("date") || dt.includes("time")
}

/** True when a column is named "id", whatever its case. */
export const isIdColumn = (colName: string): boolean => /^id$/i.test(colName)

/** True when a column's declared type holds integers. */
export const isIntegerType = (dataType: string): boolean =>
  /int|serial|bigint|smallint/.test(dataType.toLowerCase())
