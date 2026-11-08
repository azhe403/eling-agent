"use client"

import { useCallback, useEffect, useRef, useState } from "react"
import type { DbConn, DbTab, QueryResult, SchemaInfo } from "../types"
import { API, parseApiError, quoteIdent, sqlQuote, buildWherePk } from "../utils/connection-helpers"
import { pkColumnNames } from "../utils/schema-helpers"
import type { NotifyDb } from "./useNotifications"

/** How long a cached read waits before asking the database again. */
const POLL_MS = 5000

/**
 * The rows of the table on screen.
 *
 * A poll and a click can cross in flight, so every request carries a number
 * and only the newest one paints: a late answer for a tab the reader has
 * left must not overwrite a fresher one.
 */
export function useTableData({
  notifyDb,
  conns,
  activeTableRef,
}: {
  notifyDb: NotifyDb
  conns: DbConn[]
  activeTable: DbTab | null
  activeTableRef: React.MutableRefObject<DbTab | null>
  selectedId: string | null
  selectedConn: DbConn | null | undefined
  selectedIsReadOnly: boolean
  schemaInfo: SchemaInfo | null
}) {
  const [browseTable, setBrowseTable] = useState<string | null>(null)
  const [browseResult, setBrowseResult] = useState<QueryResult | null>(null)
  const [browseTotal, setBrowseTotal] = useState<number | null>(null)
  const [browseLoading, setBrowseLoading] = useState(false)

  const browsePollRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const dataScrollRef = useRef<HTMLDivElement | null>(null)
  const browseInFlightRef = useRef(0)

  useEffect(() => () => { if (browsePollRef.current) clearTimeout(browsePollRef.current) }, [])

  const browseData = useCallback(
    async (id: string, table: string, database?: string | null, background = false) => {
      const request = ++browseInFlightRef.current
      // Guard against stale polls from a previous table selection - including
      // the database: a poll for db2 must not answer into a db3 tab.
      const current = activeTableRef.current
      if (
        current &&
        (current.connId !== id || current.table !== table || (current.database ?? null) !== (database ?? null))
      ) {
        return
      }
      // Preserve scroll position across refreshes so the table doesn't jump.
      const savedScroll = dataScrollRef.current?.scrollTop ?? 0
      const isNewTable = !background && browseTable !== table
      setBrowseTable(table)
      if (!background) setBrowseLoading(true)
      if (isNewTable) {
        if (browsePollRef.current) clearTimeout(browsePollRef.current)
        browsePollRef.current = null
        setBrowseResult(null)
        setBrowseTotal(null)
      }
      try {
        const conn = conns.find((c) => c.id === id)
        const quote = conn?.provider === "MySql" ? "`" : '"'
        const escaped = table.replace(new RegExp(quote, "g"), quote + quote)
        const selectSql = "SELECT * FROM " + quote + escaped + quote + " LIMIT 100"
        const [dataRes, countRes] = await Promise.all([
          fetch(API + "/" + id + "/query", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ sql: selectSql, maxRows: 100, database: database ?? null }),
          }),
          fetch(API + "/" + id + "/query", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              sql: "SELECT COUNT(*) FROM " + quote + escaped + quote,
              maxRows: 1,
              database: database ?? null,
            }),
          }),
        ])
        if (!dataRes.ok) throw new Error(parseApiError(await dataRes.text()))
        const data = (await dataRes.json()) as QueryResult
        // The poll and the click may have crossed in flight; a late answer for
        // a tab the reader has left must not overwrite a fresher one - but the
        // spinner it raised stays its own to clear.
        if (request === browseInFlightRef.current) setBrowseResult(data)
        if (countRes.ok) {
          const cd = (await countRes.json()) as QueryResult
          const v = cd?.rows?.[0]?.[0]
          if (v !== null && v !== undefined) {
            const n = typeof v === "number" ? v : Number(v)
            if (Number.isFinite(n)) setBrowseTotal(n)
          }
        }
        if (browsePollRef.current) clearTimeout(browsePollRef.current)
        if (request === browseInFlightRef.current) {
          // Cached rows are read again later; live ones are fresh.
          browsePollRef.current = data.fromCache
            // eslint-disable-next-line react-hooks/immutability -- browseData polls itself; the callback body runs long after the binding is initialized.
            ? setTimeout(() => { void browseData(id, table, database, true) }, POLL_MS)
            : null
        }
      } catch (e: unknown) {
        const msg = e instanceof Error ? e.message : "browse failed"
        const conn = conns.find((c) => c.id === id)
        notifyDb("error", `Could not load ${table}`, `${conn?.name ?? id}: ${msg}`)
      } finally {
        // Only the newest request clears the spinner it raised.
        if (!background && request === browseInFlightRef.current) setBrowseLoading(false)
        requestAnimationFrame(() => {
          if (dataScrollRef.current) dataScrollRef.current.scrollTop = savedScroll
        })
      }
    },
    [browseTable, conns, notifyDb, activeTableRef]
  )

  return {
    browseTable,
    setBrowseTable,
    browseResult,
    setBrowseResult,
    browseTotal,
    setBrowseTotal,
    browseLoading,
    dataScrollRef,
    browseData,
  }
}

/**
 * The changes a reader makes to a table: an edited cell, a new row, a
 * deleted one.
 *
 * The primary key decides what a row is, so a table without one cannot be
 * changed - the endpoint has no way to name the row it should touch. Edits
 * are painted before the server answers and put back when it refuses, so a
 * click does not wait for a round trip.
 */
export function useTableMutations({
  notifyDb,
  browseResult,
  setBrowseResult,
  schemaInfo,
  activeTable,
  selectedId,
  selectedConn,
  selectedIsReadOnly,
  refreshData,
}: {
  notifyDb: NotifyDb
  browseResult: QueryResult | null
  setBrowseResult: React.Dispatch<React.SetStateAction<QueryResult | null>>
  schemaInfo: SchemaInfo | null
  activeTable: DbTab | null
  selectedId: string | null
  selectedConn: DbConn | null | undefined
  selectedIsReadOnly: boolean
  refreshData: () => Promise<void>
}) {
  const [mutating, setMutating] = useState(false)
  const [savingCell, setSavingCell] = useState(false)
  const [editingCell, setEditingCell] = useState<{ row: number; col: number } | null>(null)
  const [editValue, setEditValue] = useState("")
  const [newRow, setNewRow] = useState<Record<string, string> | null>(null)
  const [deleteRowTarget, setDeleteRowTarget] = useState<number | null>(null)
  const [showReadOnlyDeleteDialog, setShowReadOnlyDeleteDialog] = useState(false)

  const pkNames = pkColumnNames(schemaInfo, activeTable)
  const activeTableSchema = schemaInfo?.tables?.find((t) => t.name === activeTable?.table) ?? null

  const startEdit = useCallback(
    (rowIndex: number, colIndex: number, value: unknown) => {
      if (pkNames.length === 0) return
      setEditingCell({ row: rowIndex, col: colIndex })
      setEditValue(String(value ?? ""))
    },
    [pkNames]
  )

  const cancelEdit = useCallback(() => setEditingCell(null), [])

  /** The WHERE clause that names one row, or null when it cannot be named. */
  const whereForRow = useCallback(
    (ri: number) => {
      if (!browseResult) return null
      return buildWherePk(browseResult.rows[ri], browseResult.columns, pkNames)
    },
    [browseResult, pkNames]
  )

  const sendMutate = useCallback(
    async (sql: string) => {
      if (!selectedId) throw new Error("no connection")
      const res = await fetch(API + "/" + selectedId + "/mutate", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sql, maxAffectedRows: 1000, database: activeTable?.database ?? null }),
      })
      if (!res.ok) throw new Error(parseApiError(await res.text()))
    },
    [selectedId, activeTable]
  )

  const patchCell = useCallback(
    (ri: number, ci: number, value: unknown) => {
      setBrowseResult((prev) => {
        if (!prev) return prev
        return {
          ...prev,
          rows: prev.rows.map((r, i) => (i === ri ? r.map((v, j) => (j === ci ? value : v)) : r)),
        }
      })
    },
    [setBrowseResult]
  )

  /** Writes one cell to the server and paints it at once. */
  const commitEdit = useCallback(
    async (overrideValue?: string) => {
      if (!editingCell || !browseResult || !selectedId || !activeTable) return
      if (selectedIsReadOnly) { setEditingCell(null); return }
      const { row: ri, col: ci } = editingCell
      const colName = browseResult.columns[ci]
      // editing a primary key is not allowed
      if (pkNames.includes(colName)) { setEditingCell(null); return }
      const where = whereForRow(ri)
      if (!where) { setEditingCell(null); return }
      const val = overrideValue !== undefined ? overrideValue : editValue
      const sqlStmt = `UPDATE ${quoteIdent(activeTable.table, selectedConn)} SET ${quoteIdent(colName, selectedConn)} = ${sqlQuote(val)} WHERE ${where}`
      const prevValue = browseResult.rows[ri][ci]
      // optimistic apply
      patchCell(ri, ci, val)
      setEditingCell(null)
      setSavingCell(true)
      try {
        await sendMutate(sqlStmt)
        await refreshData()
      } catch {
        // revert
        patchCell(ri, ci, prevValue)
        notifyDb("error", "The change could not be saved.", "The server refused the update.")
      } finally {
        setSavingCell(false)
      }
    },
    [
      editingCell, browseResult, selectedId, activeTable, selectedIsReadOnly, editValue,
      selectedConn, refreshData, pkNames, patchCell, sendMutate, whereForRow, notifyDb,
    ]
  )

  /**
   * Sets one cell to NULL, to empty, or to now (for date and time columns)
   * without opening the editor. The row's primary key names the row.
   */
  const setCellValue = useCallback(
    async (rowIndex: number, colIndex: number, value: null | "" | "now") => {
      if (!browseResult || !selectedId || !activeTable) return
      if (selectedIsReadOnly) {
        notifyDb("error", "This connection is read-only, so data changes are disabled.")
        return
      }
      if (browseResult.fromCache) {
        notifyDb("error", "This data is cached (database unreachable), so edits are disabled.")
        return
      }
      const colName = browseResult.columns[colIndex]
      if (pkNames.includes(colName)) {
        notifyDb("error", "Primary key columns cannot be changed here.")
        return
      }
      const colSchema = activeTableSchema?.columns?.find((c) => c.name === colName)
      if (value === null && colSchema && !colSchema.isNullable) {
        notifyDb("error", `Column ${colName} is NOT NULL.`)
        return
      }
      const where = whereForRow(rowIndex)
      if (!where) { notifyDb("error", "no primary key — cannot update"); return }

      // Build the SQL literal plus the value shown optimistically in the grid.
      let literal: string
      let display: unknown
      if (value === null) {
        literal = "NULL"
        display = null
      } else if (value === "") {
        literal = "''"
        display = ""
      } else {
        const now = new Date()
        const pad = (n: number) => String(n).padStart(2, "0")
        const datePart = `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`
        const timePart = `${pad(now.getHours())}:${pad(now.getMinutes())}:${pad(now.getSeconds())}`
        const withTime = (colSchema?.dataType ?? "").toLowerCase().includes("time")
        display = withTime ? `${datePart}T${timePart}` : datePart
        literal = sqlQuote(display)
      }

      const sqlStmt = `UPDATE ${quoteIdent(activeTable.table, selectedConn)} SET ${quoteIdent(colName, selectedConn)} = ${literal} WHERE ${where}`
      // optimistic apply
      patchCell(rowIndex, colIndex, display)
      setMutating(true)
      try {
        await sendMutate(sqlStmt)
        await refreshData()
        notifyDb(
          "success",
          value === null ? `Set ${colName} to null` : value === "" ? `Cleared ${colName}` : `Set ${colName} to now`
        )
      } catch {
        await refreshData()
        notifyDb("error", "The change could not be saved.", "The server refused the update.")
      } finally {
        setMutating(false)
      }
    },
    [
      browseResult, selectedId, activeTable, selectedIsReadOnly, pkNames, activeTableSchema,
      selectedConn, refreshData, notifyDb, patchCell, sendMutate, whereForRow,
    ]
  )

  /** Asks for a row to be deleted; the dialog confirms it. */
  const promptDeleteRow = useCallback(
    (rowIndex: number) => {
      if (selectedIsReadOnly) {
        setShowReadOnlyDeleteDialog(true)
        return
      }
      if (browseResult?.fromCache) return
      if (!browseResult || !selectedId || !activeTable) return
      const where = whereForRow(rowIndex)
      if (!where) { notifyDb("error", "no primary key — cannot delete"); return }
      setDeleteRowTarget(rowIndex)
    },
    [selectedIsReadOnly, browseResult, selectedId, activeTable, notifyDb, whereForRow]
  )

  const confirmDeleteRow = useCallback(async () => {
    if (deleteRowTarget === null || !browseResult || !selectedId || !activeTable) return
    if (selectedIsReadOnly) { setDeleteRowTarget(null); return }
    const rowIndex = deleteRowTarget
    const where = whereForRow(rowIndex)
    if (!where) {
      notifyDb("error", "no primary key — cannot delete")
      setDeleteRowTarget(null)
      return
    }
    // optimistic removal
    setBrowseResult((prev) => {
      if (!prev) return prev
      return { ...prev, rows: prev.rows.filter((_, i) => i !== rowIndex) }
    })
    setDeleteRowTarget(null)
    setMutating(true)
    try {
      const sqlStmt = `DELETE FROM ${quoteIdent(activeTable.table, selectedConn)} WHERE ${where}`
      const res = await fetch(API + "/" + selectedId + "/mutate", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sql: sqlStmt, maxAffectedRows: 1000, database: activeTable?.database ?? null }),
      })
      if (!res.ok) throw new Error(parseApiError(await res.text()))
      const data = await res.json().catch(() => ({ affectedRows: 0, truncated: false }))
      await refreshData()
      if ((data?.affectedRows ?? 0) === 0) {
        notifyDb("error", "Nothing was deleted.", "No row matched.")
      } else {
        notifyDb("success", `Deleted ${data.affectedRows} row${data.affectedRows === 1 ? "" : "s"}`)
      }
    } catch {
      await refreshData()
      notifyDb("error", "The row could not be deleted.", "The server refused the delete.")
    } finally {
      setMutating(false)
      setDeleteRowTarget(null)
    }
  }, [
    deleteRowTarget, browseResult, selectedId, activeTable, selectedIsReadOnly, selectedConn,
    refreshData, notifyDb, whereForRow, setBrowseResult,
  ])

  const submitNewRow = useCallback(async () => {
    if (!newRow || !browseResult || !selectedId || !activeTable) return
    if (selectedIsReadOnly) return
    const cols = browseResult.columns
    const colList = cols.map((c) => quoteIdent(c, selectedConn)).join(", ")
    const valList = cols.map((c) => {
      const v = newRow[c] ?? ""
      if (pkNames.includes(c) && v === "") return "NULL"
      return sqlQuote(v)
    }).join(", ")
    const sqlStmt = `INSERT INTO ${quoteIdent(activeTable.table, selectedConn)} (${colList}) VALUES (${valList})`
    const optimisticRow = cols.map((c) => (newRow[c] ?? ""))
    // optimistic apply
    setBrowseResult((prev) => (prev ? { ...prev, rows: [...prev.rows, optimisticRow] } : prev))
    setMutating(true)
    try {
      await sendMutate(sqlStmt)
      setNewRow(null)
      await refreshData()
    } catch {
      // revert
      setBrowseResult((prev) => (prev ? { ...prev, rows: prev.rows.slice(0, -1) } : prev))
      notifyDb("error", "The row could not be added.", "The server refused the insert.")
    } finally {
      setMutating(false)
    }
  }, [
    newRow, browseResult, selectedId, activeTable, selectedIsReadOnly, selectedConn,
    refreshData, pkNames, setNewRow, setBrowseResult, sendMutate, notifyDb,
  ])

  return {
    pkNames,
    activeTableSchema,
    mutating,
    savingCell,
    editingCell,
    setEditingCell,
    editValue,
    setEditValue,
    newRow,
    setNewRow,
    deleteRowTarget,
    setDeleteRowTarget,
    showReadOnlyDeleteDialog,
    setShowReadOnlyDeleteDialog,
    startEdit,
    cancelEdit,
    commitEdit,
    setCellValue,
    promptDeleteRow,
    confirmDeleteRow,
    submitNewRow,
  }
}
