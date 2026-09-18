"use client"

import * as React from "react"
import Link from "next/link"
import {
  Brain,
  LayoutDashboard,
  Database,
} from "lucide-react"

import { NavMain } from "@/components/nav-main"
import { NavUser } from "@/components/nav-user"
import { ThemeToggle } from "@/components/theme-toggle"
import { useMemoriesSse } from "@/hooks/use-memories-sse"
import {
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarHeader,
  SidebarMenu,
  SidebarMenuButton,
  SidebarMenuItem,
  SidebarRail,
} from "@/components/ui/sidebar"
import type { Runtime } from "@/lib/types"

const user = {
  name: "Bang Azhe",
  email: "developer@eling.local",
  avatar: "",
}

export function AppSidebar({ ...props }: React.ComponentProps<typeof Sidebar>) {
  const [runtimes, setRuntimes] = React.useState<Runtime[]>([])

  const refreshRuntimes = React.useCallback(async () => {
    try {
      const res = await fetch(`/api/coordinator/runtimes?_t=${Date.now()}`, {
        cache: "no-store",
        headers: { "Cache-Control": "no-cache", Pragma: "no-cache" },
      })
      if (res.ok) {
        const data = await res.json()
        if (Array.isArray(data)) {
          setRuntimes(data)
        }
      }
    } catch {
      // ignore
    }
  }, [])

  React.useEffect(() => {
    let cancelled = false
    async function load() {
      try {
        const res = await fetch(`/api/coordinator/runtimes?_t=${Date.now()}`, {
          cache: "no-store",
          headers: { "Cache-Control": "no-cache", Pragma: "no-cache" },
        })
        if (res.ok && !cancelled) {
          const data = await res.json()
          if (Array.isArray(data)) {
            setRuntimes(data)
          }
        }
      } catch {
        // ignore
      }
    }
    void load()
    return () => {
      cancelled = true
    }
  }, [])

  // Realtime runtimes update via SSE
  useMemoriesSse(React.useCallback(() => {}, []), refreshRuntimes)

  const navMain = React.useMemo(() => {
    const dynamicProjectScopes = runtimes.map((r) => {
      const projectName =
        r.projectRoot.split("\\").pop() ?? r.projectRoot.split("/").pop() ?? "Project"
      return {
        title: `📁 ${projectName}`,
        url: `/dashboard/memories?scope=${encodeURIComponent(r.projectRoot)}`,
      }
    })

    return [
      {
        title: "Dashboard",
        url: "/dashboard",
        icon: <LayoutDashboard className="size-4" />,
        isActive: true,
      },
      {
        title: "Memories",
        url: "/dashboard/memories",
        icon: <Database className="size-4" />,
        isActive: true,
        items: [
          { title: "All Memories", url: "/dashboard/memories" },
          { title: "🌐 Global Scope", url: "/dashboard/memories?scope=global" },
          ...dynamicProjectScopes,
        ],
      },
    ]
  }, [runtimes])

  return (
    <Sidebar collapsible="icon" {...props}>
      <SidebarHeader>
        <SidebarMenu>
          <SidebarMenuItem>
            <SidebarMenuButton size="lg" render={<Link href="/dashboard/" />}>
              <div className="flex aspect-square size-8 items-center justify-center rounded-lg bg-sidebar-primary text-sidebar-primary-foreground">
                <Brain className="size-4" />
              </div>
              <div className="flex flex-col gap-0.5 leading-none">
                <span className="font-medium">Eling</span>
                <span className="text-xs text-muted-foreground">Memory Platform</span>
              </div>
            </SidebarMenuButton>
          </SidebarMenuItem>
        </SidebarMenu>
      </SidebarHeader>
      <SidebarContent>
        <NavMain items={navMain} />
      </SidebarContent>
      <SidebarFooter className="gap-2">
        <div className="px-1 group-data-[collapsible=icon]:hidden">
          <ThemeToggle />
        </div>
        <NavUser user={user} />
      </SidebarFooter>
      <SidebarRail />
    </Sidebar>
  )
}
