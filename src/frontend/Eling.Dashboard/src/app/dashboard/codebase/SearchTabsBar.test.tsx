import { describe, it, expect, vi } from "vitest"
import { render, screen } from "@testing-library/react"
import { SearchTabsBar } from "./SearchTabsBar"
import type { SearchStats, SearchTab } from "./searchTabs"

function makeTab(id: string, query: string, overrides: Partial<SearchTab> = {}): SearchTab {
  return {
    id,
    query,
    scope: "content",
    selection: { kind: "all" },
    ...overrides,
  }
}

function makeStats(overrides: Partial<SearchStats> = {}): SearchStats {
  return {
    tookMs: 42,
    hits: 20,
    totalHits: 238,
    files: 19,
    chunks: 20,
    workspaces: 50,
    workspacesWithHits: 1,
    ...overrides,
  }
}

function renderBar(tabs: SearchTab[], activeId = tabs[0]?.id ?? "") {
  const handlers = {
    onSelectTab: vi.fn(),
    onCloseTab: vi.fn(),
    onAddTab: vi.fn(),
  }
  render(<SearchTabsBar tabs={tabs} activeId={activeId} {...handlers} />)
  return handlers
}

describe("SearchTabsBar", () => {
  it("renders one tab per search, each with its query as the label", () => {
    renderBar([
      makeTab("tab-1", "resignation"),
      makeTab("tab-2", "a"),
      makeTab("tab-3", "SearchTabsBar"),
    ])

    const tabs = screen.getAllByRole("tab")
    expect(tabs).toHaveLength(3)
    expect(tabs[0].textContent).toContain("resignation")
    expect(tabs[1].textContent).toContain("a")
    expect(tabs[2].textContent).toContain("SearchTabsBar")
  })

  it("marks only the active tab as selected", () => {
    renderBar([makeTab("tab-1", "one"), makeTab("tab-2", "two")], "tab-2")

    const tabs = screen.getAllByRole("tab")
    expect(tabs[0].getAttribute("aria-selected")).toBe("false")
    expect(tabs[1].getAttribute("aria-selected")).toBe("true")
  })

  it("gives each tab its own width instead of a fixed one, so a long query is not cut short", () => {
    renderBar([makeTab("tab-1", "resignation")])

    // The width is the content's, not a constant, so no fixed width utility
    // may creep back in (the old one was `w-38`).
    const tab = screen.getByRole("tab")
    expect(tab.className).toContain("w-auto")
    expect(tab.className).not.toContain("w-38")
  })

  it("does not clip the label, so a long query is shown in full", () => {
    renderBar([makeTab("tab-1", "resignation")])

    // The tab is sized to its content and the strip scrolls, so the label
    // must not carry a clipping utility. `truncate` here would silently
    // bring back the cut-off label the change removed.
    const label = screen.getByText("resignation")
    expect(label.className).not.toContain("truncate")
    expect(label.className).not.toContain("overflow-hidden")
    expect(label.className).toContain("whitespace-nowrap")
  })

  it("keeps the new-tab button outside the scrolling tab list so it stays visible", () => {
    renderBar([makeTab("tab-1", "one"), makeTab("tab-2", "two")])

    const tabList = screen.getByRole("tablist")
    const addButton = screen.getByRole("button", { name: "New search tab" })

    // The list scrolls; the button does not scroll with it.
    expect(tabList.className).toContain("overflow-x-auto")
    expect(tabList.contains(addButton)).toBe(false)
  })

  it("owns its tabs directly, so the tab list has no invalid children", () => {
    renderBar([makeTab("tab-1", "one"), makeTab("tab-2", "two")])

    // role="tab" belongs directly under role="tablist"; the close buttons
    // live inside the tabs, and the new-tab button lives outside.
    const tabList = screen.getByRole("tablist")
    const children = Array.from(tabList.children)
    expect(children).toHaveLength(2)
    for (const child of children) {
      expect(child.getAttribute("role")).toBe("tab")
    }
  })

  it("selects a tab when it is clicked", () => {
    const handlers = renderBar([makeTab("tab-1", "one"), makeTab("tab-2", "two")])

    screen.getAllByRole("tab")[1].click()
    expect(handlers.onSelectTab).toHaveBeenCalledWith("tab-2")
  })

  it("closes a tab without selecting it", () => {
    const handlers = renderBar([makeTab("tab-1", "one"), makeTab("tab-2", "two")])

    screen.getByRole("button", { name: "Close tab two" }).click()
    expect(handlers.onCloseTab).toHaveBeenCalledWith("tab-2")
    expect(handlers.onSelectTab).not.toHaveBeenCalled()
  })

  it("offers no close button when only one tab is open", () => {
    renderBar([makeTab("tab-1", "one")])

    expect(screen.queryByRole("button", { name: /Close tab/ })).toBeNull()
  })

  it("shows the match count and the workspace count beside the label", () => {
    renderBar([
      makeTab("tab-1", "resignation", {
        selection: { kind: "workspaces", roots: ["a", "b", "c"] },
        lastStats: makeStats(),
      }),
    ])

    const tab = screen.getByRole("tab")
    expect(tab.textContent).toContain("20/238")
    // The workspace count is 3, a number the match badge does not contain.
    expect(tab.textContent).toContain("3")
  })

  it("does not fill the active tab's count badge with the primary colour, which is near-white in dark mode", () => {
    renderBar([makeTab("tab-1", "resignation", { lastStats: makeStats() })])

    // --primary is oklch(0.922) in dark mode: a solid badge on it reads as a
    // glaring white block. The badge stays muted and earns the active state
    // from its brighter text instead.
    const badge = screen.getByText("20/238")
    expect(badge.className).not.toContain("bg-primary")
    expect(badge.className).toContain("bg-muted")
    expect(badge.className).toContain("text-foreground")
    // The inactive badge dims its text; the active one must not.
    expect(badge.className).not.toContain("text-muted-foreground")
  })
})
