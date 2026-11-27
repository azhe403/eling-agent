import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { renderHook } from "@testing-library/react"

import { useDocsSse } from "./use-docs-sse"

class FakeEventSource {
  static instances: FakeEventSource[] = []
  onopen: (() => void) | null = null
  onmessage: ((event: { data: string }) => void) | null = null
  onerror: (() => void) | null = null
  readyState = 1

  constructor(readonly url: string) {
    FakeEventSource.instances.push(this)
  }

  close(): void {}
}

describe("useDocsSse", () => {
  beforeEach(() => {
    FakeEventSource.instances = []
    vi.stubGlobal("EventSource", FakeEventSource)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it("refreshes on a scoped docs payload", () => {
    const onMutation = vi.fn()
    renderHook(() => useDocsSse(onMutation, ["docs"]))

    FakeEventSource.instances.at(-1)!.onmessage?.({
      data: JSON.stringify({ topic: "docs", workspace_id: "abc", workspace: "/ws/a" }),
    })

    expect(onMutation).toHaveBeenCalledTimes(1)
  })

  it("ignores a payload from a workspace outside the current selection", () => {
    const onMutation = vi.fn()
    renderHook(() => useDocsSse(onMutation, ["docs"], ["/ws/a"]))

    FakeEventSource.instances.at(-1)!.onmessage?.({
      data: JSON.stringify({ topic: "docs", workspace_id: "other", workspace: "/ws/b" }),
    })

    expect(onMutation).not.toHaveBeenCalled()
  })

  it("still refreshes on a plain topic frame", () => {
    const onMutation = vi.fn()
    renderHook(() => useDocsSse(onMutation, ["docs"]))

    FakeEventSource.instances.at(-1)!.onmessage?.({ data: "docs" })

    expect(onMutation).toHaveBeenCalledTimes(1)
  })
})
