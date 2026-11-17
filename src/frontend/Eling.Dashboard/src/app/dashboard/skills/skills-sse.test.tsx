import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { render, screen, fireEvent, waitFor, act } from "@testing-library/react"

import { SkillsList } from "./SkillsList"

vi.mock("@/components/dashboard-header", () => ({
  DashboardHeader: ({ children, label }: { children?: React.ReactNode; label: string }) => (
    <header>
      <span>{label}</span>
      {children}
    </header>
  ),
}))

// SkillsList reads filter state through next/navigation (URL-backed
// persistence); stub the router surface deterministically.
vi.mock("next/navigation", () => ({
  useSearchParams: () => new URLSearchParams(),
  usePathname: () => "/dashboard/skills",
  useRouter: () => ({
    replace: vi.fn(),
    push: vi.fn(),
  }),
}))

/**
 * Wire fixtures use neutral names only — no real skill names — and an
 * in-memory catalog the stub can trash, restore and filter so the trash flow
 * is exercised end to end.
 */

const env = (data: unknown, init?: ResponseInit) =>
  new Response(JSON.stringify({ code: "ok", message: "ok", data }), {
    status: 200,
    headers: { "Content-Type": "application/json" },
    ...init,
  })

const errEnv = (status: number, code: string, message: string) =>
  new Response(JSON.stringify({ code, message, data: null }), {
    status,
    headers: { "Content-Type": "application/json" },
  })

const ELING_ID = "01jfixtureelingskill000000001"
const EXTERNAL_ID = "01jfixtureexternalskill0000001"

const elingSummary = {
  id: ELING_ID,
  name: "fixture-eling-skill",
  description: "Fixture skill owned by Eling",
  path: "/catalog/skills/fixture-eling-skill/SKILL.md",
  origin: "eling",
  tags: ["alpha"],
  updatedAt: "2026-11-10T08:00:00Z",
}

const externalSummary = {
  id: EXTERNAL_ID,
  name: "fixture-external-skill",
  description: "Fixture skill owned by other tooling",
  path: "/catalog/skills/fixture-external-skill/SKILL.md",
  origin: "external",
  tags: ["beta"],
  updatedAt: "2026-11-09T08:00:00Z",
}

const elingDetail = {
  ...elingSummary,
  body: "# fixture-eling-skill\n\nFixture body for the Eling-managed skill.",
  relatedMemories: [{ id: "01jfixturememory0000000001", scope: "global", relation: "implements" }],
  relatedSnippets: [],
  createdAt: "2026-10-01T08:00:00Z",
}

const externalDetail = {
  ...externalSummary,
  body: "# fixture-external-skill\n\nFixture body for the externally managed skill.",
  relatedMemories: [],
  relatedSnippets: [{ id: "01jfixturesnippet000000001", relation: "references" }],
  createdAt: "2026-10-02T08:00:00Z",
}

const state = {
  trashed: new Set<string>(),
  adopted: new Set<string>(),
  putBodies: [] as Array<Record<string, unknown>>,
  deleteCalls: [] as string[],
  restoreCalls: [] as string[],
  adoptCalls: [] as string[],
  rescanReport: {
    discovered: 0,
    external: 0,
    missing: 0,
    renamed: 0,
    failed: 0,
    failures: [] as Array<{ path: string; reason: string }>,
    failuresTruncated: false,
  },
}

function summaryFor<T extends { id: string; origin: string }>(base: T) {
  return state.adopted.has(base.id) ? { ...base, origin: "eling" } : base
}

function detailFor<T extends { id: string; origin: string }>(base: T) {
  return state.adopted.has(base.id) ? { ...base, origin: "eling" } : base
}

function listEnvelope(originParam: string | null, includeTrashed: boolean) {
  let items = [summaryFor(elingSummary), summaryFor(externalSummary)]
  if (originParam) items = items.filter((s) => s.origin === originParam)
  if (!includeTrashed) items = items.filter((s) => !state.trashed.has(s.id))
  return {
    items,
    metadata: { total: 2, matching: items.length, limit: 100, offset: 0 },
  }
}

class StubEventSource {
  static instances: StubEventSource[] = []
  static CONNECTING = 0
  static CLOSED = 2
  onopen: (() => void) | null = null
  onmessage: ((event: MessageEvent) => void) | null = null
  onerror: (() => void) | null = null
  readyState = StubEventSource.CONNECTING
  constructor() {
    StubEventSource.instances.push(this)
  }
  close() {}
}

const fetchCallCount = () => vi.mocked(fetch).mock.calls.length

beforeEach(() => {
  // The page restores its last position from localStorage (URL param >
  // saved position > default). Tests must not leak a previously selected
  // skill into the next mount.
  localStorage.clear()
  state.trashed = new Set()
  state.adopted = new Set()
  state.putBodies = []
  state.deleteCalls = []
  state.restoreCalls = []
  state.adoptCalls = []
  state.rescanReport = {
    discovered: 0,
    external: 0,
    missing: 0,
    renamed: 0,
    failed: 0,
    failures: [],
    failuresTruncated: false,
  }
  StubEventSource.instances = []

  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      const method = (init?.method ?? "GET").toUpperCase()

      if (url.includes("/api/skills/rescan") && method === "POST") {
        return env(state.rescanReport)
      }

      const restoreMatch = url.match(/\/api\/skills\/([^/?]+)\/restore/)
      if (restoreMatch && method === "POST") {
        const id = decodeURIComponent(restoreMatch[1])
        state.restoreCalls.push(id)
        state.trashed.delete(id)
        return env(id === ELING_ID ? elingDetail : detailFor(externalDetail))
      }

      const adoptMatch = url.match(/\/api\/skills\/([^/?]+)\/adopt/)
      if (adoptMatch && method === "POST") {
        const id = decodeURIComponent(adoptMatch[1])
        state.adoptCalls.push(id)
        state.adopted.add(id)
        return env(id === ELING_ID ? elingDetail : detailFor(externalDetail))
      }

      const idMatch = url.match(/\/api\/skills\/([^/?]+)(?:\?.*)?$/)
      if (idMatch && method === "GET") {
        const id = decodeURIComponent(idMatch[1])
        if (state.trashed.has(id)) {
          return errEnv(404, "skill_not_found", "No skill with that id exists.")
        }
        if (id === ELING_ID) return env(elingDetail)
        if (id === EXTERNAL_ID) return env(detailFor(externalDetail))
        return errEnv(404, "skill_not_found", "No skill with that id exists.")
      }
      if (idMatch && method === "PUT") {
        const id = decodeURIComponent(idMatch[1])
        const body = JSON.parse(String(init?.body ?? "{}")) as Record<string, unknown>
        state.putBodies.push({ id, ...body })
        return env({ ...elingDetail, ...body, name: body.name ?? elingDetail.name })
      }
      if (idMatch && method === "DELETE") {
        const id = decodeURIComponent(idMatch[1])
        state.deleteCalls.push(id)
        state.trashed.add(id)
        return env({ trashed: true })
      }

      if (method === "GET" && url.includes("/api/skills")) {
        const parsed = new URL(url, "http://localhost")
        const originParam = parsed.searchParams.get("origin")
        const includeTrashed = parsed.searchParams.get("includeTrashed") === "true"
        return env(listEnvelope(originParam, includeTrashed))
      }

      return errEnv(404, "not_found", `unstubbed request: ${method} ${url}`)
    }),
  )

  vi.stubGlobal("EventSource", StubEventSource)

  Object.defineProperty(window, "matchMedia", {
    writable: true,
    configurable: true,
    value: vi.fn((query: string) => ({
      matches: false,
      media: query,
      onchange: null,
      addEventListener: () => {},
      removeEventListener: () => {},
      addListener: () => {},
      removeListener: () => {},
      dispatchEvent: () => false,
    })),
  })
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe("SkillsList SSE", () => {
  it("a skills SSE frame refetches the list and the open detail, silently", async () => {
    render(<SkillsList />)
    expect(await screen.findByText("fixture-eling-skill")).toBeTruthy()
    const callsBefore = fetchCallCount()

    fireEvent.click(screen.getByText("fixture-eling-skill"))
    await screen.findAllByText(/fixture body for the Eling-managed skill/i)
    const callsAfterOpen = fetchCallCount()
    expect(callsAfterOpen).toBeGreaterThan(callsBefore)

    act(() => {
      StubEventSource.instances.at(-1)?.onmessage?.({ data: "skills" } as MessageEvent)
    })

    // One list reload (its paged loop) + one detail refetch.
    await waitFor(() => expect(fetchCallCount()).toBeGreaterThanOrEqual(callsAfterOpen + 2))
    // The detail panel is still open showing the same skill — no unmount/vanish.
    expect(screen.getByText(/fixture body for the Eling-managed skill/i)).toBeTruthy()
  })

  it("a skills frame mid-edit keeps the typed text", async () => {
    render(<SkillsList />)
    fireEvent.click(await screen.findByText("fixture-eling-skill"))
    await screen.findAllByText(/fixture body for the Eling-managed skill/i)

    fireEvent.click(await screen.findByRole("button", { name: /^edit$/i }))
    const description = screen.getByRole("textbox", { name: /description/i })
    fireEvent.change(description, { target: { value: "my in-progress edit" } })

    const callsBefore = fetchCallCount()
    act(() => {
      StubEventSource.instances.at(-1)?.onmessage?.({ data: "skills" } as MessageEvent)
    })
    await waitFor(() => expect(fetchCallCount()).toBeGreaterThanOrEqual(callsBefore + 2))

    expect(
      (screen.getByRole("textbox", { name: /description/i }) as HTMLInputElement).value,
    ).toBe("my in-progress edit")
  })
})
