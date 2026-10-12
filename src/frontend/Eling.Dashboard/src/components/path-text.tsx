"use client"

import { useEffect, useRef } from "react"
import { cn } from "cn"

const ELLIPSIS = "…"

/**
 * Filesystem path that ellipsizes in the MIDDLE when it overflows its
 * container, instead of the CSS default of eating the tail:
 * "<home>\…\codebase\az-4e1ab010b03154df.db".
 *
 * A path's tail is its identity — the file or folder name — so a right-side
 * truncate removes exactly the part worth keeping while leaving the shared
 * prefix behind. Both ends stay visible here, and the full path is always
 * available on hover via the title. Recomputes when the container resizes.
 */
export function PathText({
  path,
  prefix,
  className,
}: {
  path: string
  prefix?: string
  className?: string
}) {
  const ref = useRef<HTMLSpanElement>(null)

  useEffect(() => {
    const el = ref.current
    if (!el) return

    const relayout = () => {
      // The full string may simply fit.
      el.textContent = path
      if (el.scrollWidth <= el.clientWidth) return

      // Head and tail grow together, so a longer split is always a wider
      // string: binary-search the longest split that still fits.
      let lo = 1
      let hi = Math.floor(path.length / 2)
      let best = `${ELLIPSIS}${path.slice(-3)}`
      while (lo <= hi) {
        const mid = (lo + hi) >> 1
        const candidate = `${path.slice(0, mid)}${ELLIPSIS}${path.slice(-mid)}`
        el.textContent = candidate
        if (el.scrollWidth <= el.clientWidth) {
          best = candidate
          lo = mid + 1
        } else {
          hi = mid - 1
        }
      }
      el.textContent = best
    }

    relayout()

    if (typeof ResizeObserver === "undefined") return
    const observer = new ResizeObserver(relayout)
    observer.observe(el)
    return () => observer.disconnect()
  }, [path])

  return (
    <div className="flex min-w-0 items-center gap-1.5 text-muted-foreground">
      {prefix ? <span className="shrink-0">{prefix}</span> : null}
      <span
        ref={ref}
        title={path}
        className={cn(
          "min-w-0 flex-1 overflow-hidden whitespace-nowrap",
          className
        )}
      >
        {path}
      </span>
    </div>
  )
}
