"use client"

import { useCallback, useEffect, useState } from "react"
import { ChevronDown, ChevronUp, FileCode2, Layers } from "lucide-react"
import { Button } from "@/components/ui/button"
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import { Skeleton } from "@/components/ui/skeleton"
import { cn } from "@/lib/utils"
import { CodeViewer } from "./CodeViewer"
import {
  fileId,
  readChunkIndex,
  readTab,
  rememberChunk,
  rememberTab,
} from "./fileDialogPrefs"
import {
  describeStatus,
  fetchFileContent,
  fetchFileDetail,
  formatBytes,
  type SelectedFile,
} from "./fileDetailApi"

type Tab = "chunks" | "file"

type State<T> =
  | { status: "loading" }
  | { status: "error"; message: string }
  | { status: "ready"; value: T }

/**
 * File viewer for the codebase page: the indexed chunks of one file, and the
 * same file read whole from disk.
 *
 * The two views are not interchangeable, and the dialog says so. Chunks
 * overlap by 30 lines by design, so stitching them back would duplicate
 * content — they are retrieval spans, not a reconstruction. The full-file view
 * is the honest document, and it is fetched only when that tab is opened, so
 * opening the dialog on a large file costs one indexed lookup rather than a
 * multi-megabyte read.
 */
export function FileDetailDialog({
  file,
  onClose,
}: {
  file: SelectedFile | null
  onClose: () => void
}) {
  return (
    <Dialog open={file !== null} onOpenChange={(open) => !open && onClose()}>
      {file && (
        // Keyed on the file, so opening a different one remounts the body with
        // fresh state. Resetting the tab and the cached content from an effect
        // instead would be a setState in the effect body, and would also let
        // the previous file's content stay on screen for a frame.
        <FileDetailBody
          key={`${file.projectRoot}::${file.path}`}
          file={file}
          onClose={onClose}
        />
      )}
    </Dialog>
  )
}

function FileDetailBody({
  file,
  onClose,
}: {
  file: SelectedFile
  onClose: () => void
}) {
  // Seeded from storage rather than reset, so reopening a file lands on the
  // view it was left on. The initializer runs once per mount and the body is
  // keyed on the file, so this is a fresh read for each file opened.
  const [tab, setTabState] = useState<Tab>(readTab)
  const [detail, setDetail] = useState<State<DetailView>>({ status: "loading" })
  const [content, setContent] = useState<State<ContentView>>({ status: "loading" })

  const setTab = useCallback((next: Tab) => {
    setTabState(next)
    rememberTab(next)
  }, [])

  // Chunks are fetched when a file is opened, not per tab: the dialog opens on
  // the chunk view every time, so deferring it would only add a skeleton.
  useEffect(() => {
    const controller = new AbortController()
    fetchFileDetail(file, controller.signal)
      .then((value) => setDetail({ status: "ready", value }))
      .catch((e: unknown) => {
        if (controller.signal.aborted) return
        setDetail({ status: "error", message: messageOf(e) })
      })
    return () => controller.abort()
  }, [file])

  // Lazily loaded: switching to the full-file tab is what costs a disk read.
  // The guard is the status rather than a "requested" flag, so a settled
  // response is never refetched when the user toggles back and forth.
  const contentStatus = content.status
  useEffect(() => {
    if (tab !== "file" || contentStatus !== "loading") return
    const controller = new AbortController()
    fetchFileContent(file, controller.signal)
      .then((value) => setContent({ status: "ready", value }))
      .catch((e: unknown) => {
        if (controller.signal.aborted) return
        setContent({ status: "error", message: messageOf(e) })
      })
    return () => controller.abort()
  }, [tab, contentStatus, file])

  return (
    <DialogContent
      showCloseButton={false}
      // No `flex` here: the base class is `grid`, and this element's layout
      // is a header row plus a body row that takes the leftover height.
      // Overriding the display would silently void the grid-rows template.
      //
      // `minmax(0,1fr)` on the column is load-bearing, not decoration: a grid
      // item defaults to min-width auto, so a long file path would widen the
      // column past the dialog and overflow-hidden would clip the controls off
      // the right edge. A zero minimum is what lets the column stay bounded.
      //
      // Every width here carries its own variant. The base class in ui/dialog.tsx
      // sets `sm:max-w-lg`, and a bare `max-w-*` does not override it — tailwind-merge
      // only drops a class that shares the variant too, so dropping `sm:` silently
      // hands the dialog back to 32rem for the whole sm..lg range.
      className="h-[85svh] max-w-4xl grid-cols-[minmax(0,1fr)] grid-rows-[auto_minmax(0,1fr)] gap-3 overflow-hidden p-4 sm:max-w-4xl lg:max-w-7xl 2xl:max-w-[100rem]"
    >
      {/* Title above, controls below on a phone; side by side from `sm` up.
          Below that width a long path would leave the title ~180px of a
          390px screen and wrap every few characters, so stacking gives the
          name the full width and the buttons a row of their own. */}
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between sm:gap-4">
        {/* flex-1, not just min-w-0: a flex item's default basis is its
            content width, so a long path would push the controls off the
            right edge rather than wrap inside its own column. */}
        <DialogHeader className="min-w-0 gap-1 sm:flex-1">
          <DialogTitle className="flex items-center gap-2 break-all text-sm">
            <FileCode2 className="size-4 shrink-0 text-muted-foreground" />
            {file.path}
          </DialogTitle>
          <DialogDescription className="break-all text-xs">
            {file.projectRoot}
            {detail.status === "ready" && (
              <>
                {" · "}
                {detail.value.chunks.length}{" "}
                {detail.value.chunks.length === 1 ? "chunk" : "chunks"} ·{" "}
                {formatBytes(detail.value.size)}
              </>
            )}
          </DialogDescription>
        </DialogHeader>

        <div className="flex shrink-0 items-center gap-1">
          <TabButton
            active={tab === "chunks"}
            onClick={() => setTab("chunks")}
            icon={<Layers className="size-4" />}
            label="Chunks"
          />
          <TabButton
            active={tab === "file"}
            onClick={() => setTab("file")}
            icon={<FileCode2 className="size-4" />}
            label="Full file"
          />
          <Button variant="outline" size="sm" onClick={onClose}>
            Close
          </Button>
        </div>
      </div>

      {/* The body is the row that can shrink, so min-h-0 plus an explicit
          scroll container is what keeps a long file inside the dialog
          instead of stretching it past the fixed height. */}
      <div className="flex min-h-0 gap-2">
        {tab === "chunks" ? (
          <ChunksPanel state={detail} file={file} />
        ) : (
          <FilePanel state={content} />
        )}
      </div>
    </DialogContent>
  )
}

type DetailView = Awaited<ReturnType<typeof fetchFileDetail>>
type ContentView = Awaited<ReturnType<typeof fetchFileContent>>

function TabButton({
  active,
  onClick,
  icon,
  label,
}: {
  active: boolean
  onClick: () => void
  icon: React.ReactNode
  label: string
}) {
  return (
    <Button
      variant={active ? "secondary" : "ghost"}
      size="sm"
      onClick={onClick}
      aria-pressed={active}
    >
      {icon}
      {label}
    </Button>
  )
}

/**
 * The chunk view: a rail of chunks on the left, one chunk's code on the right.
 *
 * One chunk at a time rather than all of them stacked. Chunks overlap by 30
 * lines, so a stacked list repeats content at every boundary and the line
 * numbers visibly jump backwards — which reads as a bug in the indexer rather
 * than what it actually is. The rail is also the honest navigation for a long
 * file: scrolling 2500 lines to find the third chunk is worse than picking it.
 */
function ChunksPanel({
  state,
  file,
}: {
  state: State<DetailView>
  file: SelectedFile
}) {
  // Read once per mount. The body above is keyed on the file, so switching
  // files remounts this and re-reads the right entry.
  const id = fileId(file.projectRoot, file.path)
  const [active, setActiveState] = useState(() => readChunkIndex(id) ?? 0)

  const setActive = useCallback(
    (next: number) => {
      setActiveState(next)
      rememberChunk(id, next)
    },
    [id]
  )

  if (state.status === "loading") return <Loading />
  if (state.status === "error") return <Notice tone="error">{state.message}</Notice>

  const chunks = state.value.chunks
  if (chunks.length === 0) {
    return (
      <Notice>
        The index has this file but no chunks for it — it was indexed as
        unreadable or empty.
      </Notice>
    )
  }

  // Guard against a short response leaving the rail pointing past the end.
  const index = Math.min(active, chunks.length - 1)
  const chunk = chunks[index]

  const navigable = chunks.length > 1

  return (
    <>
      <ChunkRail
        chunks={chunks}
        active={index}
        onSelect={setActive}
        hidden={!navigable}
      />
      <div className="min-w-0 flex-1 overflow-y-auto rounded-lg border">
        <div className="flex items-center gap-2 border-b bg-muted/40 px-3 py-1 text-[11px] text-muted-foreground">
          <span className="min-w-0 flex-1 truncate">
            chunk {index + 1} of {chunks.length} · lines {chunk.startLine}–
            {chunk.endLine}
          </span>
          {/* The rail is hidden on narrow screens (see ChunkRail), so these
              arrows are the only way to move between chunks there. Cheap
              enough to keep at every width, and quicker than the rail. */}
          {navigable && (
            <span className="flex shrink-0 items-center gap-1">
              {/* Outline, not ghost: with the rail hidden on a phone these are
                  the only chunk navigation there is, and a ghost button has no
                  background and no border — it reads as a smudge rather than
                  something to press. A 24px target is also below the
                  comfortable tap size, hence size="sm". */}
              <Button
                variant="outline"
                size="icon-sm"
                disabled={index === 0}
                onClick={() => setActive(index - 1)}
                aria-label="Previous chunk"
              >
                <ChevronUp className="size-4" />
              </Button>
              <Button
                variant="outline"
                size="icon-sm"
                disabled={index === chunks.length - 1}
                onClick={() => setActive(index + 1)}
                aria-label="Next chunk"
              >
                <ChevronDown className="size-4" />
              </Button>
            </span>
          )}
        </div>
        <CodeViewer content={chunk.content} startLine={chunk.startLine} />
      </div>
    </>
  )
}

/** The chunk list. A rail, not tabs: it is a scroll position, and it has to
 * stay put while the code on the right scrolls.
 *
 * Hidden below `sm` because a fixed 10rem column would take a third of a phone
 * screen and leave the code unreadable; the prev/next arrows in the panel
 * header cover navigation at that width. */
function ChunkRail({
  chunks,
  active,
  onSelect,
  hidden,
}: {
  chunks: DetailView["chunks"]
  active: number
  onSelect: (index: number) => void
  hidden: boolean
}) {
  if (hidden) return null

  return (
    <nav
      aria-label="Chunks in this file"
      className="hidden w-40 shrink-0 space-y-1 overflow-y-auto rounded-lg border p-2 sm:block"
    >
      {chunks.map((chunk, i) => (
        <button
          key={`${chunk.startLine}-${i}`}
          type="button"
          onClick={() => onSelect(i)}
          aria-current={i === active}
          className={cn(
            "w-full rounded-md px-2 py-1.5 text-left text-xs transition-colors",
            "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
            i === active
              ? "bg-secondary font-medium text-secondary-foreground"
              : "text-muted-foreground hover:bg-accent hover:text-accent-foreground"
          )}
        >
          <span className="block font-mono">
            {i + 1}. {chunk.startLine}–{chunk.endLine}
          </span>
        </button>
      ))}
    </nav>
  )
}

function FilePanel({ state }: { state: State<ContentView> }) {
  if (state.status === "loading") return <Loading />
  if (state.status === "error") return <Notice tone="error">{state.message}</Notice>

  const value = state.value
  if (value.status !== "ok") {
    return <Notice>{describeStatus(value.status, value.size)}</Notice>
  }

  return (
    <div className="min-w-0 flex-1 overflow-y-auto rounded-lg border">
      <CodeViewer content={value.content ?? ""} />
    </div>
  )
}

function Loading() {
  return (
    <div className="flex flex-col gap-2 p-3">
      {Array.from({ length: 6 }).map((_, i) => (
        <Skeleton key={i} className="h-3 w-full" />
      ))}
    </div>
  )
}

function Notice({
  tone = "info",
  children,
}: {
  tone?: "info" | "error"
  children: React.ReactNode
}) {
  return (
    <p
      className={
        tone === "error"
          ? "p-4 text-sm text-destructive"
          : "p-4 text-sm text-muted-foreground"
      }
    >
      {children}
    </p>
  )
}

function messageOf(e: unknown) {
  return e instanceof Error ? e.message : "Request failed"
}
