import { AppSidebar } from "@/components/app-sidebar"
import { SidebarInset } from "@/components/ui/sidebar"
import { InlineScript } from "@/components/inline-script"
import { getSidebarBodyScript } from "@/lib/sidebar-state"
import { DashboardSidebar } from "./sidebar-init"

export default function DashboardLayout({
  children,
}: {
  children: React.ReactNode
}) {
  return (
    <DashboardSidebar style={{ "--sidebar-width": "19rem" } as React.CSSProperties}>
      <AppSidebar />
      {/* Runs during HTML parsing, right after the sidebar markup exists, stamping
          the collapsed attributes before first paint (see getSidebarBodyScript).
          Inert on client-side navigations; React owns the attributes after hydration. */}
      <InlineScript html={getSidebarBodyScript()} />
      <SidebarInset>{children}</SidebarInset>
    </DashboardSidebar>
  )
}
