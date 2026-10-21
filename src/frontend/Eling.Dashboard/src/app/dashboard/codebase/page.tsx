import type { Metadata } from "next"
import { Suspense } from "react"
import { CodebaseList } from "./CodebaseList"
import { Skeleton } from "@/components/ui/skeleton"
import { pageTitle } from "@/lib/page-title"

export const metadata: Metadata = {
  title: pageTitle("Codebase"),
}

function Loading() {
  return (
    <div className="flex flex-1 flex-col gap-4 p-4 pt-4">
      <Skeleton className="h-10 w-full rounded-xl" />
      <Skeleton className="h-20 w-full rounded-xl" />
    </div>
  )
}

export default function CodebasePage() {
  return (
    <Suspense fallback={<Loading />}>
      <CodebaseList />
    </Suspense>
  )
}
