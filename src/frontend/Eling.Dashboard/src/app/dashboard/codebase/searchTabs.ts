import type { WorkspaceSelection } from "@/components/workspaces/selection"
import { selectAll } from "@/components/workspaces/selection"
import type { SearchScope } from "./scope"

export type SearchTab = {
  id: string
  query: string
  scope: SearchScope
  selection: WorkspaceSelection
}

export const STORAGE_KEY = "eling.codebase.tabs"
const LEGACY_STORAGE_KEY = "eling.codebase.search"

export function tabLabel(tab: SearchTab): string {
  const trimmed = tab.query.trim()
  return trimmed || "New search"
}

function generateId(): string {
  return `tab_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 6)}`
}

export function createTab(existing?: Partial<SearchTab>): SearchTab {
  return {
    id: existing?.id || generateId(),
    query: existing?.query ?? "",
    scope: existing?.scope ?? "content",
    selection: existing?.selection ?? selectAll(),
  }
}

export function addTab(
  tabs: SearchTab[],
  newTab: SearchTab = createTab()
): { tabs: SearchTab[]; activeId: string } {
  return {
    tabs: [...tabs, newTab],
    activeId: newTab.id,
  }
}

export function closeTab(
  tabs: SearchTab[],
  closeId: string,
  activeId: string
): { tabs: SearchTab[]; activeId: string } {
  const index = tabs.findIndex((t) => t.id === closeId)
  if (index === -1) return { tabs, activeId }

  const nextTabs = tabs.filter((t) => t.id !== closeId)
  if (nextTabs.length === 0) {
    const fallback = createTab()
    return { tabs: [fallback], activeId: fallback.id }
  }

  if (activeId !== closeId) {
    return { tabs: nextTabs, activeId }
  }

  // Active tab was closed. Pick the previous tab, or the new first tab if we closed index 0.
  const nextActiveIndex = Math.max(0, index - 1)
  return {
    tabs: nextTabs,
    activeId: nextTabs[nextActiveIndex].id,
  }
}

export function updateTab(
  tabs: SearchTab[],
  updated: SearchTab
): SearchTab[] {
  return tabs.map((t) => (t.id === updated.id ? updated : t))
}

type StoredState = {
  tabs: SearchTab[]
  activeId: string
}

export function loadTabs(
  fallbackQuery?: string,
  fallbackScope?: SearchScope,
  fallbackSelection?: WorkspaceSelection
): { tabs: SearchTab[]; activeId: string } {
  if (typeof window === "undefined") {
    const initial = createTab({
      query: fallbackQuery,
      scope: fallbackScope,
      selection: fallbackSelection,
    })
    return { tabs: [initial], activeId: initial.id }
  }

  try {
    const raw = window.localStorage.getItem(STORAGE_KEY)
    if (raw) {
      const parsed = JSON.parse(raw) as Partial<StoredState>
      if (Array.isArray(parsed.tabs) && parsed.tabs.length > 0) {
        const validTabs: SearchTab[] = parsed.tabs.map((t) => ({
          id: t.id || generateId(),
          query: typeof t.query === "string" ? t.query : "",
          scope: t.scope === "path" || t.scope === "file" ? t.scope : "content",
          selection:
            t.selection && typeof t.selection === "object"
              ? t.selection
              : selectAll(),
        }))
        const activeExists = validTabs.some((t) => t.id === parsed.activeId)
        const activeId = activeExists ? (parsed.activeId as string) : validTabs[0].id
        return { tabs: validTabs, activeId }
      }
    }
  } catch {
    // Ignore JSON parse or storage errors
  }

  // Check legacy single-query key
  let legacyQuery = ""
  try {
    legacyQuery = window.localStorage.getItem(LEGACY_STORAGE_KEY) ?? ""
  } catch {
    // Ignore
  }

  const initial = createTab({
    query: fallbackQuery || legacyQuery || "",
    scope: fallbackScope,
    selection: fallbackSelection,
  })
  return { tabs: [initial], activeId: initial.id }
}

export function saveTabs(tabs: SearchTab[], activeId: string): void {
  if (typeof window === "undefined") return
  try {
    const state: StoredState = { tabs, activeId }
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(state))
  } catch {
    // Ignore quota or write errors
  }
}
