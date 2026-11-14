import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { render, screen, fireEvent } from "@testing-library/react"

import { CommitLease } from "./CommitLease"

vi.mock("@/components/dashboard-header", () => ({
  DashboardHeader: ({ children, label }: { children?: React.ReactNode; label: string }) => (
    <header>
      <span>{label}</span>
      {children}
    </header>
  ),
}))

/**
 * Page-level coverage for the card rendering added when the tables became
 * cards: the null fields, the expired-TTL branch and the inline paths toggle
 * only need a wire-shaped snapshot and a fetch stub; SSE is a no-op.
 */

const env = (data: unknown) =>
  JSON.stringify({ code: "ok", message: "ok", data })

const expiredLease = {
  lease_id: "lease-expired-1",
  session_id: "session-expired-1",
  branch: "main",
  since: "2026-11-14T10:00:00Z",
  last_heartbeat: "2026-11-14T10:01:00Z",
  lease_age_seconds: 500,
  ttl_seconds: 0,
  paths: ["a.ts", "b.ts", "c.ts", "d.ts"],
  intent: "expired lease fixture",
  checkout: "C:\\repo-alpha",
}

const nullReleasedLease = {
  lease_id: "lease-released-1",
  session_id: "session-released-1",
  since: "2026-11-14T09:00:00Z",
  released_at: null,
  release_reason: null,
  intent: null,
  paths: null,
  checkout: "C:\\repo-beta",
}

const snapshot = {
  checkout: null,
  branch: null,
  commit_leases: [expiredLease],
  recently_released: [nullReleasedLease],
}

class StubEventSource {
  static CONNECTING = 0
  static CLOSED = 2
  onopen: (() => void) | null = null
  onmessage: ((event: MessageEvent) => void) | null = null
  onerror: (() => void) | null = null
  readyState = StubEventSource.CONNECTING
  close() {}
}

describe("CommitLease cards", () => {
  beforeEach(() => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input)
        const data = url.includes("/api/commit-lease/active")
          ? snapshot
          : { rows: [], group: "flat", nodes: [] }
        return new Response(env(data), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        })
      }),
    )
    vi.stubGlobal("EventSource", StubEventSource)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it("renders the expired TTL in amber instead of a countdown", async () => {
    render(<CommitLease />)
    expect(await screen.findByText("0s · expired")).toBeTruthy()
  })

  it("reveals the 4th path behind the +1 more toggle on an active card", async () => {
    render(<CommitLease />)
    await screen.findByText("a.ts")
    expect(screen.queryByText("d.ts")).toBeNull()
    fireEvent.click(screen.getByRole("button", { name: /\+1 more/i }))
    expect(screen.getByText("d.ts")).toBeTruthy()
  })

  it("renders a released row with null fields as dashes, not a crash", async () => {
    render(<CommitLease />)
    expect(await screen.findByText("Released —")).toBeTruthy()
    expect(screen.getByText("lease-released-1", { exact: false })).toBeTruthy()
  })
})
