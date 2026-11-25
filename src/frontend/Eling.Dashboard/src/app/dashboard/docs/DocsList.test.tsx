import { describe, it, expect, vi } from "vitest"
import { render, screen } from "@testing-library/react"

import { DocsList } from "./DocsList"

vi.mock("next/navigation", () => ({
  useRouter: () => ({ push: vi.fn(), replace: vi.fn() }),
  usePathname: () => "/dashboard/docs",
  useSearchParams: () => new URLSearchParams(),
}))

vi.mock("@/lib/api", () => ({
  apiFetch: vi.fn().mockResolvedValue({ topics: [] }),
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
  it("shows the workspace sidebar and a server-side sort control, with no ownership badges", async () => {
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
    render(<DocsList />)

    expect(screen.getByRole("navigation", { name: "Workspace scope" })).toBeDefined()
    expect(screen.getByRole("group", { name: "Sort" })).toBeDefined()
    expect(screen.queryByText("Managed")).toBeNull()
    expect(screen.queryByText("External")).toBeNull()
  })
})
