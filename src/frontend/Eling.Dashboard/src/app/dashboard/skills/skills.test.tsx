import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { render, screen, fireEvent, waitFor } from "@testing-library/react"

import { SkillsList } from "./SkillsList"

vi.mock("@/components/dashboard-header", () => ({
  DashboardHeader: ({ children, label }: { children?: React.ReactNode; label: string }) => (
    <header>
      <span>{label}</span>
      {children}
    </header>
  ),
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

beforeEach(() => {
  state.trashed = new Set()
  state.adopted = new Set()
  state.putBodies = []
  state.deleteCalls = []
  state.restoreCalls = []
  state.adoptCalls = []

  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      const method = (init?.method ?? "GET").toUpperCase()

      if (url.includes("/api/skills/rescan") && method === "POST") {
        return env({ discovered: 0, external: 0, missing: 0, renamed: 0, failed: 0 })
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

describe("SkillsList", () => {
  it("renders skill cards from the envelope and filters by origin", async () => {
    render(<SkillsList />)
    expect(await screen.findByText("fixture-eling-skill")).toBeTruthy()
    expect(screen.getByText("fixture-external-skill")).toBeTruthy()

    fireEvent.click(screen.getByRole("button", { name: "External" }))
    await waitFor(() => expect(screen.queryByText("fixture-eling-skill")).toBeNull())
    expect(screen.getByText("fixture-external-skill")).toBeTruthy()
  })

  it("opens the detail panel on card click; external skills allow delete but not edit", async () => {
    render(<SkillsList />)
    fireEvent.click(await screen.findByText("fixture-external-skill"))

    // Detail fetch lands → read-only hint appears; edit stays disabled, delete is enabled.
    expect(await screen.findByText(/managed by other tooling/i)).toBeTruthy()
    expect(screen.getByRole("button", { name: /^edit$/i })).toHaveProperty("disabled", true)
    expect(screen.getByRole("button", { name: /delete skill/i })).toHaveProperty("disabled", false)
  })

  it("external skill shows Adopt; confirming adopts in place and enables Edit", async () => {
    render(<SkillsList />)
    fireEvent.click(await screen.findByText("fixture-external-skill"))

    // The primary action for an external skill is Adopt; Edit stays disabled.
    expect(await screen.findByText(/managed by other tooling/i)).toBeTruthy()
    expect(screen.getByRole("button", { name: "Adopt skill" })).toBeTruthy()
    expect(screen.getByRole("button", { name: /^edit$/i })).toHaveProperty("disabled", true)

    fireEvent.click(screen.getByRole("button", { name: "Adopt skill" }))

    // The confirm dialog names the skill; its path appears (panel header + dialog).
    expect(await screen.findByText(/Ownership moves to Eling in place/i)).toBeTruthy()
    expect(
      (await screen.findAllByText("/catalog/skills/fixture-external-skill/SKILL.md")).length,
    ).toBeGreaterThanOrEqual(2)

    fireEvent.click(screen.getByRole("button", { name: /^adopt$/i }))

    await waitFor(() => expect(state.adoptCalls).toEqual([EXTERNAL_ID]))
    // The panel refreshed in place: Eling-managed view, Edit enabled, Adopt gone.
    await waitFor(() =>
      expect(screen.getByRole("button", { name: /^edit$/i })).toHaveProperty("disabled", false),
    )
    expect(screen.queryByRole("button", { name: "Adopt skill" })).toBeNull()
  })

  it("delete external skill confirms with the external dialog and moves it to trash", async () => {
    render(<SkillsList />)
    fireEvent.click(await screen.findByText("fixture-external-skill"))
    await screen.findAllByText(/fixture body for the externally managed skill/i)

    fireEvent.click(await screen.findByRole("button", { name: /delete skill/i }))

    // The confirm dialog names the skill and the external trash path.
    expect(await screen.findByText("Delete External Skill?")).toBeTruthy()
    expect(screen.getByText(/managed by external tooling/i)).toBeTruthy()
    expect(screen.getByText(/\.trash\/skills\/fixture-external-skill\//)).toBeTruthy()

    fireEvent.click(screen.getByRole("button", { name: /move to trash/i }))

    await waitFor(() => expect(state.deleteCalls).toEqual([EXTERNAL_ID]))
    await waitFor(() => expect(screen.queryByText("fixture-external-skill")).toBeNull())
  })

  it("restores a trashed external skill from trash view", async () => {
    render(<SkillsList />)
    fireEvent.click(await screen.findByText("fixture-external-skill"))
    await screen.findAllByText(/fixture body for the externally managed skill/i)

    fireEvent.click(await screen.findByRole("button", { name: /delete skill/i }))
    fireEvent.click(await screen.findByRole("button", { name: /move to trash/i }))
    await waitFor(() => expect(state.deleteCalls).toEqual([EXTERNAL_ID]))

    // Trash view re-includes the trashed external row; Restore is offered.
    fireEvent.click(screen.getByRole("switch", { name: /show trashed/i }))
    expect(await screen.findByText("fixture-external-skill")).toBeTruthy()

    fireEvent.click(screen.getByText("fixture-external-skill"))
    fireEvent.click(await screen.findByRole("button", { name: /restore/i }))

    await waitFor(() => expect(state.restoreCalls).toEqual([EXTERNAL_ID]))
  })

  it("save calls PUT /api/skills/{id} and returns to view mode", async () => {
    render(<SkillsList />)
    fireEvent.click(await screen.findByText("fixture-eling-skill"))
    // Detail must land before Edit is enabled (canEdit requires the full skill).
    await screen.findAllByText(/fixture body for the Eling-managed skill/i)

    fireEvent.click(await screen.findByRole("button", { name: /^edit$/i }))
    const description = screen.getByLabelText(/^description$/i)
    fireEvent.change(description, { target: { value: "updated fixture description" } })
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }))

    await waitFor(() => expect(state.putBodies).toHaveLength(1))
    expect(state.putBodies[0]).toMatchObject({
      id: ELING_ID,
      name: "fixture-eling-skill",
      description: "updated fixture description",
    })

    // View mode is back: the edit affordance returns and the form is gone.
    expect(await screen.findByRole("button", { name: /^edit$/i })).toBeTruthy()
    expect(screen.queryByLabelText(/^description$/i)).toBeNull()
  })

  it("delete moves an eling skill to trash, and trash view offers restore", async () => {
    render(<SkillsList />)
    fireEvent.click(await screen.findByText("fixture-eling-skill"))
    await screen.findAllByText(/fixture body for the Eling-managed skill/i)

    fireEvent.click(await screen.findByRole("button", { name: /delete skill/i }))
    fireEvent.click(await screen.findByRole("button", { name: /move to trash/i }))

    await waitFor(() => expect(state.deleteCalls).toEqual([ELING_ID]))
    await waitFor(() => expect(screen.queryByText("fixture-eling-skill")).toBeNull())

    // Trash view re-includes the trashed row and swaps Restore in for Edit/Delete.
    fireEvent.click(screen.getByRole("switch", { name: /show trashed/i }))
    expect(await screen.findByText("fixture-eling-skill")).toBeTruthy()

    fireEvent.click(screen.getByText("fixture-eling-skill"))
    fireEvent.click(await screen.findByRole("button", { name: /restore/i }))

    await waitFor(() => expect(state.restoreCalls).toEqual([ELING_ID]))
  })
})
