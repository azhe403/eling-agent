"use client"

import { useState } from "react"

const PREVIEW_COUNT = 3

/**
 * The paths a lease touches, clipped to three entries with an inline toggle
 * for the rest. A commit lease can carry a very long list (a whole staged
 * tree); rendering them all would make every card taller than the grid is
 * wide, but hiding them entirely was the expand-click this replaces.
 */
export function LeasePaths({ paths }: { paths: string[] }) {
  const [expanded, setExpanded] = useState(false)
  const visiblePaths = expanded ? paths : paths.slice(0, PREVIEW_COUNT)
  const hiddenCount = paths.length - PREVIEW_COUNT

  if (paths.length === 0) {
    return <div className="text-xs text-muted-foreground">—</div>
  }

  return (
    <div className="min-w-0">
      <div className="text-[11px] text-muted-foreground">Paths</div>
      <ul className="mt-1 flex flex-col gap-1 font-mono text-xs">
        {visiblePaths.map((p) => (
          <li key={p} className="break-all" title={p}>
            {p}
          </li>
        ))}
      </ul>
      {hiddenCount > 0 && (
        <button
          type="button"
          onClick={() => setExpanded((current) => !current)}
          aria-expanded={expanded}
          className="mt-1 text-xs text-muted-foreground transition-colors hover:text-foreground"
        >
          {expanded ? "Show less" : `+${hiddenCount} more`}
        </button>
      )}
    </div>
  )
}
