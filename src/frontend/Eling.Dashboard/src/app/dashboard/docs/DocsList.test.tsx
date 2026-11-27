import { beforeEach, describe, it, expect, vi } from "vitest"
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react"

import { apiFetch } from "@/lib/api"
import { DocsList } from "./DocsList"

const nav = vi.hoisted(() => ({ searchParams: new URLSearchParams(), replace: vi.fn() }))

vi.mock("next/navigation", () => ({
  useRouter: () => ({ push: vi.fn(), replace: nav.replace }),
  usePathname: () => "/dashboard/docs",
  useSearchParams: () => nav.searchParams,
}))

vi.mock("@/lib/api", () => ({
  apiFetch: vi.fn().mockImplementation((url: string) => {
    if (url.startsWith("/api/docs?")) {
      return Promise.resolve({
        total: 1,
        topics: [{
          topic: "specs",
          documents: [{
            docId: "01j00000000000000000000000",
            id: "01j00000000000000000000000",
            workspaceId: "ws-a",
            path: "docs/specs/example.md",
            archived: false,
            kind: 0,
            title: "Example Spec",
            status: 0,
            topic: "specs",
            order: 0,
            stepsTotal: 0,
            stepsDone: 0,
            mTime: new Date(Date.now() - 4 * 60 * 60 * 1000).toISOString(),
          }],
        }],
      })
    }
    return Promise.resolve({})
  }),
  ApiError: class ApiError extends Error {},
}))

vi.mock("@/components/dashboard-header", () => ({
  DashboardHeader: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
}))

vi.mock("@/hooks/use-docs-sse", () => ({
  useDocsSse: () => ({ status: "connecting" }),
}))

vi.mock("@/components/workspaces/use-workspace-rows", () => ({
  useWorkspaceRows: () => ({ rows: [], error: null, reload: vi.fn() }),
}))

vi.mock("@/components/workspaces/workspace-scope-panel", () => ({
  WorkspaceScopePanel: ({
    onSelectOnly,
    onSelectAll,
  }: {
    onSelectOnly: (root: string) => void
    onSelectAll: () => void
  }) => (
    <nav aria-label="Workspace scope">
      <button onClick={() => onSelectOnly("/ws/a")}>Only A</button>
      <button onClick={onSelectAll}>All workspaces</button>
    </nav>
  ),
}))

vi.mock("@/components/workspaces/workspace-select", () => ({
  WorkspaceSelect: () => <div>Workspace select</div>,
}))

describe("DocsList unified surface", () => {
  beforeEach(() => {
    nav.searchParams = new URLSearchParams()
    nav.replace.mockClear()
    localStorage.clear()
    vi.mocked(apiFetch).mockClear()
    Object.defineProperty(window, "matchMedia", {
      writable: true,
      value: vi.fn().mockImplementation((query: string) => ({
        matches: false,
        media: query,
        onchange: null,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
        addListener: vi.fn(),
        removeListener: vi.fn(),
        dispatchEvent: vi.fn(),
      })),
    })
  })

  it("shows the workspace sidebar, a server-side sort control and a relative-time badge", async () => {
    render(<DocsList />)

    expect(screen.getByRole("navigation", { name: "Workspace scope" })).toBeDefined()
    expect(screen.getByRole("group", { name: "Sort" })).toBeDefined()
    expect(screen.queryByText("Managed")).toBeNull()
    expect(screen.queryByText("External")).toBeNull()
    expect(await screen.findByText("Example Spec")).toBeDefined()
    expect(screen.queryByText(/ws:/)).toBeNull()
    expect(screen.getByText(/hours ago/)).toBeDefined()
  })

  it("clears sort from the URL and storage when the default is chosen from a non-default link", async () => {
    nav.searchParams = new URLSearchParams("sort=updated")

    render(<DocsList />)
    await screen.findByText("Example Spec")

    fireEvent.click(within(screen.getByRole("group", { name: "Sort" })).getByRole("button", { name: "Name" }))

    await waitFor(() => {
      const target = nav.replace.mock.calls.at(-1)?.[0] as string | undefined
      expect(target).toBeDefined()
      expect(target).not.toMatch(/[?&]sort=/)
    })
    expect(localStorage.getItem("eling:docs:sort")).toBeNull()
  })

  it("restores the workspace scope from localStorage on a bare revisit", async () => {
    localStorage.setItem("eling:docs:workspace", "/ws/a")

    render(<DocsList />)

    await waitFor(() => {
      expect(vi.mocked(apiFetch)).toHaveBeenCalledWith(
        expect.stringContaining("workspace=%2Fws%2Fa"),
        expect.anything(),
      )
    })
  })

  it("persists the workspace selection to storage and the URL, and clears both on All", async () => {
    render(<DocsList />)
    await screen.findByText("Example Spec")

    fireEvent.click(screen.getByRole("button", { name: "Only A" }))
    await waitFor(() => expect(localStorage.getItem("eling:docs:workspace")).toBe("/ws/a"))
    expect(nav.replace.mock.calls.at(-1)?.[0]).toContain("workspace=")

    fireEvent.click(screen.getByRole("button", { name: "All workspaces" }))
    await waitFor(() => expect(localStorage.getItem("eling:docs:workspace")).toBeNull())
    expect(nav.replace.mock.calls.at(-1)?.[0]).not.toContain("workspace=")
  })

  it("restores an open detail from the persisted doc param", async () => {
    nav.searchParams = new URLSearchParams("doc=01j00000000000000000000000")

    render(<DocsList />)

    await waitFor(() => {
      expect(vi.mocked(apiFetch)).toHaveBeenCalledWith(
        "/api/docs/01j00000000000000000000000",
        expect.objectContaining({ cache: "no-store" }),
      )
    })
    expect(screen.getByRole("dialog")).toBeDefined()
  })
})
