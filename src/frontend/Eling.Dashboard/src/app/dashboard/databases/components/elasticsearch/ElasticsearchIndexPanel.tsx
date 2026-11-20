"use client"

/**
 * The Elasticsearch index panel: the Discover view of one index.
 *
 * Three tabs - Discover (filter builder + Lucene + histogram + documents grid),
 * Mapping (raw mappings/settings) and Query (raw DSL console). Everything is
 * read-only: the panel fetches through `es-client.ts` and never writes.
 *
 * The panel keeps its own state and fetches on its own; the page only hands
 * it the tab identity and the connection list. It unmounts when the tab
 * closes, so a fresh mount is a fresh read - the same shape as RedisBrowser.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react"
import type {
  DbConn,
  ElasticsearchDocumentPage,
  ElasticsearchHistogram,
  ElasticsearchIndexDetail,
  ElasticsearchSearchResult,
} from "@/app/dashboard/databases/types"
import {
  fetchEsDocuments,
  fetchEsHistogram,
  fetchEsIndex,
  fetchEsSearch,
} from "@/app/dashboard/databases/es-client"
import { esTabKey } from "@/app/dashboard/databases/es-browser"

/** The panel identity: one connection, one index. */
export interface ElasticsearchIndexPanelProps {
  conns: DbConn[]
  connId: string
  index: string
  isWide: boolean
}

type PanelTab = "discover" | "mapping" | "query"
type FilterMode = "filter" | "lucene"

/** One row of the filter builder. */
interface FilterRow {
  field: string
  op: FilterOp
  value: string
}

type FilterOp = "is" | "contains" | "gt" | "gte" | "lt" | "lte" | "exists"

const FILTER_OPS: Array<{ value: FilterOp; label: string }> = [
  { value: "is", label: "is" },
  { value: "contains", label: "contains" },
  { value: "gt", label: ">" },
  { value: "gte", label: ">=" },
  { value: "lt", label: "<" },
  { value: "lte", label: "<=" },
  { value: "exists", label: "exists" },
]

const RANGE_OPS: ReadonlySet<FilterOp> = new Set(["gt", "gte", "lt", "lte"])

/** Time-range presets the Discover bar offers. */
type RangePreset = "15m" | "1h" | "24h" | "7d" | "all"

const RANGE_PRESETS: Array<{ value: RangePreset; label: string; ms: number | null }> = [
  { value: "15m", label: "15m", ms: 15 * 60 * 1000 },
  { value: "1h", label: "1h", ms: 60 * 60 * 1000 },
  { value: "24h", label: "24h", ms: 24 * 60 * 60 * 1000 },
  { value: "7d", label: "7d", ms: 7 * 24 * 60 * 60 * 1000 },
  { value: "all", label: "all time", ms: null },
]

const PAGE_SIZE = 25

/** The storage key for one panel's state: mode, filters, time range, selected fields. */
export const panelStateKey = (connId: string, index: string) => `eling-es-panel:${esTabKey(connId, index)}`

interface PanelState {
  mode?: FilterMode
  rows?: FilterRow[]
  logic?: "and" | "or"
  preset?: RangePreset
  dateField?: string
  selectedFields?: string[] | null
  luceneQuery?: string
}

/** Read the persisted panel state, tolerating anything malformed. */
const readPanelState = (connId: string, index: string): PanelState => {
  try {
    const raw = window.localStorage.getItem(panelStateKey(connId, index))
    if (!raw) return {}
    const parsed: unknown = JSON.parse(raw)
    return typeof parsed === "object" && parsed !== null ? (parsed as PanelState) : {}
  } catch {
    return {}
  }
}

/** Write the panel state back; failures (private mode) are silent. */
const writePanelState = (connId: string, index: string, state: PanelState) => {
  try {
    window.localStorage.setItem(panelStateKey(connId, index), JSON.stringify(state))
  } catch {
    // Private mode or a full quota: the panel keeps working without persistence.
  }
}

/** Human-readable byte count for the Mapping header. */
export const formatBytes = (bytes: number): string => {
  if (!Number.isFinite(bytes) || bytes < 0) return "0 B"
  const units = ["B", "KB", "MB", "GB", "TB"]
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value >= 100 || unit === 0 ? Math.round(value) : value.toFixed(1)} ${units[unit]}`
}

/** The date-typed fields of a mapping's top-level properties, in declaration order. */
export const dateFieldsOf = (mappings: unknown): string[] => {
  const properties = (mappings as { properties?: Record<string, unknown> } | null)?.properties
  if (!properties || typeof properties !== "object") return []
  return Object.entries(properties)
    .filter(([, value]) => (value as { type?: string } | null)?.type === "date")
    .map(([name]) => name)
}

/** The top-level property names of a mapping, in declaration order. */
const propertyNamesOf = (mappings: unknown): string[] => {
  const properties = (mappings as { properties?: Record<string, unknown> } | null)?.properties
  if (!properties || typeof properties !== "object") return []
  return Object.keys(properties)
}

/** The scalar-ish top-level fields a column picker defaults to. */
export const scalarFieldNames = (mappings: unknown): string[] => {
  const properties = (mappings as { properties?: Record<string, unknown> } | null)?.properties
  if (!properties || typeof properties !== "object") return []
  const scalar = new Set(["keyword", "text", "boolean", "long", "integer", "short", "byte", "double", "float", "half_float", "date", "ip"])
  return Object.entries(properties)
    .filter(([, value]) => scalar.has((value as { type?: string } | null)?.type ?? ""))
    .map(([name]) => name)
}

/** One filter row as the API expects it, or null when the row is incomplete. */
const rowToFilter = (row: FilterRow): { field: string; op: FilterOp; value?: string } | null => {
  if (!row.field) return null
  if (row.op === "exists") return { field: row.field, op: row.op }
  if (!row.value) return null
  return { field: row.field, op: row.op, value: row.value }
}

/** The `filters` query-string payload from the rows, or null when none apply. */
export const filtersPayload = (rows: FilterRow[]): string | null => {
  const filters = rows.map(rowToFilter).filter((f): f is { field: string; op: FilterOp; value?: string } => f !== null)
  return filters.length > 0 ? JSON.stringify(filters) : null
}

/** The `[start, end]` of a preset, as ISO-8601 strings, or nulls for all time. */
export const presetRange = (preset: RangePreset, now: number): { start: string | null; end: string | null } => {
  const match = RANGE_PRESETS.find((p) => p.value === preset)
  if (!match || match.ms === null) return { start: null, end: null }
  return { start: new Date(now - match.ms).toISOString(), end: new Date(now).toISOString() }
}

const cellText = (value: unknown): string => {
  if (value === null || value === undefined) return ""
  if (typeof value === "string") return value
  return JSON.stringify(value)
}

export const ElasticsearchIndexPanel = (_props: ElasticsearchIndexPanelProps) => {
  const { conns, connId, index } = _props
  const connName = useMemo(() => conns.find((c) => c.id === connId)?.name ?? "", [conns, connId])

  const [tab, setTab] = useState<PanelTab>("discover")
  const [mode, setMode] = useState<FilterMode>("lucene")
  const [rows, setRows] = useState<FilterRow[]>([])
  const [logic, setLogic] = useState<"and" | "or">("and")
  const [luceneQuery, setLuceneQuery] = useState("")
  const [preset, setPreset] = useState<RangePreset>("all")
  const [dateField, setDateField] = useState<string>("@timestamp")
  /** A bucket the reader clicked: the range narrows to it until cleared. */
  const [zoom, setZoom] = useState<{ start: string; end: string } | null>(null)

  const [detail, setDetail] = useState<ElasticsearchIndexDetail | null>(null)
  const [detailError, setDetailError] = useState<{ status: number; message: string } | null>(null)
  const [histogram, setHistogram] = useState<ElasticsearchHistogram | null>(null)
  const [histogramError, setHistogramError] = useState<{ status: number; message: string } | null>(null)
  const [documents, setDocuments] = useState<ElasticsearchDocumentPage | null>(null)
  const [documentsError, setDocumentsError] = useState<{ status: number; message: string } | null>(null)
  const [from, setFrom] = useState(0)
  const [loading, setLoading] = useState(false)

  const [selectedFields, setSelectedFields] = useState<string[] | null>(null)
  const [pickerOpen, setPickerOpen] = useState(false)
  const [expandedHit, setExpandedHit] = useState<number | null>(null)

  const [dsl, setDsl] = useState("")
  const [dslSize, setDslSize] = useState(25)
  const [dslAggs, setDslAggs] = useState("")
  const [dslResult, setDslResult] = useState<ElasticsearchSearchResult | null>(null)
  const [dslError, setDslError] = useState<{ status: number; message: string } | null>(null)
  const [dslLoading, setDslLoading] = useState(false)

  // Restore the persisted panel state once per mount.
  //
  // Read inside an effect (localStorage is not available during SSR) and
  // seeded through the `restore` callback so the setState calls happen in a
  // callback, not synchronously in the effect body.
  const restored = useRef(false)
  useEffect(() => {
    if (restored.current) return
    restored.current = true
    const saved = readPanelState(connId, index)
    const restore = () => {
      if (saved.mode === "filter" || saved.mode === "lucene") setMode(saved.mode)
      if (Array.isArray(saved.rows)) {
        setRows(
          saved.rows
            .filter((r): r is FilterRow => typeof r?.field === "string" && typeof r?.op === "string")
            .map((r) => ({ field: r.field, op: r.op as FilterOp, value: typeof r.value === "string" ? r.value : "" })),
        )
      }
      if (saved.logic === "and" || saved.logic === "or") setLogic(saved.logic)
      if (saved.preset) setPreset(saved.preset)
      if (saved.luceneQuery) setLuceneQuery(saved.luceneQuery)
      if (Array.isArray(saved.selectedFields)) setSelectedFields(saved.selectedFields)
    }
    restore()
  }, [connId, index])

  // Persist the choices the reader makes.
  useEffect(() => {
    writePanelState(connId, index, { mode, rows, logic, preset, luceneQuery, selectedFields })
  }, [connId, index, mode, rows, logic, preset, luceneQuery, selectedFields])

  // The mapping answers two questions at once: the date fields for the time
  // selector, and the property list for the column picker.
  useEffect(() => {
    let alive = true
    fetchEsIndex(connId, index)
      .then((d) => {
        if (!alive) return
        setDetail(d)
        setDetailError(null)
        const dates = dateFieldsOf(d.mappings)
        if (dates.length > 0) setDateField(dates[0])
      })
      .catch((e: { status: number; message: string }) => {
        if (!alive) return
        setDetailError(e)
      })
    return () => {
      alive = false
    }
  }, [connId, index])

  // The query envelope both the documents grid and the histogram share.
  //
  // A clicked bucket wins over the preset until it is cleared: the reader
  // picked a slice of time, and the grid below must show that slice.
  const envelope = useCallback(() => {
    const range = zoom ?? presetRange(preset, Date.now())
    return mode === "filter"
      ? { q: null, filters: filtersPayload(rows), logic: rows.length > 0 ? logic : null, start: range.start, end: range.end, field: dateField }
      : { q: luceneQuery.trim() ? luceneQuery.trim() : null, filters: null, logic: null, start: range.start, end: range.end, field: dateField }
  }, [mode, rows, logic, luceneQuery, preset, dateField, zoom])

  // One refresh of the histogram and the first page of documents.
  const refresh = useCallback(async () => {
    const env = envelope()
    setLoading(true)
    const results = await Promise.allSettled([
      fetchEsHistogram(connId, index, { buckets: 60, q: env.q, start: env.start, end: env.end, field: env.field, filters: env.filters, logic: env.logic }),
      fetchEsDocuments(connId, index, { from: 0, size: PAGE_SIZE, q: env.q, start: env.start, end: env.end, field: env.field, filters: env.filters, logic: env.logic }),
    ])
    const [histogramResult, documentsResult] = results
    if (histogramResult.status === "fulfilled") {
      setHistogram(histogramResult.value)
      setHistogramError(null)
    } else {
      setHistogramError(histogramResult.reason)
    }
    if (documentsResult.status === "fulfilled") {
      setDocuments(documentsResult.value)
      setDocumentsError(null)
    } else {
      setDocumentsError(documentsResult.reason)
    }
    setLoading(false)
    setFrom(0)
  }, [connId, index, envelope])

  // Fetch on mount and whenever the envelope changes. The fetch itself is
  // async, so every setState lands in a promise callback, not in the effect.
  useEffect(() => {
    let alive = true
    const run = async () => {
      await refresh()
      if (!alive) return
    }
    void run()
    return () => {
      alive = false
    }
  }, [refresh])

  // Paging: re-fetch with a new offset.
  const goToPage = useCallback(
    async (nextFrom: number) => {
      const env = envelope()
      setLoading(true)
      try {
        const page = await fetchEsDocuments(connId, index, {
          from: nextFrom,
          size: PAGE_SIZE,
          q: env.q,
          start: env.start,
          end: env.end,
          field: env.field,
          filters: env.filters,
          logic: env.logic,
        })
        setDocuments(page)
        setDocumentsError(null)
        setFrom(nextFrom)
      } catch (e) {
        setDocumentsError(e as { status: number; message: string })
      } finally {
        setLoading(false)
      }
    },
    [connId, index, envelope],
  )

  // The DSL console: parse the JSON textareas and run the search.
  const runDsl = useCallback(async () => {
    const parse = (text: string): unknown => {
      const trimmed = text.trim()
      if (!trimmed) return undefined
      return JSON.parse(trimmed)
    }
    try {
      const query = parse(dsl)
      const aggs = parse(dslAggs)
      setDslLoading(true)
      setDslError(null)
      const result = await fetchEsSearch(connId, index, { size: dslSize, query, aggs })
      setDslResult(result)
    } catch (e) {
      if (e instanceof SyntaxError) {
        setDslError({ status: 0, message: `Invalid JSON: ${e.message}` })
      } else {
        setDslError(e as { status: number; message: string })
      }
    } finally {
      setDslLoading(false)
    }
  }, [connId, index, dsl, dslAggs, dslSize])

  const dateFields = useMemo(() => dateFieldsOf(detail?.mappings), [detail])
  const propertyNames = useMemo(() => propertyNamesOf(detail?.mappings), [detail])
  const columns = useMemo(() => {
    const chosen = selectedFields && selectedFields.length > 0 ? selectedFields : scalarFieldNames(detail?.mappings).slice(0, 6)
    return chosen
  }, [detail, selectedFields])

  const total = documents?.total ?? 0
  const pageCount = Math.max(1, Math.ceil(total / PAGE_SIZE))

  return (
    <div className="flex h-full min-h-0 flex-col gap-2">
      <div className="flex items-center gap-2 border-b pb-2">
        <span className="text-sm font-medium">{index}</span>
        <span className="text-xs text-muted-foreground">{connName}</span>
        <div className="ml-auto flex items-center gap-1">
          {(["discover", "mapping", "query"] as PanelTab[]).map((t) => (
            <button
              key={t}
              type="button"
              onClick={() => setTab(t)}
              className={`rounded px-2 py-1 text-xs capitalize ${tab === t ? "bg-muted font-medium" : "text-muted-foreground"}`}
            >
              {t}
            </button>
          ))}
        </div>
      </div>

      {detailError ? <p className="text-sm text-destructive">{detailError.message}</p> : null}

      {tab === "discover" ? (
        <div className="flex min-h-0 flex-1 flex-col gap-2">
          {/* Mode toggle + time range. */}
          <div className="flex flex-wrap items-center gap-2">
            <div className="flex overflow-hidden rounded border text-xs">
              <button
                type="button"
                onClick={() => setMode("filter")}
                className={`px-2 py-1 ${mode === "filter" ? "bg-muted font-medium" : "text-muted-foreground"}`}
              >
                Filter
              </button>
              <button
                type="button"
                onClick={() => setMode("lucene")}
                className={`px-2 py-1 ${mode === "lucene" ? "bg-muted font-medium" : "text-muted-foreground"}`}
              >
                Lucene
              </button>
            </div>

            {dateFields.length > 0 ? (
              <select
                aria-label="Time range"
                className="rounded border bg-transparent px-2 py-1 text-xs"
                value={preset}
                onChange={(e) => setPreset(e.target.value as RangePreset)}
              >
                {RANGE_PRESETS.map((p) => (
                  <option key={p.value} value={p.value}>
                    {p.label}
                  </option>
                ))}
              </select>
            ) : null}

            {dateFields.length > 1 ? (
              <select
                aria-label="Date field"
                className="rounded border bg-transparent px-2 py-1 text-xs"
                value={dateField}
                onChange={(e) => setDateField(e.target.value)}
              >
                {dateFields.map((f) => (
                  <option key={f} value={f}>
                    {f}
                  </option>
                ))}
              </select>
            ) : null}

            <button
              type="button"
              onClick={() => {
                setZoom(null)
                void refresh()
              }}
              className="ml-auto rounded border px-2 py-1 text-xs"
              disabled={loading}
            >
              {loading ? "Loading…" : "Refresh"}
            </button>
            {zoom ? (
              <button
                type="button"
                onClick={() => setZoom(null)}
                className="rounded border px-2 py-1 text-xs"
                title={zoom.start}
              >
                zoomed ✕
              </button>
            ) : null}
          </div>

          {/* The query builder itself. */}
          {mode === "filter" ? (
            <div className="flex flex-col gap-1 rounded border p-2">
              <div className="flex items-center gap-2">
                <span className="text-xs text-muted-foreground">Match</span>
                <div className="flex overflow-hidden rounded border text-xs">
                  <button
                    type="button"
                    onClick={() => setLogic("and")}
                    className={`px-2 py-0.5 ${logic === "and" ? "bg-muted font-medium" : "text-muted-foreground"}`}
                  >
                    AND
                  </button>
                  <button
                    type="button"
                    onClick={() => setLogic("or")}
                    className={`px-2 py-0.5 ${logic === "or" ? "bg-muted font-medium" : "text-muted-foreground"}`}
                  >
                    OR
                  </button>
                </div>
                <button
                  type="button"
                  onClick={() => setRows((r) => [...r, { field: propertyNames[0] ?? "", op: "is", value: "" }])}
                  className="ml-auto rounded border px-2 py-0.5 text-xs"
                >
                  + add filter
                </button>
              </div>
              {rows.map((row, i) => (
                <div key={i} className="flex flex-wrap items-center gap-1 text-xs">
                  <select
                    aria-label={`Field ${i + 1}`}
                    className="min-w-0 flex-1 rounded border bg-transparent px-1 py-1"
                    value={row.field}
                    onChange={(e) => setRows((rs) => rs.map((r, j) => (j === i ? { ...r, field: e.target.value } : r)))}
                  >
                    {propertyNames.map((name) => (
                      <option key={name} value={name}>
                        {name}
                      </option>
                    ))}
                  </select>
                  <select
                    aria-label={`Operator ${i + 1}`}
                    className="rounded border bg-transparent px-1 py-1"
                    value={row.op}
                    onChange={(e) =>
                      setRows((rs) => rs.map((r, j) => (j === i ? { ...r, op: e.target.value as FilterOp } : r)))
                    }
                  >
                    {FILTER_OPS.map((op) => (
                      <option key={op.value} value={op.value}>
                        {op.label}
                      </option>
                    ))}
                  </select>
                  {row.op !== "exists" ? (
                    <input
                      aria-label={`Value ${i + 1}`}
                      className="min-w-0 flex-1 rounded border bg-transparent px-1 py-1"
                      placeholder={RANGE_OPS.has(row.op) ? "ISO-8601 or number" : "value"}
                      value={row.value}
                      onChange={(e) => setRows((rs) => rs.map((r, j) => (j === i ? { ...r, value: e.target.value } : r)))}
                    />
                  ) : null}
                  <button
                    type="button"
                    aria-label={`Remove filter ${i + 1}`}
                    onClick={() => setRows((rs) => rs.filter((_, j) => j !== i))}
                    className="rounded border px-1.5 py-1"
                  >
                    ×
                  </button>
                </div>
              ))}
            </div>
          ) : (
            <div className="flex items-center gap-2">
              <input
                aria-label="Query"
                className="min-w-0 flex-1 rounded border bg-transparent px-2 py-1 font-mono text-xs"
                placeholder="Lucene query_string syntax — e.g. level:error AND message:timeout"
                value={luceneQuery}
                onChange={(e) => setLuceneQuery(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === "Enter") void refresh()
                }}
              />
            </div>
          )}

          {/* Histogram. */}
          <div className="rounded border p-1">
            {histogramError ? <p className="text-xs text-destructive">{histogramError.message}</p> : null}
            {!histogramError && (!histogram || histogram.buckets.length === 0) ? (
              <p className="text-xs text-muted-foreground">no hits in range</p>
            ) : null}
            {histogram && histogram.buckets.length > 0 ? (
              <div className="flex h-16 items-end gap-px" role="img" aria-label="Hit histogram">
                {histogram.buckets.map((bucket, bi) => {
                  const max = Math.max(1, ...histogram.buckets.map((b) => b.docCount))
                  const height = Math.round((bucket.docCount / max) * 100)
                  // A click narrows the time range to this bucket: from its own
                  // start to the next bucket's start, or an hour past it for
                  // the last bucket (the cluster picked the width, so the tail
                  // is an estimate - the grid is still the truth).
                  const next = histogram.buckets[bi + 1]
                  const endMs = next ? next.keyMs : bucket.keyMs + 60 * 60 * 1000
                  return (
                    <div
                      key={bucket.keyMs}
                      title={`${new Date(bucket.keyMs).toISOString()} — ${bucket.docCount} hits`}
                      className="min-w-0 flex-1 cursor-pointer rounded-sm bg-primary/60 hover:bg-primary"
                      style={{ height: `${Math.max(2, height)}%` }}
                      onClick={() => {
                        setZoom({
                          start: new Date(bucket.keyMs).toISOString(),
                          end: new Date(endMs).toISOString(),
                        })
                      }}
                    />
                  )
                })}
              </div>
            ) : null}
          </div>

          {/* Selected fields picker + results grid. */}
          <div className="flex items-center gap-2 text-xs">
            <button
              type="button"
              onClick={() => setPickerOpen((o) => !o)}
              className="rounded border px-2 py-1"
            >
              {columns.length > 0 ? `${columns.length} fields` : "fields"}
              {pickerOpen ? " ▴" : " ▾"}
            </button>
            <span className="text-muted-foreground">
              {documents ? `${total} hits` : ""} · page {Math.floor(from / PAGE_SIZE) + 1} of {pageCount}
            </span>
            <div className="ml-auto flex gap-1">
              <button
                type="button"
                onClick={() => void goToPage(Math.max(0, from - PAGE_SIZE))}
                disabled={from === 0 || loading}
                className="rounded border px-2 py-1 disabled:opacity-40"
              >
                Previous
              </button>
              <button
                type="button"
                onClick={() => void goToPage(from + PAGE_SIZE)}
                disabled={(from + PAGE_SIZE) >= total || loading}
                className="rounded border px-2 py-1 disabled:opacity-40"
              >
                Next
              </button>
            </div>
          </div>

          {pickerOpen ? (
            <div className="flex flex-wrap gap-1 rounded border p-2">
              {propertyNames.map((name) => {
                const chosen = columns.includes(name)
                return (
                  <label key={name} className="flex items-center gap-1 text-xs">
                    <input
                      type="checkbox"
                      checked={chosen}
                      onChange={() =>
                        setSelectedFields(
                          chosen
                            ? columns.filter((c) => c !== name)
                            : [...columns, name],
                        )
                      }
                    />
                    {name}
                  </label>
                )
              })}
            </div>
          ) : null}

          {documentsError ? <p className="text-sm text-destructive">{documentsError.message}</p> : null}

          <div className="min-h-0 flex-1 overflow-auto rounded border">
            <table className="w-full border-collapse text-xs">
              <thead className="sticky top-0 bg-muted/60">
                <tr>
                  <th className="border-b px-2 py-1 text-left font-mono">_id</th>
                  {columns.map((c) => (
                    <th key={c} className="border-b px-2 py-1 text-left font-medium">
                      {c}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {(documents?.hits ?? []).map((hit, i) => (
                  <tr
                    key={`${hit.index}:${hit.id}`}
                    className="cursor-pointer hover:bg-muted/40"
                    onClick={() => setExpandedHit(expandedHit === i ? null : i)}
                  >
                    <td className="border-b px-2 py-1 font-mono">{hit.id}</td>
                    {columns.map((c) => (
                      <td key={c} className="max-w-64 truncate border-b px-2 py-1">
                        {cellText((hit.source as Record<string, unknown> | null)?.[c])}
                      </td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
            {!loading && documents && documents.hits.length === 0 ? (
              <p className="px-2 py-3 text-xs text-muted-foreground">No documents match.</p>
            ) : null}
          </div>

          {expandedHit !== null && documents?.hits[expandedHit] ? (
            <div className="max-h-64 overflow-auto rounded border bg-muted/30 p-2">
              <pre className="whitespace-pre-wrap break-words font-mono text-xs">
                {JSON.stringify(documents.hits[expandedHit].source, null, 2)}
              </pre>
            </div>
          ) : null}
        </div>
      ) : null}

      {tab === "mapping" ? (
        <div className="flex min-h-0 flex-1 flex-col gap-2">
          <p className="text-xs text-muted-foreground">
            {detail ? `${detail.docsCount} docs · ${formatBytes(detail.storeSizeBytes)}` : ""}
          </p>
          <details open className="rounded border p-2">
            <summary className="cursor-pointer text-xs font-medium">mappings</summary>
            <pre className="mt-1 max-h-64 overflow-auto whitespace-pre-wrap break-words font-mono text-xs">
              {detail ? JSON.stringify(detail.mappings, null, 2) : ""}
            </pre>
          </details>
          <details className="rounded border p-2">
            <summary className="cursor-pointer text-xs font-medium">settings</summary>
            <pre className="mt-1 max-h-64 overflow-auto whitespace-pre-wrap break-words font-mono text-xs">
              {detail ? JSON.stringify(detail.settings, null, 2) : ""}
            </pre>
          </details>
        </div>
      ) : null}

      {tab === "query" ? (
        <div className="flex min-h-0 flex-1 flex-col gap-2">
          <div className="flex items-center gap-2 text-xs">
            <label className="flex items-center gap-1">
              size
              <input
                type="number"
                min={1}
                max={100}
                className="w-16 rounded border bg-transparent px-1 py-1"
                value={dslSize}
                onChange={(e) => setDslSize(Math.max(1, Math.min(100, Number(e.target.value) || 25)))}
              />
            </label>
            <button
              type="button"
              onClick={() => void runDsl()}
              disabled={dslLoading}
              className="rounded border px-2 py-1"
            >
              {dslLoading ? "Running…" : "Run"}
            </button>
          </div>
          <textarea
            aria-label="Query DSL"
            className="min-h-24 flex-1 rounded border bg-transparent p-2 font-mono text-xs"
            placeholder="Query DSL — e.g. {&quot;match&quot;: {&quot;level&quot;: &quot;error&quot;}}"
            value={dsl}
            onChange={(e) => setDsl(e.target.value)}
          />
          <textarea
            aria-label="Aggregations"
            className="min-h-16 rounded border bg-transparent p-2 font-mono text-xs"
            placeholder="Aggregations (optional, JSON)"
            value={dslAggs}
            onChange={(e) => setDslAggs(e.target.value)}
          />
          {dslError ? <p className="text-xs text-destructive">{dslError.message}</p> : null}
          {dslResult ? (
            <pre className="max-h-64 flex-1 overflow-auto whitespace-pre-wrap break-words rounded border bg-muted/30 p-2 font-mono text-xs">
              {JSON.stringify(dslResult, null, 2)}
            </pre>
          ) : null}
        </div>
      ) : null}
    </div>
  )
}