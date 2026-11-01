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

    expect(screen.getByText("14 matches")).toBeDefined()
    expect(screen.getByText("4")).toBeDefined()
    expect(screen.getByText("25ms")).toBeDefined()
    expect(screen.getByText("3")).toBeDefined()
    expect(screen.getByText(/workspaces/)).toBeDefined()
    expect(screen.getByText("2 hits")).toBeDefined()
    expect(screen.getByText("Content")).toBeDefined()
    expect(screen.getByText("top 20")).toBeDefined()
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

    expect(screen.getByText("No matches")).toBeDefined()
    expect(screen.getByText("File")).toBeDefined()
    expect(screen.getByText("top 20")).toBeDefined()
  })

  it("renders recent files stats in top mode", () => {
    render(
      <SearchStatsRibbon
        stats={{
          tookMs: 12,
          hits: 20,
          files: 20,
          chunks: 95,
          workspaces: 48,
          workspacesWithHits: 3,
          mode: "top",
        }}
        scope="content"
        limit={20}
      />
    )

    expect(screen.getByText("Recent files")).toBeDefined()
    expect(screen.getByText("20")).toBeDefined()
    expect(screen.getByText("95")).toBeDefined()
    expect(screen.getByText("48")).toBeDefined()
    expect(screen.getByText("3 active")).toBeDefined()
    expect(screen.getByText("12ms")).toBeDefined()
    expect(screen.getByText("Recent")).toBeDefined()
    expect(screen.getByText("top 20")).toBeDefined()
  })
})
