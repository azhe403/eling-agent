import { renderHook, act, waitFor } from "@testing-library/react"
import { describe, it, expect, vi, beforeEach } from "vitest"

// The hook talks to the API through apiFetch; everything else is real.
vi.mock("@/lib/api", () => ({ apiFetch: vi.fn() }))

import { apiFetch } from "@/lib/api"
import { useTableData } from "./useTableData"
import type { DbConn, DbTab } from "../types"

const apiFetchMock = apiFetch as unknown as ReturnType<typeof vi.fn>

const conn: DbConn = {
  id: "c",
  name: "dev",
  provider: "MySql",
  environment: "dev",
  hasConnectionString: true,
  allowAgentMutations: false,
  requireApprovalForMutations: false,
  createdAt: "2026-11-12T00:00:00Z",
  lastConnectedAt: null,
}

const page4Tab: DbTab = { connId: "c", table: "t", database: null, browseLimit: 100, browsePage: 4 }
const page1Tab: DbTab = { ...page4Tab, browsePage: 1 }

/** The /browse response: one page of rows plus the filtered total. */
const browse = (rows: unknown[][], total: number) => ({
  columns: ["id"],
  rows,
  truncated: false,
  total,
  fromCache: false,
  cachedAt: null,
})

/** A promise whose resolution this test controls, plus the calls apiFetch saw. */
const deferred = <T,>() => {
  let resolve!: (v: T) => void
  const promise = new Promise<T>((res) => { resolve = res })
  return { promise, resolve }
}

const setup = (tab: DbTab) => {
  const activeTableRef = { current: tab as DbTab | null }
  const onClampPage = vi.fn()
  const notifyDb = vi.fn()
  const hooks = renderHook(() =>
    useTableData({
      notifyDb,
      conns: [conn],
      activeTable: tab,
      activeTableRef,
      selectedId: null,
      selectedConn: undefined,
      selectedIsReadOnly: true,
      schemaInfo: null,
      onClampPage,
    })
  )
  return { activeTableRef, onClampPage, notifyDb, hooks }
}

beforeEach(() => {
  apiFetchMock.mockReset()
})

describe("useTableData clamp vs the stale-answer guard", () => {
  it("a stale in-flight response whose page no longer exists does not clamp", async () => {
    // Request 1 asks for page 4 of a table that by then only had 150 rows
    // (page 2 valid). Before it resolves, the reader navigates back to
    // page 1 (request 2). When request 1 resolves, it must not touch
    // anything: the clamp belongs to the newest request only.
    const req1 = deferred<never>()
    const req2 = deferred<never>()
    apiFetchMock
      .mockImplementationOnce(() => req1.promise)
      .mockImplementationOnce(() => req2.promise)

    const { activeTableRef, onClampPage, hooks } = setup(page4Tab)

    // Request 1: page 4.
    let p1: Promise<void>
    act(() => {
      p1 = hooks.result.current.browseData("c", "t", null)
    })
    // The reader navigates to page 1; request 2 supersedes request 1.
    activeTableRef.current = { ...page1Tab }
    let p2: Promise<void>
    act(() => {
      p2 = hooks.result.current.browseData("c", "t", null)
    })

    // Request 1 resolves: page beyond the end (empty), count says 150 rows.
    await act(async () => {
      req1.resolve(browse([], 150) as never)
      await Promise.allSettled([p1!])
    })

    expect(onClampPage).not.toHaveBeenCalled()

    // Request 2 resolves normally; its rows and total land.
    await act(async () => {
      req2.resolve(browse([[1]], 150) as never)
      await Promise.allSettled([p2!])
    })

    await waitFor(() => expect(hooks.result.current.browseTotal).toBe(150))
    expect(onClampPage).not.toHaveBeenCalled()
  })

  it("the newest overflowed response clamps and paints nothing", async () => {
    const req = deferred<never>()
    apiFetchMock.mockImplementationOnce(() => req.promise)

    const { onClampPage, hooks } = setup(page4Tab)

    let p: Promise<void>
    act(() => {
      p = hooks.result.current.browseData("c", "t", null)
    })

    await act(async () => {
      req.resolve(browse([], 150) as never)
      await Promise.allSettled([p!])
    })

    expect(onClampPage).toHaveBeenCalledTimes(1)
    expect(onClampPage).toHaveBeenCalledWith(2)
    // The empty page must not be painted as the grid's content.
    expect(hooks.result.current.browseResult).toBeNull()
    expect(hooks.result.current.browseTotal).toBe(150)
  })

  it("sends the committed filter, sort and manual condition to /browse", async () => {
    apiFetchMock.mockImplementationOnce(async () => browse([[1]], 1) as never)
    const tab: DbTab = {
      connId: "c",
      table: "t",
      database: "db1",
      browseLimit: 100,
      browsePage: 2,
      browseFilters: [{ column: "name", op: "contains", value: "a", enabled: true, logic: "first" }],
      browseSort: [{ column: "updated_at", order: "desc" }],
      manualCondition: "is_active = 1",
    }

    const { hooks } = setup(tab)
    await act(async () => {
      await hooks.result.current.browseData("c", "t", "db1")
    })

    const [url, init] = apiFetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toContain("/c/browse")
    const body = JSON.parse(String(init.body))
    expect(body).toMatchObject({
      table: "t",
      database: "db1",
      page: 2,
      limit: 100,
      filters: [{ column: "name", op: "contains", value: "a", enabled: true, logic: "first" }],
      sort: [{ column: "updated_at", order: "desc" }],
      manualCondition: "is_active = 1",
    })
  })

  it("sends refresh=true to bypass the cache", async () => {
    apiFetchMock.mockImplementationOnce(async () => browse([[1]], 1) as never)

    const { hooks } = setup(page1Tab)
    await act(async () => {
      await hooks.result.current.browseData("c", "t", null, false, true)
    })

    const [url, init] = apiFetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toContain("/c/browse")
    expect(JSON.parse(String(init.body)).refresh).toBe(true)
  })
})
