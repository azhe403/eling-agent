import { describe, it, expect, vi, afterEach } from "vitest"
import { render } from "@testing-library/react"

import { DocDetailPanel } from "./DocDetailPanel"
import type { DocListItem, DocReadResult } from "@/lib/types"

const doc: DocListItem = {
  id: "managed:current:abc123",
  relPath: ".eling/docs/specs/example.md",
  ownership: 0,
  origin: 0,
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

  it("says content is unavailable instead of rendering nothing", () => {
    const { getByText } = render(
      <DocDetailPanel {...baseProps} detailLoading={false} detail={detail} />,
    )

    expect(getByText("Content not available for this document.")).toBeDefined()
  })
})
