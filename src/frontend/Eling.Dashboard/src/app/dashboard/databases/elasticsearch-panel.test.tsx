import { describe, it, expect, vi, beforeEach, afterEach } from "vitest"
import { render, screen, waitFor, fireEvent, cleanup } from "@testing-library/react"

import {
  ElasticsearchIndexPanel,
  dateFieldsOf,
  filtersPayload,
  formatBytes,
  panelStateKey,
  presetRange,
  scalarFieldNames,
} from "./components/elasticsearch/ElasticsearchIndexPanel"
import * as esClient from "@/app/dashboard/databases/es-client"
import type { DbConn, ElasticsearchIndexDetail } from "@/app/dashboard/databases/types"

/** One connection the panel needs for its header. */
const conn = (id: string): DbConn => ({
  id,
  name: "cluster-dev",
  provider: "Elasticsearch",
  environment: "ReadOnly",
  hasConnectionString: true,
  allowAgentMutations: false,
  requireApprovalForMutations: true,
  createdAt: "2026-01-01T00:00:00Z",
  lastConnectedAt: null,
})

/** The mapping the panel reads: one date field and a couple of scalars. */
const detail: ElasticsearchIndexDetail = {
  name: "logs-*",
  mappings: {
    properties: {
      "@timestamp": { type: "date" },
      message: { type: "text" },
      level: { type: "keyword" },
      nested: { type: "object" },
    },
  },
  settings: { index: { number_of_shards: "1" } },
  docsCount: 12,
  storeSizeBytes: 2048,
}

describe("formatBytes", () => {
  it("spells small sizes in bytes", () => {
    expect(formatBytes(0)).toBe("0 B")
    expect(formatBytes(512)).toBe("512 B")
  })

  it("steps up to KB and MB", () => {
    expect(formatBytes(2048)).toBe("2.0 KB")
    expect(formatBytes(5 * 1024 * 1024)).toBe("5.0 MB")
  })

  it("tolerates nonsense", () => {
    expect(formatBytes(Number.NaN)).toBe("0 B")
    expect(formatBytes(-5)).toBe("0 B")
  })
})

describe("dateFieldsOf", () => {
  it("finds the date-typed properties in declaration order", () => {
    expect(dateFieldsOf(detail.mappings)).toEqual(["@timestamp"])
  })

  it("finds several and skips non-dates", () => {
    expect(
      dateFieldsOf({
        properties: {
          created: { type: "date" },
          message: { type: "text" },
          updated: { type: "date" },
        },
      }),
    ).toEqual(["created", "updated"])
  })

  it("answers nothing for a missing or malformed mapping", () => {
    expect(dateFieldsOf(null)).toEqual([])
    expect(dateFieldsOf({})).toEqual([])
    expect(dateFieldsOf({ properties: null })).toEqual([])
  })
})

describe("scalarFieldNames", () => {
  it("keeps scalars and drops objects", () => {
    expect(scalarFieldNames(detail.mappings)).toEqual(["@timestamp", "message", "level"])
  })

  it("answers nothing for a missing mapping", () => {
    expect(scalarFieldNames(undefined)).toEqual([])
  })
})

describe("filtersPayload", () => {
  it("answers null when no row is complete", () => {
    expect(filtersPayload([])).toBeNull()
    expect(filtersPayload([{ field: "", op: "is", value: "x" }])).toBeNull()
    expect(filtersPayload([{ field: "level", op: "is", value: "" }])).toBeNull()
  })

  it("compiles complete rows and drops the value for exists", () => {
    const payload = filtersPayload([
      { field: "level", op: "is", value: "error" },
      { field: "user.id", op: "exists", value: "" },
    ])
    expect(JSON.parse(payload ?? "[]")).toEqual([
      { field: "level", op: "is", value: "error" },
      { field: "user.id", op: "exists" },
    ])
  })

  it("keeps a numeric range value verbatim", () => {
    const payload = filtersPayload([{ field: "status", op: "gte", value: "500" }])
    expect(JSON.parse(payload ?? "[]")).toEqual([{ field: "status", op: "gte", value: "500" }])
  })
})

describe("presetRange", () => {
  const now = Date.UTC(2026, 10, 20, 12, 0, 0)

  it("answers no bounds for all time", () => {
    expect(presetRange("all", now)).toEqual({ start: null, end: null })
  })

  it("answers an ISO window for a preset", () => {
    const { start, end } = presetRange("1h", now)
    expect(end).toBe(new Date(now).toISOString())
    expect(start).toBe(new Date(now - 60 * 60 * 1000).toISOString())
  })
})

describe("panelStateKey", () => {
  it("names one index of one connection", () => {
    expect(panelStateKey("c1", "orders")).toBe("eling-es-panel:c1:orders")
  })

  it("keeps two indices of one connection apart", () => {
    expect(panelStateKey("c1", "a")).not.toBe(panelStateKey("c1", "b"))
  })
})

describe("ElasticsearchIndexPanel", () => {
  beforeEach(() => {
    window.localStorage.clear()
    vi.spyOn(esClient, "fetchEsIndex").mockResolvedValue(detail)
    vi.spyOn(esClient, "fetchEsHistogram").mockResolvedValue({
      field: "@timestamp",
      interval: "day",
      buckets: [
        { keyMs: Date.UTC(2026, 10, 19), docCount: 3 },
        { keyMs: Date.UTC(2026, 10, 20), docCount: 9 },
      ],
    })
    vi.spyOn(esClient, "fetchEsDocuments").mockResolvedValue({
      total: 12,
      from: 0,
      size: 25,
      tookMs: 3,
      hits: [
        { index: "logs-2026", id: "abc", score: 1, source: { message: "hello", level: "info" } },
        { index: "logs-2026", id: "def", score: null, source: { message: "bye" } },
      ],
    })
    vi.spyOn(esClient, "fetchEsSearch").mockResolvedValue({
      total: 1,
      tookMs: 2,
      hits: [{ index: "logs-2026", id: "abc", score: 1, source: { message: "hello" } }],
      aggregations: null,
    })
  })

  afterEach(() => {
    cleanup()
    vi.restoreAllMocks()
  })

  it("renders the index name and the connection name", async () => {
    render(<ElasticsearchIndexPanel conns={[conn("c1")]} connId="c1" index="logs-2026" isWide={false} />)
    expect(screen.getByText("logs-2026")).toBeTruthy()
    expect(screen.getByText("cluster-dev")).toBeTruthy()
    await waitFor(() => expect(esClient.fetchEsIndex).toHaveBeenCalledWith("c1", "logs-2026"))
  })

  it("renders the documents of the first page", async () => {
    render(<ElasticsearchIndexPanel conns={[conn("c1")]} connId="c1" index="logs-2026" isWide={false} />)
    await waitFor(() => expect(screen.getByText("abc")).toBeTruthy())
    expect(screen.getByText("hello")).toBeTruthy()
    expect(screen.getByText("12 hits · page 1 of 1")).toBeTruthy()
  })

  it("renders an empty cell for a field the document lacks", async () => {
    render(<ElasticsearchIndexPanel conns={[conn("c1")]} connId="c1" index="logs-2026" isWide={false} />)
    await waitFor(() => expect(screen.getByText("def")).toBeTruthy())
    // The "level" column is selected by default; the second hit has no level.
    const row = screen.getByText("def").closest("tr")
    expect(row).not.toBeNull()
    expect(row?.textContent).not.toContain("undefined")
  })

  it("switches to the filter builder and adds a row", async () => {
    render(<ElasticsearchIndexPanel conns={[conn("c1")]} connId="c1" index="logs-2026" isWide={false} />)
    fireEvent.click(screen.getByRole("button", { name: "Filter" }))
    fireEvent.click(screen.getByRole("button", { name: "+ add filter" }))
    expect(await screen.findByLabelText("Field 1")).toBeTruthy()
    expect(screen.getByRole("button", { name: "AND" })).toBeTruthy()
  })

  it("persists the mode and the selected fields per index", async () => {
    const { unmount } = render(
      <ElasticsearchIndexPanel conns={[conn("c1")]} connId="c1" index="logs-2026" isWide={false} />,
    )
    fireEvent.click(screen.getByRole("button", { name: "Filter" }))
    await waitFor(() => {
      const saved = window.localStorage.getItem(panelStateKey("c1", "logs-2026"))
      expect(saved).not.toBeNull()
      expect(JSON.parse(saved ?? "{}").mode).toBe("filter")
    })
    unmount()

    // A fresh mount of the same tab restores the chosen mode.
    render(<ElasticsearchIndexPanel conns={[conn("c1")]} connId="c1" index="logs-2026" isWide={false} />)
    await waitFor(() => expect(screen.getByRole("button", { name: "+ add filter" })).toBeTruthy())
  })

  it("does not share persisted state between two indices", async () => {
    const { unmount } = render(
      <ElasticsearchIndexPanel conns={[conn("c1")]} connId="c1" index="index-a" isWide={false} />,
    )
    fireEvent.click(screen.getByRole("button", { name: "Filter" }))
    await waitFor(() => {
      expect(JSON.parse(window.localStorage.getItem(panelStateKey("c1", "index-a")) ?? "{}").mode).toBe("filter")
    })
    unmount()

    render(<ElasticsearchIndexPanel conns={[conn("c1")]} connId="c1" index="index-b" isWide={false} />)
    // index-b never chose Filter, so it opens in the default Lucene mode.
    await waitFor(() => expect(screen.getByLabelText("Query")).toBeTruthy())
    expect(window.localStorage.getItem(panelStateKey("c1", "index-b")) ?? "").not.toContain('"filter"')
  })

  it("runs a DSL query and shows the result", async () => {
    render(<ElasticsearchIndexPanel conns={[conn("c1")]} connId="c1" index="logs-2026" isWide={false} />)
    fireEvent.click(screen.getByRole("button", { name: "query" }))
    fireEvent.change(screen.getByLabelText("Query DSL"), { target: { value: '{"match":{"level":"info"}}' } })
    fireEvent.click(screen.getByRole("button", { name: "Run" }))
    await waitFor(() => expect(esClient.fetchEsSearch).toHaveBeenCalled())
    expect(await screen.findByText(/"tookMs": 2/)).toBeTruthy()
  })

  it("reports invalid DSL JSON without calling the server", async () => {
    render(<ElasticsearchIndexPanel conns={[conn("c1")]} connId="c1" index="logs-2026" isWide={false} />)
    fireEvent.click(screen.getByRole("button", { name: "query" }))
    fireEvent.change(screen.getByLabelText("Query DSL"), { target: { value: "{not json" } })
    fireEvent.click(screen.getByRole("button", { name: "Run" }))
    expect(await screen.findByText(/Invalid JSON/)).toBeTruthy()
    expect(esClient.fetchEsSearch).not.toHaveBeenCalled()
  })

  it("shows the mapping and settings on the Mapping tab", async () => {
    render(<ElasticsearchIndexPanel conns={[conn("c1")]} connId="c1" index="logs-2026" isWide={false} />)
    fireEvent.click(screen.getByRole("button", { name: "mapping" }))
    expect(await screen.findByText("mappings")).toBeTruthy()
    expect(screen.getByText("settings")).toBeTruthy()
    expect(screen.getByText(/12 docs · 2.0 KB/)).toBeTruthy()
  })

  it("shows the error the server answered with", async () => {
    vi.mocked(esClient.fetchEsIndex).mockRejectedValue({ status: 502, message: "authentication failed (401)" })
    render(<ElasticsearchIndexPanel conns={[conn("c1")]} connId="c1" index="logs-2026" isWide={false} />)
    expect(await screen.findByText("authentication failed (401)")).toBeTruthy()
  })
})
