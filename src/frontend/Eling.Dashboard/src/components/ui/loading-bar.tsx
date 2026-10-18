"use client"

import { cn } from "@/lib/utils"

/**
 * Thin animated progress bar used as an in-place loading indicator on top of
 * a list container (mirrors the codebase page). Renders nothing when not
 * loading so callers can drop it into a `relative` container unconditionally.
 */
export function LoadingBar({
  loading,
  className,
}: {
  loading: boolean
  className?: string
}) {
  if (!loading) return null
  return (
    <div
      className={cn(
        "absolute top-0 left-0 right-0 h-1 z-10 overflow-hidden rounded-t-lg",
        className,
      )}
    >
      <div className="absolute inset-0 h-full w-full bg-accent/30" />
      <div className="absolute inset-x-0 h-full bg-primary animate-progress-query" />
      <div className="absolute inset-x-0 h-full bg-primary/80 animate-progress-active" />
      <div className="absolute inset-x-0 h-full bg-primary/40 animate-progress-secondary-query" />
      <div className="absolute inset-x-0 h-full bg-primary/40 animate-progress-secondary-active" />
    </div>
  )
}