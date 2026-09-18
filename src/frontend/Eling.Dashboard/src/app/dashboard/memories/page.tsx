import { Suspense } from "react"
import { MemoriesList } from "./MemoriesList"
import { Skeleton } from "@/components/ui/skeleton"

function MemoriesLoading() {
  return (
    <div className="flex flex-1 flex-col gap-4 p-4 pt-4">
      <div className="flex flex-col gap-2">
        {Array.from({ length: 5 }).map((_, i) => (
          <Skeleton key={i} className="h-20 w-full rounded-xl" />
        ))}
      </div>
    </div>
  )
}

export default function MemoriesPage() {
  return (
    <Suspense fallback={<MemoriesLoading />}>
      <MemoriesList />
    </Suspense>
  )
}
