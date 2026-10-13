"use client"

import { useEffect } from "react"
import { FileDetailContents } from "./FileDetailDialog"
import type { SelectedFile } from "./fileDetailApi"

/**
 * The wide-screen viewer: file detail docked as a right-hand column of the
 * page, not a modal. No backdrop on purpose — the result list stays clickable
 * and the panel swaps its file in place, which is the point of using the
 * empty right side instead of covering the list.
 *
 * Rendered only at xl and up (the JS gate and the `xl:flex` class agree), so
 * below that width the centered dialog remains the only viewer.
 */
export function FileDetailPanel({
  file,
  onClose,
}: {
  file: SelectedFile
  onClose: () => void
}) {
  // Escape parity with the dialog this replaces at this width: the centered
  // modal closes on Escape through the dialog primitive, and a docked panel
  // that ignored the key would feel broken after a resize.
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose()
    }
    window.addEventListener("keydown", onKeyDown)
    return () => window.removeEventListener("keydown", onKeyDown)
  }, [onClose])

  return (
    <aside
      aria-label="File viewer"
      className="sticky top-16 hidden min-h-0 max-h-[calc(100dvh-4rem)] min-w-0 flex-1 flex-col border-l bg-background xl:flex"
    >
      <div className="flex min-h-0 min-w-0 flex-1 flex-col gap-3 overflow-hidden p-4">
        {/* Keyed on the file for the same reason the dialog keys it: opening
            another file remounts with fresh state instead of showing the
            previous file's content for a frame. */}
        <FileDetailContents
          key={`${file.projectRoot}::${file.path}`}
          file={file}
          onClose={onClose}
        />
      </div>
    </aside>
  )
}
