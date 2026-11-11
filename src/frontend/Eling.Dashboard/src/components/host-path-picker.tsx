"use client"

import { useCallback, useEffect, useState } from "react"
import { Folder, FolderOpen, File, Loader2 } from "lucide-react"

import { Button } from "@/components/ui/button"

export interface HostPathPickerProps {
  open: boolean
  /** "file" lists files as selectable entries; "directory" lists directories only and confirms with "Use this folder". */
  mode: "file" | "directory"
  /** Header line; defaults to a mode-appropriate caption. */
  title?: string
  /** Called with the chosen path (a file path in "file" mode, the current folder in "directory" mode). */
  onSelect: (path: string) => void
  onClose: () => void
}

interface HostEntry {
  path: string
  isDirectory: boolean
}

/**
 * Shared browser for paths on the machine hosting the backend. Lists the drives,
 * walks directories, and calls back with the selected path. Replaces the
 * per-page pickers (SQLite file picker, shell directory picker) with one
 * implementation backed by /api/agent/host/drives and /api/agent/host/browse.
 */
export function HostPathPicker({ open, mode, title, onSelect, onClose }: HostPathPickerProps) {
  // null means "show the drives list"; anything else is the folder being browsed.
  const [path, setPath] = useState<string | null>(null)
  const [parent, setParent] = useState<string | null>(null)
  const [entries, setEntries] = useState<HostEntry[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const browse = useCallback(async (target: string | null) => {
    setLoading(true)
    setError(null)
    try {
      if (!target) {
        const res = await fetch("/api/agent/host/drives")
        if (!res.ok) throw new Error(`Drives list failed: HTTP ${res.status}`)
        const drives: string[] = await res.json()
        setPath(null)
        setParent(null)
        setEntries(drives.map((d) => ({ path: d, isDirectory: true })))
        return
      }
      const res = await fetch(`/api/agent/host/browse?path=${encodeURIComponent(target)}`)
      if (!res.ok) throw new Error(`Browse failed: HTTP ${res.status}`)
      const data = await res.json()
      setPath(data.path ?? target)
      setParent(data.parent ?? null)
      setEntries(data.entries ?? [])
    } catch (err) {
      setEntries([])
      setError(err instanceof Error ? err.message : "Failed to browse host.")
    } finally {
      setLoading(false)
    }
  }, [])

  // Open on the drives list; reset state when closed so re-opens start fresh.
  useEffect(() => {
    if (!open) return
    // Defer to a microtask so the fetch + setState happen outside the synchronous
    // effect body (react-hooks/set-state-in-effect).
    void Promise.resolve().then(() => browse(null))
  }, [open, browse])

  if (!open) return null

  const currentLabel = path ?? "Drives"
  const visibleEntries = mode === "directory" ? entries.filter((e) => e.isDirectory) : entries
  const heading = title ?? (mode === "directory" ? "Select a folder" : "Select a file")

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 px-4">
      <div className="w-full max-w-2xl rounded-lg border border-border bg-background p-4 shadow-lg">
        <div className="mb-3 flex items-center justify-between gap-3">
          <p className="flex min-w-0 items-center gap-2 font-medium">
            <FolderOpen className="size-4 shrink-0" />
            <span className="truncate">{heading}</span>
          </p>
          <Button variant="ghost" size="sm" onClick={onClose}>
            Close
          </Button>
        </div>
        <p className="mb-2 truncate text-xs text-muted-foreground">{currentLabel}</p>

        <div className="mb-2 flex gap-2">
          <Button variant="outline" size="sm" onClick={() => void browse(null)}>
            Drives
          </Button>
          {parent ? (
            <Button variant="outline" size="sm" onClick={() => void browse(parent)}>
              .. Up
            </Button>
          ) : null}
          {mode === "directory" ? (
            <div className="ml-auto">
              <Button size="sm" disabled={!path} onClick={() => path && onSelect(path)}>
                Use this folder
              </Button>
            </div>
          ) : null}
        </div>

        {error ? <p className="mb-2 text-xs text-destructive">{error}</p> : null}
        {loading ? (
          <p className="flex items-center gap-2 text-sm text-muted-foreground">
            <Loader2 className="size-4 animate-spin" /> Loading…
          </p>
        ) : (
          <div className="h-[420px] overflow-y-auto rounded border">
            {visibleEntries.map((entry) => (
              <button
                key={entry.path}
                type="button"
                className="flex w-full items-center gap-2 px-3 py-2 text-left text-sm hover:bg-accent"
                onClick={() => {
                  if (entry.isDirectory) {
                    void browse(entry.path)
                    return
                  }
                  onSelect(entry.path)
                }}
              >
                {entry.isDirectory ? <Folder className="size-4 shrink-0" /> : <File className="size-4 shrink-0" />}
                <span className="truncate">{entry.path.split(/[\\/]/).filter(Boolean).pop()}</span>
              </button>
            ))}
            {visibleEntries.length === 0 ? (
              <p className="px-3 py-2 text-sm text-muted-foreground">Empty.</p>
            ) : null}
          </div>
        )}
      </div>
    </div>
  )
}
