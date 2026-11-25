import { act, render, screen } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"

import { RelativeTime } from "./relative-time"

describe("RelativeTime", () => {
  beforeEach(() => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date("2026-11-26T10:00:00Z"))
  })

  afterEach(() => vi.useRealTimers())

  it("refreshes the age when the shared clock ticks", () => {
    render(<RelativeTime value="2026-11-26T09:56:00.000Z" />)
    expect(screen.getByText("4 minutes ago")).toBeDefined()

    act(() => {
      vi.setSystemTime(new Date("2026-11-26T10:00:30Z"))
      vi.advanceTimersByTime(30_000)
    })

    expect(screen.getByText("5 minutes ago")).toBeDefined()
  })
})
