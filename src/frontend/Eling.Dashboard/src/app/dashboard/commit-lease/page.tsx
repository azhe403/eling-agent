import type { Metadata } from "next"
import { Suspense } from "react"
import { CommitLease } from "./CommitLease"
import { Skeleton } from "@/components/ui/skeleton"
import { pageTitle } from "@/lib/page-title"

export const metadata: Metadata = {
  title: pageTitle("Commit Lease"),
}

function Loading() {
  return (
    <div className="flex flex-1 flex-col gap-4 p-4 pt-4">
      <Skeleton className="h-10 w-full rounded-xl" />
      <Skeleton className="h-48 w-full rounded-xl" />
      <Skeleton className="h-48 w-full rounded-xl" />
    </div>
  )
}

export default function CommitLeasePage() {
  return (
    <Suspense fallback={<Loading />}>
      <CommitLease />
    </Suspense>
  )
}
