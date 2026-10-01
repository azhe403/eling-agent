"use client"

import { cn } from "@/lib/utils"

/**
 * A read-only code view with a line-number gutter.
 *
 * The gutter is a sibling column rather than a marker on each line so the
 * numbers stay aligned no matter how wide the code is, and so a long line
 * scrolls horizontally without dragging its numbers along with it.
 */
export function CodeViewer({
  content,
  startLine = 1,
  className,
}: {
  content: string
  /** Line number of the first line in `content`. */
  startLine?: number
  className?: string
}) {
  const lines = content.split("\n")
  // A trailing newline makes the last split element an empty string, which
  // would render a phantom line number past the end of the file.
  if (lines.length > 1 && lines[lines.length - 1] === "") lines.pop()

  return (
    // Vertical scrolling belongs to the panel around this, so a multi-chunk
    // file reads as one continuous document instead of one scroll box per
    // chunk. Only the horizontal axis is handled here.
    <div className={cn("overflow-x-auto", className)}>
      <div className="flex min-w-full text-xs leading-relaxed">
        {/* Opaque, not bg-muted/40: the gutter is sticky, so code scrolls
            UNDER it. A translucent background let the code show through and
            the numbers read as floating on top of the text instead of sitting
            beside it. The tint is kept, the transparency is not. */}
        <div
          aria-hidden="true"
          className="sticky left-0 z-10 shrink-0 select-none border-r bg-muted px-2 py-3 text-right font-mono text-muted-foreground"
        >
          {lines.map((_, i) => (
            <div key={i}>{startLine + i}</div>
          ))}
        </div>
        <pre className="min-w-0 flex-1 px-3 py-3 font-mono">
          <code>{lines.join("\n")}</code>
        </pre>
      </div>
    </div>
  )
}
