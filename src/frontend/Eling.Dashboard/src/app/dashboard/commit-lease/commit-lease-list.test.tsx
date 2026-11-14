import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { render, screen, fireEvent, within } from "@testing-library/react"

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

  it("lists every path inside the detail dialog instead of on the card", async () => {
    render(<CommitLease />)
    await screen.findByText("repo-alpha")
    expect(screen.queryByText("a.ts")).toBeNull()
    fireEvent.click(screen.getAllByRole("button", { name: /detail/i })[0])
    const dialog = await screen.findByRole("dialog")
    expect(within(dialog).getByText("a.ts")).toBeTruthy()
    expect(within(dialog).getByText("b.ts")).toBeTruthy()
    expect(within(dialog).getByText("c.ts")).toBeTruthy()
    expect(within(dialog).getByText("d.ts")).toBeTruthy()
  })

  it("shows the full lease detail — ids, intent, branch, TTL — in the dialog", async () => {
    render(<CommitLease />)
    await screen.findByText("repo-alpha")
    fireEvent.click(screen.getAllByRole("button", { name: /detail/i })[0])
    const dialog = await screen.findByRole("dialog")
    expect(within(dialog).getByText("lease-expired-1")).toBeTruthy()
    expect(within(dialog).getByText("session-expired-1")).toBeTruthy()
    expect(within(dialog).getByText("expired lease fixture")).toBeTruthy()
    expect(within(dialog).getByText("main")).toBeTruthy()
    expect(within(dialog).getByText("0s · expired")).toBeTruthy()
  })

  it("renders a released row with null fields as dashes, not a crash", async () => {
    render(<CommitLease />)
    expect(await screen.findByText("Released —")).toBeTruthy()
    // The same null-tolerance inside the detail dialog
    fireEvent.click(screen.getAllByRole("button", { name: /detail/i })[1])
    const dialog = await screen.findByRole("dialog")
    expect(within(dialog).getByText("lease-released-1")).toBeTruthy()
    expect(within(dialog).getAllByText("—").length).toBeGreaterThan(0)
  })
})
