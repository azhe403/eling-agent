/**
 * Remembers where the file viewer was left: which of the two views was showing,
 * and which chunk was selected for each file.
 *
 * localStorage rather than component state, because "remember the last position"
 * only earns its keep if it survives a reload. Reading a file on the codebase
 * page is a repeated activity — open, look, close, open the next one — and
 * re-deriving the same view every time is the friction this removes.
 *
 * The tab is global because it is a viewing preference. The chunk index is
 * per file, because "the part of this file I was reading" means nothing for a
 * different file.
 */

const STORAGE_KEY = "eling.codebase.fileDialog"

/**
 * Upper bound on remembered files. A long browsing session can touch more than
 * this, and an unbounded map in localStorage is a slow leak — the oldest entry
 * is dropped once the cap is passed, not the least recently used, which is
 * good enough for a navigation aid.
 */
const MAX_TRACKED_FILES = 20

export type ViewerTab = "chunks" | "file"

type StoredPrefs = {
  tab?: ViewerTab
  chunksByFile?: Record<string, number>
}

/**
 * Identity for one file across projects. The relative path alone is not enough:
 * the same `src/index.ts` can exist in two indexed workspaces, and restoring the
 * wrong project's chunk index for it would be quietly wrong. Matches the key
 * FileDetailDialog already uses to remount on file change.
 */
export function fileId(projectRoot: string, path: string) {
  return `${projectRoot}::${path}`
}

/**
 * Never throws. Private browsing and full storage both make localStorage
 * unusable, and a viewer that cannot remember a position should still open.
 */
function readPrefs(): StoredPrefs {
  if (typeof window === "undefined") return {}
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY)
    if (!raw) return {}
    const parsed: unknown = JSON.parse(raw)
    if (typeof parsed !== "object" || parsed === null) return {}
    return parsed as StoredPrefs
  } catch {
    return {}
  }
}

function writePrefs(prefs: StoredPrefs) {
  if (typeof window === "undefined") return
  try {
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(prefs))
  } catch {
    // A write that does not fit, or a store that rejects writes. The session
    // still works; it just will not remember.
  }
}

export function readTab(): ViewerTab {
  return readPrefs().tab ?? "chunks"
}

export function readChunkIndex(id: string): number | undefined {
  const index = readPrefs().chunksByFile?.[id]
  return typeof index === "number" ? index : undefined
}

/** Records which view is showing. Called on switch, so the next open matches. */
export function rememberTab(tab: ViewerTab) {
  writePrefs({ ...readPrefs(), tab })
}

/**
 * Records the selected chunk for one file, dropping the oldest entry past
 * {@link MAX_TRACKED_FILES}.
 */
export function rememberChunk(id: string, index: number) {
  const prefs = readPrefs()
  const chunksByFile = { ...prefs.chunksByFile, [id]: index }
  const keys = Object.keys(chunksByFile)
  for (const stale of keys.slice(0, Math.max(0, keys.length - MAX_TRACKED_FILES))) {
    delete chunksByFile[stale]
  }
  writePrefs({ ...prefs, chunksByFile })
}
