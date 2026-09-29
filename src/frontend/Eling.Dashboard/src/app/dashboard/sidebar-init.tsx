"use client"

import * as React from "react"
import { SidebarProvider } from "@/components/ui/sidebar"
import { SIDEBAR_STORAGE_KEY, isSidebarCollapsed } from "@/lib/sidebar-state"

// Wraps the project's SidebarProvider so the initial open state can be read
// from the DOM (data-sidebar-collapsed) at hydration time, instead of from
// a server-side cookies() call. This keeps the route statically prerenderable
// under output: "export".
//
// Flow:
//   1. InlineScript in the root layout sets data-sidebar-collapsed on <html>
//      before first paint, reading the same localStorage key the client uses.
//   2. This hook reads that storage via useSyncExternalStore so the server
//      renders defaultOpen=true (matches the inline script's default), the
//      client renders defaultOpen=stored on first render (matches the data
//      attribute the inline script already set), and React's getServerSnapshot
//      guarantees no hydration mismatch. No setState-in-effect required.
const emptySubscribe = () => () => {}

function getSidebarOpenSnapshot(): boolean {
  try {
    const stored = localStorage.getItem(SIDEBAR_STORAGE_KEY)
    // Mirror the convention used by the InlineScript in the root layout:
    // anything other than the literal string "false" means "open".
    return !isSidebarCollapsed(stored)
  } catch {
    return true
  }
}

function getServerSidebarOpenSnapshot(): boolean {
  return true
}

export function DashboardSidebar({
  children,
  style,
}: {
  children: React.ReactNode
  style?: React.CSSProperties
}) {
  const defaultOpen = React.useSyncExternalStore(
    emptySubscribe,
    getSidebarOpenSnapshot,
    getServerSidebarOpenSnapshot,
  )

  // SidebarProvider captures defaultOpen via useState (initial render only), so
  // the post-hydration correction (server true -> stored false) would otherwise
  // leave it stuck expanded while <html> says collapsed — expanded content in
  // a narrow container reads as a "dented" sidebar. Remounting on that single
  // transition aligns them without effects. No-op when already open.
  return (
    <SidebarProvider key={String(defaultOpen)} defaultOpen={defaultOpen} style={style}>
      {children}
    </SidebarProvider>
  )
}
