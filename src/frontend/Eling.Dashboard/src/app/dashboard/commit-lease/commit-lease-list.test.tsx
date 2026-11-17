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
  caller_id: "session-expired-1",
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
  caller_id: "session-released-1",
  branch: null,
  since: "2026-11-14T09:00:00Z",
  released_at: null,
  release_reason: null,
  intent: null,
  paths: null,
  checkout: "C:\\repo-beta",
}

const heldReleasedLease = {
  lease_id: "lease-held-1",
  caller_id: "session-held-1",
  branch: "feat/held",
  since: "2026-11-14T09:10:00Z",
  released_at: "2026-11-14T09:12:30Z",
  release_reason: "explicit",
  intent: "held lease fixture",
  paths: ["src/held.ts"],
  checkout: "C:\\repo-delta",
  ttl_seconds: 600,
}

const normalLease = {
  lease_id: "lease-live-1",
  caller_id: "session-live-1",
  branch: "feat/lease",
  since: "2026-11-14T11:00:00Z",
  last_heartbeat: "2026-11-14T11:00:30Z",
  lease_age_seconds: 100,
  ttl_seconds: 3600,
  paths: ["src/gamma.ts"],
  intent: "live lease fixture",
  checkout: "C:\\repo-gamma",
}

const snapshot = {
  checkout: null,
  branch: null,
  commit_leases: [expiredLease, normalLease],
  recently_released: [nullReleasedLease, heldReleasedLease],
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

  it("shows the actual duration a released lease was held", async () => {
    render(<CommitLease />)
    // 09:10:00Z -> 09:12:30Z = 150s -> formatAge renders "2m 30s"
    expect(await screen.findByText(/Held 2m 30s/)).toBeTruthy()
  })

  it("shows the branch on the released card", async () => {
    render(<CommitLease />)
    expect(await screen.findByText("feat/held")).toBeTruthy()
  })

  it("shows the configured total as a separate TTL badge on the card", async () => {
    render(<CommitLease />)
    // ttl_seconds: 3600 -> formatAge renders "1h 0m"
    expect(await screen.findByText("TTL 1h 0m")).toBeTruthy()
  })

  it("renders the expired TTL in amber instead of a countdown", async () => {
    render(<CommitLease />)
    expect(await screen.findByText("0s · expired")).toBeTruthy()
  })

  it("shows the intent directly on the card", async () => {
    render(<CommitLease />)
    // The fixture's intent must be reachable without opening any dialog
    expect(await screen.findByText("expired lease fixture")).toBeTruthy()
  })

  it("lists every path inside the detail dialog, not on the card", async () => {
    render(<CommitLease />)
    await screen.findByText("repo-alpha")
    expect(screen.queryByText("a.ts")).toBeNull()
    fireEvent.click(screen.getByRole("button", { name: /lease details for repo-alpha/i }))
    const dialog = await screen.findByRole("dialog")
    for (const p of ["a.ts", "b.ts", "c.ts", "d.ts"]) {
      expect(within(dialog).getByText(p)).toBeTruthy()
    }
  })

  it("shows the full lease detail — ids, intent, branch, TTL — in the dialog", async () => {
    render(<CommitLease />)
    await screen.findByText("repo-alpha")
    fireEvent.click(screen.getByRole("button", { name: /lease details for repo-alpha/i }))
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
    fireEvent.click(screen.getByRole("button", { name: /lease details for repo-beta/i }))
    const dialog = await screen.findByRole("dialog")
    expect(within(dialog).getByText("lease-released-1")).toBeTruthy()
    expect(within(dialog).getAllByText("—").length).toBeGreaterThan(0)
  })

  it("clicking Force release opens the confirmation, not the detail dialog", async () => {
    render(<CommitLease />)
    await screen.findByText("repo-alpha")
    fireEvent.click(screen.getAllByRole("button", { name: /force release/i })[0])
    expect(await screen.findByText("Force release lease?")).toBeTruthy()
    expect(screen.queryByRole("dialog")).toBeNull()
  })
})
