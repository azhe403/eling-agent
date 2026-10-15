import { Suspense } from "react"
import { AuditTrailList } from "./AuditTrailList"
import { Skeleton } from "@/components/ui/skeleton"

function AuditTrailLoading() {
  return (
    <div className="flex flex-1 flex-col gap-4 p-4 pt-4">
      <div className="flex flex-col gap-2">
        {Array.from({ length: 5 }).map((_, i) => (
          <Skeleton key={i} className="h-12 w-full rounded-xl" />
        ))}
      </div>
    </div>
  )
}

export default function AuditTrailPage() {
  return (
    <Suspense fallback={<AuditTrailLoading />}>
      <AuditTrailList />
    </Suspense>
  )
}
