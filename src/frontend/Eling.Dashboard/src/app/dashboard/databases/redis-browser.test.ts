import { describe, it, expect, vi, beforeEach, afterEach } from "vitest"
import { renderHook, act, waitFor } from "@testing-library/react"

import {
  dbRowLabel,
  dbRowCount,
  dbRowAsOf,
  parseRedisDb,
  pickRedisDb,
  fetchRedisDatabases,
  fetchRedisKeyDelete,
  fetchRedisPrefixPreview,
  fetchRedisPrefixDelete,
  redisPageKey,
  type RedisDbInfo,
  type RedisPage,
} from "./redis-browser"
import { useRedisBrowser } from "./hooks/useRedisBrowser"

describe("dbRowLabel", () => {
  it("reads db3", () => {
    expect(dbRowLabel({ index: 3, keys: 3 })).toBe("db3")
  })
})

describe("dbRowCount", () => {
  it("reads 3 keys", () => {
    expect(dbRowCount({ index: 3, keys: 3 })).toBe("3 keys")
  })

  it("reads 0 keys", () => {
    expect(dbRowCount({ index: 0, keys: 0 })).toBe("0 keys")
  })
})

describe("dbRowAsOf", () => {
  it("says nothing for a live count", () => {
    expect(dbRowAsOf({ index: 3, keys: 3 })).toBeNull()
  })

  it("stamps a cached count with the moment it was taken", () => {
    const at = "2026-10-27T08:15:00.000Z"
    expect(dbRowAsOf({ index: 3, keys: 3, fromCache: true, cachedAt: at })).toBe(
      `as of ${new Date(at).toLocaleString()}`,
    )
  })

  it("still says cached when the stamp is missing", () => {
    expect(dbRowAsOf({ index: 3, keys: 3, fromCache: true, cachedAt: null })).toBe("cached")
  })

  it("ignores an unparseable stamp", () => {
    expect(dbRowAsOf({ index: 3, keys: 3, fromCache: true, cachedAt: "not a date" })).toBe("cached")
  })
})

describe("parseRedisDb", () => {
  it("parses a valid index", () => {
    expect(parseRedisDb("3")).toBe(3)
  })

  it("rejects a non-number", () => {
    expect(parseRedisDb("abc")).toBeNull()
  })

  it("rejects a negative index", () => {
    expect(parseRedisDb("-1")).toBeNull()
  })

  it("rejects an out-of-range index", () => {
    expect(parseRedisDb("99")).toBeNull()
  })

  it("rejects null", () => {
    expect(parseRedisDb(null)).toBeNull()
  })
})

describe("pickRedisDb", () => {
  const available: RedisDbInfo[] = [
    { index: 3, keys: 3 },
    { index: 5, keys: 128 },
  ]

  it("uses the URL index when present", () => {
    expect(
      pickRedisDb({ urlDb: "5", savedTab: { connId: "a", redisDb: 3 }, connId: "a", available }),
    ).toBe(5)
  })

  it("uses the saved index for the same connection", () => {
    expect(pickRedisDb({ urlDb: null, savedTab: { connId: "a", redisDb: 3 }, connId: "a", available })).toBe(3)
  })

  it("ignores a saved index from another connection", () => {
    // available[0] must not be the saved index, or the case passes whether or
    // not the saved tab leaked in.
    const other: RedisDbInfo[] = [{ index: 7, keys: 1 }]
    expect(pickRedisDb({ urlDb: null, savedTab: { connId: "b", redisDb: 3 }, connId: "a", available: other })).toBe(7)
  })

  it("uses the first database that holds keys when nothing else names one", () => {
    expect(pickRedisDb({ urlDb: null, savedTab: null, connId: "a", available })).toBe(3)
  })

  it("falls back to zero when nothing is available", () => {
    expect(pickRedisDb({ urlDb: null, savedTab: null, connId: "a", available: [] })).toBe(0)
  })
})

describe("fetchRedisDatabases", () => {
  const originalFetch = globalThis.fetch

  beforeEach(() => {
    vi.restoreAllMocks()
  })

  afterEach(() => {
    globalThis.fetch = originalFetch
  })

  it("resolves with the parsed list on 200", async () => {
    globalThis.fetch = vi.fn(
      async () =>
        new Response(JSON.stringify([{ index: 3, keys: 3 }]), {
          status: 200,
          headers: { "content-type": "application/json" },
        }),
    )
    const result = await fetchRedisDatabases("c1")
    expect(result).toEqual([{ index: 3, keys: 3, fromCache: false, cachedAt: null }])
  })

  it("carries the cache stamp an offline server sends", async () => {
    const cachedAt = "2026-10-27T08:15:00.000Z"
    globalThis.fetch = vi.fn(
      async () =>
        new Response(
          JSON.stringify([{ index: 3, keys: 3, fromCache: true, cachedAt }]),
          { status: 200, headers: { "content-type": "application/json" } },
        ),
    )

    const result = await fetchRedisDatabases("c1")

    expect(result[0]?.fromCache).toBe(true)
    expect(result[0]?.cachedAt).toBe(cachedAt)
  })

  it("rejects on a non-OK response", async () => {
    globalThis.fetch = vi.fn(async () => new Response("nope", { status: 400 }))
    await expect(fetchRedisDatabases("c1")).rejects.toThrow()
  })
})

describe("fetchRedisKeyDelete", () => {
  beforeEach(() => {
    vi.unstubAllGlobals()
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it("deletes one key by query string and resolves deleted on 200", async () => {
    const fetchMock = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>(async () =>
      new Response(JSON.stringify({ deleted: true }), {
        status: 200,
        headers: { "content-type": "application/json" },
      }),
    )
    vi.stubGlobal("fetch", fetchMock)

    const result = await fetchRedisKeyDelete("c1", 2, "my:key")
    expect(result).toEqual({ deleted: true })

    const url = String(fetchMock.mock.calls[0]?.[0] ?? "")
    expect(url).toContain("/api/databases/c1/keys/delete")
    const qs = new URLSearchParams(url.split("?")[1])
    expect(qs.get("database")).toBe("2")
    expect(qs.get("key")).toBe("my:key")
    expect(fetchMock.mock.calls[0]?.[1]).toMatchObject({ method: "DELETE" })
  })

  it("keeps a key with slashes and percent signs out of the path", async () => {
    const fetchMock = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>(async () =>
      new Response(JSON.stringify({ deleted: true }), { status: 200 }),
    )
    vi.stubGlobal("fetch", fetchMock)

    await fetchRedisKeyDelete("c1", 0, "a/b%c")
    const url = String(fetchMock.mock.calls[0]?.[0] ?? "")
    const qs = new URLSearchParams(url.split("?")[1])
    expect(qs.get("key")).toBe("a/b%c")
  })

  it("rejects with the parsed error message on a non-OK response", async () => {
    const fetchMock = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>(async () =>
      new Response(JSON.stringify({ detail: "key not found" }), { status: 404 }),
    )
    vi.stubGlobal("fetch", fetchMock)

    await expect(fetchRedisKeyDelete("c1", 0, "missing")).rejects.toThrow("key not found")
  })
})

describe("fetchRedisPrefixPreview", () => {
  beforeEach(() => {
    vi.unstubAllGlobals()
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it("previews one prefix by query string and resolves the parsed body on 200", async () => {
    const body = { prefix: "users:", keys: ["users:1", "users:2"], total: 150, truncated: true }
    const fetchMock = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>(async () =>
      new Response(JSON.stringify(body), {
        status: 200,
        headers: { "content-type": "application/json" },
      }),
    )
    vi.stubGlobal("fetch", fetchMock)

    const result = await fetchRedisPrefixPreview("c1", 0, "users:")
    expect(result).toEqual(body)

    const url = String(fetchMock.mock.calls[0]?.[0] ?? "")
    expect(url).toContain("/api/databases/c1/keys/preview")
    const qs = new URLSearchParams(url.split("?")[1])
    expect(qs.get("database")).toBe("0")
    expect(qs.get("prefix")).toBe("users:")
  })

  it("rejects with the parsed error message on a non-OK response", async () => {
    const fetchMock = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>(async () => new Response("prefix must not be blank", { status: 400 }))
    vi.stubGlobal("fetch", fetchMock)

    await expect(fetchRedisPrefixPreview("c1", 0, "users:")).rejects.toThrow(
      "prefix must not be blank",
    )
  })
})

describe("fetchRedisPrefixDelete", () => {
  beforeEach(() => {
    vi.unstubAllGlobals()
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it("deletes one prefix by query string and resolves the parsed body on 200", async () => {
    const body = { deleted: 3, truncated: false }
    const fetchMock = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>(async () =>
      new Response(JSON.stringify(body), {
        status: 200,
        headers: { "content-type": "application/json" },
      }),
    )
    vi.stubGlobal("fetch", fetchMock)

    const result = await fetchRedisPrefixDelete("c1", 1, "users:")
    expect(result).toEqual(body)

    const url = String(fetchMock.mock.calls[0]?.[0] ?? "")
    expect(url).toContain("/api/databases/c1/keys/prefix")
    const qs = new URLSearchParams(url.split("?")[1])
    expect(qs.get("database")).toBe("1")
    expect(qs.get("prefix")).toBe("users:")
    expect(fetchMock.mock.calls[0]?.[1]).toMatchObject({ method: "DELETE" })
  })

  it("rejects with the parsed error message on a non-OK response", async () => {
    const fetchMock = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>(async () => new Response("prefix must not be blank", { status: 400 }))
    vi.stubGlobal("fetch", fetchMock)

    await expect(fetchRedisPrefixDelete("c1", 0, "users:")).rejects.toThrow(
      "prefix must not be blank",
    )
  })
})

describe("useRedisBrowser delete", () => {
  const rootPage: RedisPage = {
    prefix: "",
    nodes: [{ segment: "users", prefix: "users:" }],
    keys: [
      { key: "my:key", type: "string", ttlSeconds: 60 },
      { key: "other:key", type: "string", ttlSeconds: 60 },
    ],
    cursor: 0,
    pageOffset: 0,
    complete: true,
    scanned: 2,
  }

  /** What the server reports after the delete: the key is gone from the root. */
  const liveRootPage: RedisPage = {
    prefix: "",
    nodes: [{ segment: "users", prefix: "users:" }],
    keys: [{ key: "other:key", type: "string", ttlSeconds: 60 }],
    cursor: 0,
    pageOffset: 0,
    complete: true,
    scanned: 1,
  }

  const myPage: RedisPage = {
    prefix: "my:",
    nodes: [],
    keys: [{ key: "my:key", type: "string", ttlSeconds: 60 }],
    cursor: 0,
    pageOffset: 0,
    complete: true,
    scanned: 1,
  }

  const treePage: RedisPage = {
    prefix: "users:",
    nodes: [],
    keys: [{ key: "users:1", type: "string", ttlSeconds: 60 }],
    cursor: 0,
    pageOffset: 0,
    complete: true,
    scanned: 1,
  }

  /** Routes the delete, the preview, the databases list and the tree pages. */
  function stubRedisFetch(deleteStatus = 200) {
    const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
      const method = init?.method ?? "GET"
      if (url.includes("/keys/delete") && method === "DELETE") {
        if (deleteStatus !== 200) {
          return new Response(JSON.stringify({ detail: "key not found" }), { status: deleteStatus })
        }
        return new Response(JSON.stringify({ deleted: true }), { status: 200 })
      }
      if (url.includes("/keys/preview")) {
        return new Response(
          JSON.stringify({ prefix: "users:", keys: ["users:1"], total: 3, truncated: false }),
          { status: 200 },
        )
      }
      if (url.includes("/keys/prefix") && method === "DELETE") {
        return new Response(JSON.stringify({ deleted: 2, truncated: false }), { status: 200 })
      }
      if (url.includes("/keys/databases")) {
        return new Response(JSON.stringify([{ index: 0, keys: 2 }]), { status: 200 })
      }
      return new Response(JSON.stringify(liveRootPage), { status: 200 })
    })
    vi.stubGlobal("fetch", fetchMock)
    return fetchMock
  }

  function renderBrowser() {
    const notifyDb = vi.fn()
    const { result } = renderHook(() =>
      useRedisBrowser({
        notifyDb,
        activeTable: null,
        setActiveTable: vi.fn(),
        activeTableRef: { current: null },
        setSelectedId: vi.fn(),
        setSchemaConnId: vi.fn(),
        setOpenTabs: vi.fn(),
        setRightTab: vi.fn(),
        expandedConns: new Set(),
        setExpandedConns: vi.fn(),
        conns: [],
      }),
    )
    return { result, notifyDb }
  }

  beforeEach(() => {
    vi.unstubAllGlobals()
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it("clears the inspector when the deleted key matches", async () => {
    stubRedisFetch()
    const { result } = renderBrowser()

    act(() => {
      result.current.setRedisInspectKey("my:key")
      result.current.setRedisInspect({
        key: "my:key",
        type: "string",
        ttlSeconds: 60,
        expiresAt: null,
        length: 1,
        encoding: null,
        preview: { kind: "string", entries: [], truncated: false },
      })
      result.current.setRedisDeleteOpen(true)
    })

    await act(async () => {
      await result.current.deleteRedisKey("c1", 0, "my:key")
    })

    expect(result.current.redisInspectKey).toBe("")
    expect(result.current.redisInspect).toBeNull()
    expect(result.current.redisDeleteOpen).toBe(false)
  })

  it("keeps the inspector when the deleted key is another key", async () => {
    stubRedisFetch()
    const { result } = renderBrowser()

    act(() => {
      result.current.setRedisInspectKey("other:key")
    })

    await act(async () => {
      await result.current.deleteRedisKey("c1", 0, "my:key")
    })

    expect(result.current.redisInspectKey).toBe("other:key")
  })

  it("drops the deleted key from the cached pages", async () => {
    stubRedisFetch()
    const { result } = renderBrowser()

    act(() => {
      result.current.setRedisPages({
        [redisPageKey("c1", 0, "")]: rootPage,
        [redisPageKey("c1", 0, "my:")]: myPage,
        [redisPageKey("c1", 0, "users:")]: treePage,
      })
    })

    await act(async () => {
      await result.current.deleteRedisKey("c1", 0, "my:key")
    })

    await waitFor(() => {
      const root = result.current.redisPages[redisPageKey("c1", 0, "")]
      expect(root?.keys.map((k) => k.key)).toEqual(["other:key"])
      // The non-root page is never reloaded, so only the delete's own drop
      // can remove the key from it.
      expect(result.current.redisPages[redisPageKey("c1", 0, "my:")]?.keys).toEqual([])
      // A page the deleted key never appeared on is untouched.
      expect(result.current.redisPages[redisPageKey("c1", 0, "users:")]?.keys.map((k) => k.key)).toEqual([
        "users:1",
      ])
    })
  })

  it("keeps the dialog open and toasts on failure without dropping the key", async () => {
    stubRedisFetch(404)
    const { result, notifyDb } = renderBrowser()

    act(() => {
      result.current.setRedisPages({ [redisPageKey("c1", 0, "")]: rootPage })
      result.current.setRedisInspectKey("my:key")
      result.current.setRedisDeleteOpen(true)
    })

    await act(async () => {
      await result.current.deleteRedisKey("c1", 0, "my:key")
    })

    expect(notifyDb).toHaveBeenCalledWith("error", "Redis delete failed", "key not found")
    expect(result.current.redisDeleteOpen).toBe(true)
    expect(result.current.redisInspectKey).toBe("my:key")
    expect(result.current.redisPages[redisPageKey("c1", 0, "")]?.keys.map((k) => k.key)).toContain(
      "my:key",
    )
  })

  it("previews a prefix before the subtree delete", async () => {
    stubRedisFetch()
    const { result } = renderBrowser()

    await act(async () => {
      await result.current.previewRedisPrefix("c1", 0, "users:")
    })

    expect(result.current.redisPreview).toEqual({
      prefix: "users:",
      keys: ["users:1"],
      total: 3,
      truncated: false,
    })
    expect(result.current.redisPrefixPreviewOpen).toBe(true)
    expect(result.current.redisPreviewLoading).toBe(false)
  })

  it("clears an inspector inside the deleted subtree", async () => {
    stubRedisFetch()
    const { result } = renderBrowser()

    await act(async () => {
      await result.current.previewRedisPrefix("c1", 0, "users:")
    })

    act(() => {
      result.current.setRedisInspectKey("users:1")
    })

    await act(async () => {
      await result.current.deleteRedisPrefix("c1", 0, "users:")
    })

    expect(result.current.redisInspectKey).toBe("")
    expect(result.current.redisPrefixPreviewOpen).toBe(false)
  })

  it("toasts the count the server actually deleted, not the preview total", async () => {
    stubRedisFetch()
    const { result, notifyDb } = renderBrowser()

    // The preview said total 3; the server deleted only 2 (e.g. the subtree
    // shrank after the preview, or the run was truncated).
    await act(async () => {
      await result.current.previewRedisPrefix("c1", 0, "users:")
    })
    expect(result.current.redisPreview?.total).toBe(3)

    await act(async () => {
      await result.current.deleteRedisPrefix("c1", 0, "users:")
    })

    expect(notifyDb).toHaveBeenCalledWith("success", "Redis keys deleted", "2 keys under users:")
    expect(notifyDb).not.toHaveBeenCalledWith("success", "Redis keys deleted", "3 keys under users:")
  })
})
