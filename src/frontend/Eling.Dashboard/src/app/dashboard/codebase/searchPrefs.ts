/**
 * Remembers the last query this browser ran on the codebase page.
 *
 * localStorage rather than component state, because a search is a working
 * position the reader comes back to: open a file, look at something else,
 * come back - and the box is empty and the query has to be typed again. The
 * URL carries the query too (see `scope.ts`), which is what makes a search
 * shareable and survives a reload; this is the part that survives a visit
 * to a bare address, where the URL has no query to carry.
 *
 * The same split the memories page makes for its selection: the URL is the
 * source of truth the moment it says anything, and this only fills in an
 * address that says nothing.
 *
 * Never throws: private browsing and full storage both make localStorage
 * unusable, and a search box that cannot remember a query should still type.
 */

const STORAGE_KEY = "eling.codebase.search"

/** The last query, or an empty string when there is none. */
export function readQuery(): string {
  if (typeof window === "undefined") return ""
  try {
    return window.localStorage.getItem(STORAGE_KEY) ?? ""
  } catch {
    return ""
  }
}

/**
 * Records the query, so the next visit to the page starts where this one
 * left off. A blank query is forgotten rather than stored: it is the same
 * as never having searched.
 */
export function rememberQuery(query: string) {
  if (typeof window === "undefined") return
  try {
    if (query.trim()) window.localStorage.setItem(STORAGE_KEY, query)
    else window.localStorage.removeItem(STORAGE_KEY)
  } catch {
    // A store that rejects writes. The session still works; it just will
    // not remember.
  }
}
