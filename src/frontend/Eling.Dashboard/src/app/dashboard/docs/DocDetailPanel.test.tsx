import { describe, it, expect, vi, afterEach } from "vitest"
import { render, fireEvent } from "@testing-library/react"

import { DocDetailPanel } from "./DocDetailPanel"
import type { DocListItem, DocReadResult } from "@/lib/types"

vi.mock("next/navigation", () => ({
  useRouter: () => ({ push: vi.fn(), replace: vi.fn() }),
  usePathname: () => "/dashboard/docs",
  useSearchParams: () => new URLSearchParams(),
}))

const doc: DocListItem = {
  id: "01j00000000000000000000000",
  workspaceId: "ws-a",
  path: "docs/specs/example.md",
  archived: false,
  kind: 0,
  title: "Example Spec",
  status: 0,
  topic: "specs",
  order: 0,
  supersededBy: null,
  stepsTotal: 0,
  stepsDone: 0,
  fileHash: "abc123",
  mTime: "2026-01-01T00:00:00Z",
  indexedAt: "2026-01-01T00:00:00Z",
  frontMatterError: false,
  frontMatterErrorMessage: null,
}

const detail: DocReadResult = {
  document: doc,
  sections: [],
  references: [],
  relations: [],
}

const baseProps = {
  variant: "aside" as const,
  summary: doc,
  detailLoading: true,
  detail: null,
  detailError: null,
  createMode: false,
  trashMode: false,
  onClose: () => {},
  onSaveEdit: async () => {},
  onCreate: async () => {},
  onDelete: () => {},
  onPromote: () => {},
  onAddRelation: async () => {},
}

describe("DocDetailPanel hook order", () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it("keeps a stable hook order when the detail finishes loading", () => {
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => {})

    const { rerender } = render(<DocDetailPanel {...baseProps} />)
    rerender(<DocDetailPanel {...baseProps} detailLoading={false} detail={detail} />)

    const hookOrderErrors = errorSpy.mock.calls.filter((args) =>
      String(args[0] ?? "").includes("Rendered more hooks") ||
      String(args[0] ?? "").includes("change in the order of Hooks"),
    )
    expect(hookOrderErrors).toHaveLength(0)
  })

  it("shows the document's markdown so clicking a card lets the reader read it", () => {
    const withContent: DocReadResult = {
      ...detail,
      content: "# Heading\n\nBody paragraph.",
    }

    const { getByText } = render(
      <DocDetailPanel {...baseProps} detailLoading={false} detail={withContent} />,
    )

    expect(getByText(/Body paragraph\./)).toBeDefined()
  })

  it("keeps the real header in the loading state instead of a bare Loading box", () => {
    const { getByText, queryByText, container } = render(
      <DocDetailPanel {...baseProps} detailLoading={true} detail={null} />,
    )

    expect(getByText("Example Spec")).toBeDefined()
    expect(queryByText("Loading...")).toBeNull()
    // Skeleton rows mirror the panel shape rather than three random bars.
    expect(container.querySelectorAll(".animate-pulse").length).toBeGreaterThan(0)
  })

  it("says content is unavailable instead of rendering nothing", () => {
    const { getByText } = render(
      <DocDetailPanel {...baseProps} detailLoading={false} detail={detail} />,
    )

    expect(getByText("Content not available for this document.")).toBeDefined()
  })

  it("renders GFM tables, task checkboxes and code in Preview mode", () => {
    const md = "| A | B |\n|---|---|\n| 1 | 2 |\n\n- [x] done\n\n```ts\nconst x = 1\n```"
    const { getByRole, getByText } = render(
      <DocDetailPanel {...baseProps} detailLoading={false} detail={{ ...detail, content: md }} />,
    )

    expect(getByRole("table")).toBeDefined()
    expect(getByRole("checkbox")).toHaveProperty("checked", true)
    expect(getByText("const x = 1")).toBeDefined()
  })

  it("escapes raw HTML in Preview mode", () => {
    const { container } = render(
      <DocDetailPanel
        {...baseProps}
        detailLoading={false}
        detail={{ ...detail, content: "<script>alert(1)</script>" }}
      />,
    )

    expect(container.querySelector("script")).toBeNull()
  })

  it("keeps raw source text in Source mode", () => {
    const md = "# Heading\n\n| A | B |\n|---|---|\n| 1 | 2 |"
    const { getByText, getByRole } = render(
      <DocDetailPanel {...baseProps} detailLoading={false} detail={{ ...detail, content: md }} />,
    )

    fireEvent.click(getByRole("button", { name: "Source" }))
    expect(getByText(/# Heading/)).toBeDefined()
  })

  it("exposes copy buttons for id, path and topic with the full values", () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, "clipboard", {
      value: { writeText },
      configurable: true,
    })

    const { getByRole } = render(
      <DocDetailPanel {...baseProps} detailLoading={false} detail={{ ...detail, content: "body" }} />,
    )

    fireEvent.click(getByRole("button", { name: "Copy document id" }))
    fireEvent.click(getByRole("button", { name: "Copy document path" }))
    fireEvent.click(getByRole("button", { name: "Copy document topic" }))

    expect(writeText).toHaveBeenCalledWith("01j00000000000000000000000")
    expect(writeText).toHaveBeenCalledWith("docs/specs/example.md")
    expect(writeText).toHaveBeenCalledWith("specs")
  })

  it("shows a full-panel not-found state with a retry action", () => {
    const onRetry = vi.fn()
    const { getByRole, getByText } = render(
      <DocDetailPanel
        {...baseProps}
        detailLoading={false}
        detail={null}
        detailError="Document not found"
        onRetry={onRetry}
      />,
    )

    expect(getByRole("button", { name: "Close document detail" })).toBeDefined()
    expect(getByRole("heading", { name: "Document not found" })).toBeDefined()
    expect(getByText(/It may have been deleted, moved, or the index is still catching up/)).toBeDefined()
    fireEvent.click(getByRole("button", { name: "Retry" }))
    expect(onRetry).toHaveBeenCalledOnce()
  })

  it("hides YAML front-matter in Preview but shows it in Source", () => {
    const md = '---\nid: 01j00000000000000000000000\nkind: plan\ntitle: "Plan"\nstatus: 1\n---\n\n# Real Title\n\nBody text.'
    const { container, queryByText, getByText, getByRole } = render(
      <DocDetailPanel {...baseProps} detailLoading={false} detail={{ ...detail, content: md }} />,
    )

    // The persisted view choice can leak between tests; force Preview here.
    fireEvent.click(getByRole("button", { name: "Preview" }))

    // Preview must not render the YAML keys as a prose paragraph.
    expect(queryByText(/id: 01j00000000000000000000000/)).toBeNull()
    expect(getByText("Real Title")).toBeDefined()

    // Source stays honest: the full raw document including front-matter.
    fireEvent.click(getByRole("button", { name: "Source" }))
    expect(getByText(/kind: plan/)).toBeDefined()
    expect(container.querySelector(".md-preview")).toBeNull()
  })
})
