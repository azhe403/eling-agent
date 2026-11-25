import { beforeEach, describe, it, expect, vi } from "vitest"
import { render, screen, waitFor } from "@testing-library/react"

import { apiFetch } from "@/lib/api"
import { DocsList } from "./DocsList"

const nav = vi.hoisted(() => ({ searchParams: new URLSearchParams() }))

vi.mock("next/navigation", () => ({
  useRouter: () => ({ push: vi.fn(), replace: vi.fn() }),
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
  WorkspaceScopePanel: () => <nav aria-label="Workspace scope">Workspace scope</nav>,
}))

vi.mock("@/components/workspaces/workspace-select", () => ({
  WorkspaceSelect: () => <div>Workspace select</div>,
}))

describe("DocsList unified surface", () => {
  beforeEach(() => {
    nav.searchParams = new URLSearchParams()
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
