import { describe, it, expect, beforeEach } from "vitest"
import {
  tabLabel,
  createTab,
  addTab,
  closeTab,
  updateTab,
  loadTabs,
  saveTabs,
  type SearchTab,
} from "./searchTabs"

describe("tabLabel", () => {
  it("returns trimmed query when present", () => {
    const tab: SearchTab = {
      id: "tab-1",
      query: "  search query  ",
      scope: "content",
      selection: { kind: "all" },
    }
    expect(tabLabel(tab)).toBe("search query")
  })

  it("returns 'New search' when query is empty", () => {
    const tab: SearchTab = {
      id: "tab-1",
      query: "   ",
      scope: "content",
      selection: { kind: "all" },
    }
    expect(tabLabel(tab)).toBe("New search")
  })
})

describe("createTab", () => {
  it("generates a tab with default properties", () => {
    const tab = createTab()
    expect(tab.id).toBeTruthy()
    expect(tab.query).toBe("")
    expect(tab.scope).toBe("content")
    expect(tab.selection).toEqual({ kind: "all" })
  })

  it("allows partial overrides", () => {
    const tab = createTab({
      query: "find me",
      scope: "file",
      selection: { kind: "workspaces", roots: ["/repo/a"] },
    })
    expect(tab.query).toBe("find me")
    expect(tab.scope).toBe("file")
    expect(tab.selection).toEqual({ kind: "workspaces", roots: ["/repo/a"] })
  })
})

describe("addTab", () => {
  it("appends a tab and marks it as active", () => {
    const t1 = createTab({ id: "t1" })
    const t2 = createTab({ id: "t2" })
    const res = addTab([t1], t2)
    expect(res.tabs).toEqual([t1, t2])
    expect(res.activeId).toBe("t2")
  })
})

describe("closeTab", () => {
  it("removes tab and keeps active tab if different tab closed", () => {
    const t1 = createTab({ id: "t1" })
    const t2 = createTab({ id: "t2" })
    const t3 = createTab({ id: "t3" })
    const res = closeTab([t1, t2, t3], "t2", "t1")
    expect(res.tabs.map((t) => t.id)).toEqual(["t1", "t3"])
    expect(res.activeId).toBe("t1")
  })

  it("activates previous tab when active tab is closed", () => {
    const t1 = createTab({ id: "t1" })
    const t2 = createTab({ id: "t2" })
    const t3 = createTab({ id: "t3" })
    const res = closeTab([t1, t2, t3], "t2", "t2")
    expect(res.tabs.map((t) => t.id)).toEqual(["t1", "t3"])
    expect(res.activeId).toBe("t1")
  })

  it("activates next tab when first active tab is closed", () => {
    const t1 = createTab({ id: "t1" })
    const t2 = createTab({ id: "t2" })
    const res = closeTab([t1, t2], "t1", "t1")
    expect(res.tabs.map((t) => t.id)).toEqual(["t2"])
    expect(res.activeId).toBe("t2")
  })

  it("never leaves tabs empty: resets to a single blank tab if only tab is closed", () => {
    const t1 = createTab({ id: "t1", query: "test" })
    const res = closeTab([t1], "t1", "t1")
    expect(res.tabs.length).toBe(1)
    expect(res.tabs[0].query).toBe("")
    expect(res.activeId).toBe(res.tabs[0].id)
  })
})

describe("updateTab", () => {
  it("updates matching tab in array immutably", () => {
    const t1 = createTab({ id: "t1", query: "old" })
    const t2 = createTab({ id: "t2" })
    const next = updateTab([t1, t2], { ...t1, query: "new" })
    expect(next[0].query).toBe("new")
    expect(next[1]).toBe(t2)
  })
})

describe("loadTabs & saveTabs", () => {
  beforeEach(() => {
    localStorage.clear()
  })

  it("returns default tab when storage is empty", () => {
    const { tabs, activeId } = loadTabs()
    expect(tabs.length).toBe(1)
    expect(tabs[0].id).toBe(activeId)
    expect(tabs[0].query).toBe("")
  })

  it("saves and reloads tabs", () => {
    const t1 = createTab({ id: "t1", query: "search 1" })
    const t2 = createTab({
      id: "t2",
      query: "search 2",
      scope: "path",
      selection: { kind: "workspaces", roots: ["/r1"] },
      lastStats: {
        tookMs: 42,
        hits: 7,
        files: 3,
        chunks: 7,
        workspaces: 2,
        workspacesWithHits: 1,
        mode: "files",
      },
    })
    saveTabs([t1, t2], "t2")

    const loaded = loadTabs()
    expect(loaded.tabs.length).toBe(2)
    expect(loaded.activeId).toBe("t2")
    expect(loaded.tabs[1].query).toBe("search 2")
    expect(loaded.tabs[1].scope).toBe("path")
    expect(loaded.tabs[1].selection).toEqual({
      kind: "workspaces",
      roots: ["/r1"],
    })
    expect(loaded.tabs[1].lastStats).toEqual({
      tookMs: 42,
      hits: 7,
      files: 3,
      chunks: 7,
      workspaces: 2,
      workspacesWithHits: 1,
      mode: "files",
    })
    expect(loaded.tabs[0].lastStats).toBeUndefined()
  })
})
