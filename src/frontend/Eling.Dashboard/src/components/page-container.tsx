import type { ReactNode } from "react"
import { cn } from "cn"

export function PageContainer({
  className,
  children,
}: {
  className?: string
  children: ReactNode
}) {
  return (
    <div className={cn("flex w-full flex-1 flex-col gap-4 p-4 pt-4", className)}>
      {children}
    </div>
  )
}