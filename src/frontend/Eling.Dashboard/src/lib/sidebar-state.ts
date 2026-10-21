// Sidebar persistence helpers shared between the root-layout InlineScript (which
// runs during HTML parsing, before first paint) and the client-side
// DashboardSidebar wrapper (which reads the same state at hydration).
//
// Everything here is a pure function — no React, no DOM imports at module scope —
// so it can be imported by the inline script generator without pulling the client
// bundle into the server component tree.

import { COMPACT_BREAKPOINT } from "@/lib/constants"

export const SIDEBAR_STORAGE_KEY = "eling_sidebar_state"

// Routes whose sidebar always starts collapsed, whatever the viewport width and
// whatever the user last chose. The databases page renders wide tables, and
// the room those tables need is worth more than the nav labels.
export const COLLAPSED_ROUTES = ["/dashboard/databases"]

// True when the pathname belongs to a route that starts with the sidebar
// collapsed. The inline pre-paint script runs the same check, so the layout the
// browser paints before React boots matches the hydrated one.
export function isCollapsedRoute(pathname: string): boolean {
  return COLLAPSED_ROUTES.some((route) => pathname === route || pathname.startsWith(`${route}/`))
}

// The route check as browser source, shared by both inline scripts so the two
// can never disagree about which routes collapse.
const ROUTE_CHECK = `(function(p){return ${JSON.stringify(COLLAPSED_ROUTES)}.some(function(r){return p===r||p.indexOf(r+'/')===0})})(window.location.pathname)`

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
 * out of layout.tsx; ROUTE_CHECK and COMPACT_BREAKPOINT keep the collapsed
 * routes and the threshold identical to the ones the sidebar provider uses
 * after hydration.
 */
export function getSidebarInlineScript(): string {
  return `(function(){try{document.documentElement.classList.add('preload');var s=localStorage.getItem('${SIDEBAR_STORAGE_KEY}');var route=${ROUTE_CHECK};var compact=window.innerWidth<${COMPACT_BREAKPOINT};if(route||compact||${isSidebarCollapsed.toString()}(s)){document.documentElement.setAttribute('data-sidebar-collapsed','true')}else{document.documentElement.removeAttribute('data-sidebar-collapsed')}window.addEventListener('DOMContentLoaded',function(){requestAnimationFrame(function(){requestAnimationFrame(function(){document.documentElement.classList.remove('preload')})})})}catch(e){console.warn('eling sidebar preload failed',e)}})()`
}

/**
 * Generates the inline script string placed immediately after the sidebar
 * markup in the dashboard layout. The head script can only touch <html>
 * (body isn't parsed yet), so the server-rendered sidebar still carries
 * expanded attributes at first paint — wide content in a narrowed frame.
 * This script runs synchronously during parsing, right after the sidebar
 * element exists, and stamps the collapsed attributes before first paint.
 * Tailwind's group-data selectors react to the attributes via pure CSS, so
 * the first paint is already a correct narrow sidebar with no React involved.
 * On soft navigations the InlineScript wrapper renders it inert (text/plain).
 */
export function getSidebarBodyScript(): string {
  return `(function(){try{var s=localStorage.getItem('${SIDEBAR_STORAGE_KEY}');var route=${ROUTE_CHECK};var compact=window.innerWidth<${COMPACT_BREAKPOINT};if(route||compact||${isSidebarCollapsed.toString()}(s)){var el=document.querySelector('[data-slot="sidebar"]');if(el){el.setAttribute('data-state','collapsed');el.setAttribute('data-collapsible','icon')}}}catch(e){console.warn('eling sidebar pre-paint failed',e)}})()`
}
