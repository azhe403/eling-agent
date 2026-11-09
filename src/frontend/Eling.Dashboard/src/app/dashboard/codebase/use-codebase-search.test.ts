import { describe, it, expect, vi, beforeEach } from "vitest"
import { renderHook, act, waitFor } from "@testing-library/react"
import { useCodebaseSearch } from "./use-codebase-search"
import type { SearchScope } from "./scope"
import type { WorkspaceSelection } from "@/components/workspaces/selection"

// The hook reads the URL through next/navigation, so the router calls are
// stubbed: the tests care about the search itself, not the address bar.
vi.mock("next/navigation", () => ({
  useSearchParams: () => new URLSearchParams(),
  useRouter: () => ({ replace: vi.fn(), push: vi.fn() }),
  usePathname: () => "/dashboard/codebase",
}))

const selection: WorkspaceSelection = { kind: "all" }

function setup(overrides: Partial<Parameters<typeof useCodebaseSearch>[0]> = {}) {
  const writeUrl = vi.fn()
  const setStatusLoading = vi.fn()
  const { result } = renderHook(() =>
    useCodebaseSearch({
      setStatusLoading,
      defaultScope: "content" as SearchScope,
      writeUrl,
      selectedFile: null,
      ...overrides,
    })
  )
  return { result, writeUrl, setStatusLoading }
}

describe("useCodebaseSearch", () => {
  beforeEach(() => {
    vi.restoreAllMocks()
    // The tabs live in localStorage, so a leftover tab from another test
    // would decide which scope the hook starts on.
    window.localStorage.clear()
  })

  it("starts with the selection the caller gave it", () => {
    const { result } = setup()
    expect(result.current.selection).toEqual(selection)
    expect(result.current.searchScope).toBe("content")
  })

  it("clears the rows and the error when the query is emptied", () => {
    const { result } = setup()

    act(() => result.current.handleQueryChange(""))

    // The list is emptied in the event handler, not in an effect, so the rows
    // go without waiting for a render pass.
    expect(result.current.results).toEqual([])
    expect(result.current.fileResults).toEqual([])
    expect(result.current.searchError).toBeNull()
    expect(result.current.searching).toBe(false)
  })

  it("keeps the query in the tab it belongs to", () => {
    const { result } = setup()

    act(() => result.current.handleQueryChange("zel"))

    expect(result.current.query).toBe("zel")
  })

  it("searches immediately when the reader presses Enter", () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ({ results: [], stats: null, mode: "search" }),
    })
    vi.stubGlobal("fetch", fetchMock)

    const { result } = setup()
    act(() => result.current.handleQueryChange("zel"))

    act(() => result.current.searchNow())

    // Enter does not wait out the debounce: the request goes out at once.
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const url = fetchMock.mock.calls[0][0] as string
    expect(url).toContain("q=zel")
  })

  it("drops a stale answer when a newer search has already gone out", async () => {
    const resolvers: Array<(v: unknown) => void> = []
    const fetchMock = vi.fn().mockImplementation(
      () =>
        new Promise((resolve) => {
          resolvers.push(resolve)
        })
    )
    vi.stubGlobal("fetch", fetchMock)

    const { result } = setup()

    // Two searches in flight; the first one answers last.
    act(() => {
      void result.current.doSearch("first", "content")
      void result.current.doSearch("second", "content")
    })

    await waitFor(() => expect(resolvers.length).toBe(2))

    await act(async () => {
      resolvers[1]({
        ok: true,
        json: async () => ({
          results: [{ projectRoot: "r", path: "second.ts", startLine: 1, endLine: 2, content: "x", score: 1 }],
          stats: null,
          mode: "search",
        }),
      })
      resolvers[0]({
        ok: true,
        json: async () => ({
          results: [{ projectRoot: "r", path: "first.ts", startLine: 1, endLine: 2, content: "x", score: 1 }],
          stats: null,
          mode: "search",
        }),
      })
    })

    // The newer search owns the rows; the slow older answer is dropped.
    await waitFor(() => expect(result.current.results).toHaveLength(1))
    expect(result.current.results[0].path).toBe("second.ts")
  })

  it("puts the new scope in the URL when the scope changes", () => {
    const { result, writeUrl } = setup()

    act(() => result.current.applySearchScope("file"))

    expect(writeUrl).toHaveBeenCalled()
    const written = writeUrl.mock.calls[0][0] as { scope: string }
    expect(written.scope).toBe("file")
  })

  it("does nothing when the scope is the one already on screen", () => {
    const { result, writeUrl } = setup()

    act(() => result.current.applySearchScope("content"))

    expect(writeUrl).not.toHaveBeenCalled()
  })

  it("writes the selection change into the URL", () => {
    const { result, writeUrl } = setup()

    act(() => result.current.applySelection({ kind: "workspaces", roots: ["a"] }))

    expect(writeUrl).toHaveBeenCalled()
    const written = writeUrl.mock.calls[0][0] as { selection: WorkspaceSelection }
    expect(written.selection).toEqual({ kind: "workspaces", roots: ["a"] })
  })

  it("opens a file without dropping the tab it belongs to", () => {
    const { result, writeUrl } = setup()

    act(() => result.current.openFile({ projectRoot: "r", path: "a.ts" }))

    const written = writeUrl.mock.calls[0][0] as { file: unknown; tab: string }
    expect(written.file).toEqual({ projectRoot: "r", path: "a.ts" })
    expect(written.tab).toBe(result.current.activeTabId)
  })

  it("closes the file but keeps the query", () => {
    const { result, writeUrl } = setup()

    act(() => result.current.handleQueryChange("zel"))
    act(() => result.current.closeFile())

    const written = writeUrl.mock.calls[writeUrl.mock.calls.length - 1][0] as {
      file: unknown
      query: string
    }
    expect(written.file).toBeNull()
    expect(written.query).toBe("zel")
  })
})
