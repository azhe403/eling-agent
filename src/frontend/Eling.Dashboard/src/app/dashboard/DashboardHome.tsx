"use client"

import Link from "next/link"
import { Plus, RefreshCw } from "lucide-react"

import { DashboardHeader } from "@/components/dashboard-header"
import { Button } from "@/components/ui/button"
import { Skeleton } from "@/components/ui/skeleton"
import { cn } from "@/lib/utils"
import { useDashboardData } from "./home/data"
import { RecentActivity } from "./home/recent-activity"
import { RecentMemories } from "./home/recent-memories"
import { PlatformStatus } from "./home/platform"
import { VitalsRow } from "./home/vitals"
import { WorkspaceList } from "./home/workspaces"

/**
 * The home page.
 *
 * It answers three questions, in the order a reader asks them: is the
 * platform healthy, what did it learn, and where can I go from here. The
 * vitals row answers the first and doubles as navigation; the two feeds
 * answer the second; the sidebar and the links in every card header answer
 * the third.
 *
 * The whole page is a client component on purpose. The dashboard ships as a
 * static export, so a server component's fetch would run at BUILD time and
 * its numbers would be frozen into the HTML forever. Reading in the browser
 * is what lets the page show now, and keep showing now.
 */
export function DashboardHome() {
  const { data, loading, refreshing, streamStatus, refresh } = useDashboardData()

  return (
    <>
      <DashboardHeader label="Dashboard">
        <Button
          variant="outline"
          size="sm"
          nativeButton={false}
          render={<Link href="/dashboard/create" />}
        >
          <Plus className="size-4" />
          New memory
        </Button>
        <Button
          variant="ghost"
          size="sm"
          onClick={refresh}
          disabled={refreshing}
          aria-label="Refresh dashboard"
        >
          <RefreshCw className={cn("size-4", refreshing && "animate-spin")} />
          Refresh
        </Button>
      </DashboardHeader>

      <div className="flex flex-1 flex-col gap-4 p-4 pt-0">
        {loading ? (
          <LoadingBody />
        ) : (
          <>
            <VitalsRow data={data} />
            <div className="grid gap-4 lg:grid-cols-3">
              <div className="flex flex-col gap-4 lg:col-span-2">
                <RecentMemories section={data.memories} />
                <RecentActivity section={data.activity} />
              </div>
              <div className="flex flex-col gap-4">
                <PlatformStatus data={data} streamStatus={streamStatus} />
                <WorkspaceList section={data.workspaces} />
              </div>
            </div>
          </>
        )}
      </div>
    </>
  )
}

/**
 * The first paint, before any read has come back. The shape matches the
 * loaded layout so the page does not jump when the data arrives.
 */
function LoadingBody() {
  return (
    <>
      <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        {Array.from({ length: 4 }).map((_, i) => (
          <Skeleton key={i} className="h-28 w-full rounded-xl" />
        ))}
      </div>
      <div className="grid gap-4 lg:grid-cols-3">
        <Skeleton className="h-80 w-full rounded-xl lg:col-span-2" />
        <Skeleton className="h-80 w-full rounded-xl" />
      </div>
    </>
  )
}
