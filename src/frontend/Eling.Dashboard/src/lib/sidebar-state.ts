// Sidebar persistence helpers shared between the root-layout InlineScript (which
// runs during HTML parsing, before first paint) and the client-side
// DashboardSidebar wrapper (which reads the same state at hydration).
//
// Everything here is a pure function — no React, no DOM imports at module scope —
// so it can be imported by the inline script generator without pulling the client
// bundle into the server component tree.

export const SIDEBAR_STORAGE_KEY = "eling_sidebar_state"

// Any on-disk value other than the literal string "false" means "open". This
// mirrors the convention used by the inline pre-paint script so both sides agree.
export function isSidebarCollapsed(stored: string | null): boolean {
  return stored === "false"
}

// Server + initial client render both assume open (matches desktop-first default),
// avoiding a hydration mismatch. The real value is aligned right after hydration.
export function getSidebarDefaultOpen(): boolean {
  return true
}

/**
 * Returns the value used for the `data-sidebar-collapsed` attribute on <html>.
 * - null (key absent / unreadable): attribute removed (open, default).
 * - collapsed: attribute set to "true".
 * - open: attribute removed.
 */
export function getSidebarAttribute(stored: string | null): { name: string; value: string } | null {
  return isSidebarCollapsed(stored) ? { name: "data-sidebar-collapsed", value: "true" } : null
}

/**
 * Generates the inline script string used by the root layout to apply the saved
 * sidebar state before first paint and then drop the `.preload` class once the
 * page is interactive. Interpolating SIDEBAR_STORAGE_KEY keeps the magic string
 * out of layout.tsx.
 */
export function getSidebarInlineScript(): string {
  return `(function(){try{document.documentElement.classList.add('preload');var s=localStorage.getItem('${SIDEBAR_STORAGE_KEY}');if(${isSidebarCollapsed.toString()}(s)){document.documentElement.setAttribute('data-sidebar-collapsed','true')}else{document.documentElement.removeAttribute('data-sidebar-collapsed')}window.addEventListener('DOMContentLoaded',function(){requestAnimationFrame(function(){requestAnimationFrame(function(){document.documentElement.classList.remove('preload')})})})}catch(_){}})()`
}
