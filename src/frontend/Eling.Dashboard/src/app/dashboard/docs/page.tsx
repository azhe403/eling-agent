import type { Metadata } from "next"
import { Suspense } from "react"
import { DocsList } from "./DocsList"
import { Skeleton } from "@/components/ui/skeleton"
import { pageTitle } from "@/lib/page-title"

export const metadata: Metadata = {
  title: pageTitle("Docs"),
}

function DocsLoading() {
  return (
    <div className="flex min-h-0 w-full flex-1 overflow-hidden">
      <div aria-hidden className="hidden w-72 shrink-0 flex-col gap-2 self-start border-r p-3 lg:flex">
        {Array.from({ length: 6 }).map((_, i) => (
          <Skeleton key={i} className="h-7 w-full" />
        ))}
      </div>
      <div className="flex min-w-0 flex-1 flex-col">
        <div className="flex flex-wrap items-center gap-2 px-4 pt-4">
          <Skeleton className="h-8 w-56" />
          <Skeleton className="h-8 w-24" />
          <Skeleton className="h-8 w-16" />
        </div>
        <div className="flex flex-col gap-2.5 p-4">
          {Array.from({ length: 5 }).map((_, i) => (
            <Skeleton key={i} className="h-20 w-full rounded-xl" />
          ))}
        </div>
      </div>
    </div>
  )
}

export default function DocsPage() {
  return (
    <Suspense fallback={<DocsLoading />}>
      <DocsList />
    </Suspense>
  )
}
