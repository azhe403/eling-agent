import type { Metadata } from "next"
import { Suspense } from "react"

import { Skeleton } from "@/components/ui/skeleton"
import { pageTitle } from "@/lib/page-title"
import { DashboardHome } from "./DashboardHome"

export const metadata: Metadata = {
  title: pageTitle("Dashboard"),
}

/**
 * The dashboard's home page.
 *
 * A thin server shell around a client page. The dashboard ships as a static
 * export, so anything fetched while rendering on the server would be read
 * once at BUILD time and frozen into the HTML - which is exactly what the
 * previous version of this page did, shipping a permanent "No data yet"
 * because the build machine had no backend to talk to. The reads happen in
 * the browser instead, so the numbers are the numbers right now.
 */
export default function DashboardPage() {
  return (
    <Suspense fallback={<DashboardFallback />}>
      <DashboardHome />
    </Suspense>
  )
}

function DashboardFallback() {
  return (
    <div className="flex flex-1 flex-col gap-4 p-4 pt-4">
      <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        {Array.from({ length: 4 }).map((_, i) => (
          <Skeleton key={i} className="h-28 w-full rounded-xl" />
        ))}
      </div>
      <div className="grid gap-4 lg:grid-cols-3">
        <Skeleton className="h-80 w-full rounded-xl lg:col-span-2" />
        <Skeleton className="h-80 w-full rounded-xl" />
      </div>
    </div>
  )
}
