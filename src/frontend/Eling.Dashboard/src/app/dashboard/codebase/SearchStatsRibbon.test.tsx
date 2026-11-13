import { describe, it, expect } from "vitest"
import { render, screen } from "@testing-library/react"
import { SearchStatsRibbon, limitOptions } from "./SearchStatsRibbon"

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
    expect(screen.getByText("Max")).toBeDefined()
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
    expect(screen.getByText("Max")).toBeDefined()
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
    expect(screen.getAllByText("20").length).toBeGreaterThanOrEqual(1)
    expect(screen.getByText("95")).toBeDefined()
    expect(screen.getByText("48")).toBeDefined()
    expect(screen.getByText("3 active")).toBeDefined()
    expect(screen.getByText("12ms")).toBeDefined()
    expect(screen.getByText("Max")).toBeDefined()
  })

  it("renders the full 1-2-5 limit ladder covering the matches", () => {
    expect([...limitOptions(20, 1768)]).toEqual([5, 10, 20, 50, 100, 200, 500, 1000, 2000])
    expect([...limitOptions(200, 1768)]).toEqual([5, 10, 20, 50, 100, 200, 500, 1000, 2000])
    expect([...limitOptions(5, 1768)]).toEqual([5, 10, 20, 50, 100, 200, 500, 1000, 2000])
    // The ladder extends past matches to a round 1-2-5 number covering them.
    expect([...limitOptions(2000, 1387)]).toEqual([5, 10, 20, 50, 100, 200, 500, 1000, 2000])
    // With no matches, the ladder still reaches 200 as its floor.
    expect([...limitOptions(20, 1)]).toEqual([5, 10, 20, 50, 100, 200])
    expect([...limitOptions(5000, 100000)]).toEqual([5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000])

    const { rerender } = render(
      <SearchStatsRibbon
        stats={{ tookMs: 1, hits: 1, files: 1, chunks: 1, workspaces: 1, workspacesWithHits: 1 }}
        scope="content"
        limit={20}
      />,
    )
    expect(screen.getByRole("button", { name: "5" })).toBeDefined()
    expect(screen.getByRole("button", { name: "200" })).toBeDefined()

    rerender(
      <SearchStatsRibbon
        stats={{ tookMs: 1, hits: 1, files: 1, chunks: 1, workspaces: 1, workspacesWithHits: 1 }}
        scope="content"
        limit={200}
      />,
    )
    expect(screen.getByRole("button", { name: "200" })).toBeDefined()
    expect(screen.getByRole("button", { name: "5" })).toBeDefined()
    expect(screen.getByRole("button", { name: "10" })).toBeDefined()
  })

  it("renders '20 of 142 matches' when totalHits exceeds hits", () => {
    let clickedLimit: number | null = null
    render(
      <SearchStatsRibbon
        stats={{
          tookMs: 45,
          hits: 20,
          totalHits: 142,
          files: 12,
          chunks: 20,
          workspaces: 5,
          workspacesWithHits: 3,
        }}
        scope="content"
        limit={20}
        onLimitChange={(l) => {
          clickedLimit = l
        }}
      />
    )

    expect(screen.getByText("20 of 142 matches")).toBeDefined()
    const btn50 = screen.getByRole("button", { name: "50" })
    expect(btn50).toBeDefined()
    btn50.click()
    expect(clickedLimit).toBe(50)
  })
})
