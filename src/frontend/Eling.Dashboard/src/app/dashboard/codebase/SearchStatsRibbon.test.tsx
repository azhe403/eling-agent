import { describe, it, expect } from "vitest"
import { render, screen } from "@testing-library/react"
import { SearchStatsRibbon } from "./SearchStatsRibbon"

describe("SearchStatsRibbon", () => {
  it("renders match counts, timing, files, and chunks", () => {
    render(
      <SearchStatsRibbon
        stats={{
          tookMs: 25,
          hits: 14,
          files: 4,
          chunks: 14,
          workspaces: 3,
          workspacesWithHits: 2,
        }}
        scope="content"
        limit={20}
      />
    )

    expect(screen.getByText("14 matches")).toBeInTheDocument()
    expect(screen.getByText("4")).toBeInTheDocument()
    expect(screen.getByText("25")).toBeInTheDocument()
    expect(screen.getByText("3")).toBeInTheDocument()
    expect(screen.getByText(/workspaces/)).toBeInTheDocument()
    expect(screen.getByText(/\(2 with hits\)/)).toBeInTheDocument()
    expect(screen.getByText("Scope: Content (top 20)")).toBeInTheDocument()
  })

  it("handles 0 matches gracefully", () => {
    render(
      <SearchStatsRibbon
        stats={{
          tookMs: 8,
          hits: 0,
          files: 0,
          chunks: 0,
          workspaces: 1,
          workspacesWithHits: 0,
        }}
        scope="file"
        limit={20}
      />
    )

    expect(screen.getByText("No matches")).toBeInTheDocument()
    expect(screen.getByText("Scope: File (top 20)")).toBeInTheDocument()
  })
})
