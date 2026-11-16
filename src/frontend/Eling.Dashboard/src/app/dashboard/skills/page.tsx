import type { Metadata } from "next"
import { Suspense } from "react"
import { SkillsList } from "./SkillsList"
import { Skeleton } from "@/components/ui/skeleton"
import { pageTitle } from "@/lib/page-title"

export const metadata: Metadata = {
  title: pageTitle("Skills"),
}

function SkillsLoading() {
  return (
    <div className="flex flex-1 flex-col gap-4 p-4 pt-4">
      <div className="flex flex-col gap-2">
        {Array.from({ length: 6 }).map((_, i) => (
          <Skeleton key={i} className="h-24 w-full rounded-xl" />
        ))}
      </div>
    </div>
  )
}

export default function SkillsPage() {
  return (
    <Suspense fallback={<SkillsLoading />}>
      <SkillsList />
    </Suspense>
  )
}
