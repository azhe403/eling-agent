"use client"

import { workspaceName } from "@/components/workspaces/runtime-rows"
import { utcToLocal } from "@/lib/date-utils"
import type { SelectedFile } from "./fileDetailApi"

/** One file in a result list: the path, the workspace it came from, and what the index holds for it. */
export type FileResult = SelectedFile & {
  chunkCount: number
  lastIndexedAt: string
}

/** The path of a file, rendered as the button that opens the viewer. */
export function FilePathLink({
  file,
  onOpen,
}: {
  file: SelectedFile
  onOpen: (file: SelectedFile) => void
}) {
  return (
    <button
      type="button"
      onClick={() => onOpen(file)}
      title={`Open ${file.path}`}
      className="text-left font-medium break-all underline-offset-4 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring rounded-xs"
    >
      {file.path}
    </button>
  )
}

/** Which workspace a row came from — only worth showing when the scope covers more than one. */
export function WorkspaceBadge({ root }: { root: string }) {
  return (
    <span
      className="rounded-full border border-border/50 bg-muted/50 px-2 py-0.5 text-[11px] font-medium text-muted-foreground"
      title={root}
    >
      📁 {workspaceName(root)}
    </span>
  )
}

/**
 * One indexed file per row: its path, the workspace it came from, and how
 * much of it the index holds.
 *
 * The empty-query listing and the file scopes answer with the same rows, so
 * the markup lives here once — the page only decides which list it shows.
 */
export function FileResultList({
  files,
  showWorkspace,
  onOpen,
}: {
  files: readonly FileResult[]
  showWorkspace: boolean
  onOpen: (file: SelectedFile) => void
}) {
  return (
    <>
      {files.map((f) => (
        <div key={`${f.projectRoot}-${f.path}`} className="p-3 text-sm">
          <div className="flex flex-wrap items-center gap-2">
            <FilePathLink file={f} onOpen={onOpen} />
            {showWorkspace && <WorkspaceBadge root={f.projectRoot} />}
          </div>
          <div className="text-muted-foreground text-xs">
            {f.chunkCount} {f.chunkCount === 1 ? "chunk" : "chunks"} · indexed{" "}
            {utcToLocal(f.lastIndexedAt)}
          </div>
        </div>
      ))}
    </>
  )
}
