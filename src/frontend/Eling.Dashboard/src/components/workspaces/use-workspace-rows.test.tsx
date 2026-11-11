import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { act, renderHook, waitFor } from "@testing-library/react"

import type { WorkspaceGroupMode } from "./grouping"

/**
 * The coordinator owns the workspace arrangement, so the client never groups
 * anything itself. These tests pin the two consequences: a mode switch is
 * always a fresh request, and two surfaces that want the same mode share one
 * request instead of issuing two.
 */
/** Wraps a payload in the standard envelope so stubs match the live wire format. */
const env = (data: unknown) => JSON.stringify({ code: "ok", message: "ok", data })

describe("useWorkspaceTrees", () => {
  let requests: string[]

  beforeEach(() => {
    vi.resetModules()
    requests = []
    vi.stubGlobal(
      "fetch",
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input)
        requests.push(url)
        return new Response(
          env({
            rows: [{ root: "/work/alpha", alive: true }],
            group: "flat",
            nodes: [
              {
                id: "ws:/work/alpha",
                label: "alpha",
                path: "/work/alpha",
                root: "/work/alpha",
                alive: true,
                roots: ["/work/alpha"],
                isRepository: false,
                isWorktree: false,
                children: [],
              },
            ],
          }),
          { status: 200, headers: { "Content-Type": "application/json" } },
        )
      }),
    )
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it("asks the coordinator for the mode it was given", async () => {
    const { useWorkspaceTrees } = await import("./use-workspace-rows")

    const { result } = renderHook(() => useWorkspaceTrees("repo"))

    await waitFor(() => expect(result.current.rows).toHaveLength(1))
    expect(requests).toHaveLength(1)
    expect(requests[0]).toContain("group=repo")
  })

  it("hits the backend again when the mode changes", async () => {
    const { useWorkspaceTrees } = await import("./use-workspace-rows")

    const { result, rerender } = renderHook(
      ({ mode }: { mode: WorkspaceGroupMode }) => useWorkspaceTrees(mode),
      { initialProps: { mode: "flat" as WorkspaceGroupMode } },
    )

    await waitFor(() => expect(result.current.rows).toHaveLength(1))
    expect(requests).toHaveLength(1)

    rerender({ mode: "repo" as WorkspaceGroupMode })

    await waitFor(() => expect(requests).toHaveLength(2))
    expect(requests[1]).toContain("group=repo")
  })

  it("shares one request between two surfaces on the same mode", async () => {
    const { useWorkspaceTrees } = await import("./use-workspace-rows")

    const { result } = renderHook(() => [
      useWorkspaceTrees("folder"),
      useWorkspaceTrees("folder"),
    ])

    await waitFor(() => expect(result.current[0].rows).toHaveLength(1))
    expect(result.current[1].rows).toHaveLength(1)
    expect(requests).toHaveLength(1)
  })
})

/**
 * Answers arrive out of order in the real app — a slow request for the mode the
 * reader just left must never replace the tree they are looking at.
 */
describe("useWorkspaceTrees with answers in flight", () => {
  type Pending = { url: string; resolve: () => void }
  let pending: Pending[]

  beforeEach(() => {
    vi.resetModules()
    pending = []
    vi.stubGlobal(
      "fetch",
      vi.fn((input: RequestInfo | URL) => {
        const url = String(input)
        const group = new URL(url, "http://localhost").searchParams.get("group")
        return new Promise<Response>((resolve) => {
          pending.push({
            url,
            resolve: () =>
              resolve(
                new Response(
                  env({
                    rows: [{ root: `/work/${group}`, alive: true }],
                    group,
                    nodes: [],
                  }),
                  { status: 200, headers: { "Content-Type": "application/json" } },
                ),
              ),
          })
        })
      }),
    )
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it("keeps the mode the reader switched back to, and drops the other answer", async () => {
    const { useWorkspaceTrees } = await import("./use-workspace-rows")

    const { result, rerender } = renderHook(
      ({ mode }: { mode: WorkspaceGroupMode }) => useWorkspaceTrees(mode),
      { initialProps: { mode: "flat" as WorkspaceGroupMode } },
    )

    // flat is asked for, then repo, then flat again while both are in flight.
    rerender({ mode: "repo" as WorkspaceGroupMode })
    rerender({ mode: "flat" as WorkspaceGroupMode })
    expect(pending).toHaveLength(2)

    // The repo answer lands last: it must not become the rendered list.
    await act(async () => {
      pending[1].resolve()
      await Promise.resolve()
    })
    await act(async () => {
      pending[0].resolve()
      await Promise.resolve()
    })

    await waitFor(() => expect(result.current.rows).toHaveLength(1))
    expect(result.current.rows[0].root).toBe("/work/flat")
  })

  it("re-asks the backend when the reader switches back, after the first answer landed", async () => {
    const { useWorkspaceTrees } = await import("./use-workspace-rows")

    const { result, rerender } = renderHook(
      ({ mode }: { mode: WorkspaceGroupMode }) => useWorkspaceTrees(mode),
      { initialProps: { mode: "flat" as WorkspaceGroupMode } },
    )

    await act(async () => {
      pending[0].resolve()
      await Promise.resolve()
    })
    await waitFor(() => expect(result.current.rows[0]?.root).toBe("/work/flat"))

    rerender({ mode: "repo" as WorkspaceGroupMode })
    await act(async () => {
      pending[1].resolve()
      await Promise.resolve()
    })
    await waitFor(() => expect(result.current.rows[0]?.root).toBe("/work/repo"))

    rerender({ mode: "flat" as WorkspaceGroupMode })

    // The earlier flat answer is stale by now, so the switch costs a request.
    await waitFor(() => expect(pending).toHaveLength(3))
    expect(pending[2].url).toContain("group=flat")
  })
})
