// Browser tab titles. Every route names itself through here, so the tab strip
// reads the same shape everywhere instead of each page inventing its own.
//
// The dashboard ships as a static export, so the title has to be right in the
// prerendered HTML. A title set from an effect would show the wrong value
// until React boots, and would never reach the exported file the backend
// serves at all - the tab would keep a stale title for the whole session.

const APP_NAME = "Eling"

// `process.env.NODE_ENV` is inlined by Next.js at build time, so the "Dev"
// marker exists only in the `next dev` bundle. The static export the backend
// serves in production is always built with NODE_ENV=production and therefore
// always reads as plain "Eling": a dev tab can never be mistaken for a
// production one, and no extra flag has to travel from the backend to the
// browser to make that happen.
export const APP_TITLE =
  process.env.NODE_ENV === "development" ? `${APP_NAME} Dev` : APP_NAME

// The tab title of a page: "Eling - Memories" in production,
// "Eling Dev - Memories" in development. The page name matches the label in
// the page header, so the tab and the breadcrumb never disagree.
export function pageTitle(page: string): string {
  return `${APP_TITLE} - ${page}`
}
