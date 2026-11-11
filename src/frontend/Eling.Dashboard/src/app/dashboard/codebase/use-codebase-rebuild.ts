"use client"

import { useCallback, useEffect, useState } from "react"
import { rebuildUrl } from "./scope"
import { useCodebaseRebuildSse, type RebuildProgress } from "@/hooks/use-codebase-rebuild-sse"
import { apiFetch } from "@/lib/api"
import type { WorkspaceSelection } from "@/components/workspaces/selection"

/** The two ways the index can be rebuilt: in place, or from scratch. */
export type RebuildMode = "incremental" | "full"

/** How the rebuild button names a mode. */
export function rebuildModeLabel(mode: RebuildMode): string {
  return mode === "full" ? "Full re-index" : "Incremental"
}

// The running job's id is persisted so a page refresh can re-attach to an
// in-flight rebuild instead of silently dropping the progress indicator.
const REBUILD_JOB_KEY = "codebase:rebuildJobId"

// A clean finished report is transient: it confirms the rebuild ran, then
// gets out of the way. Errors are not transient - a failing project's message
// must stay on screen until the reader dismisses it, so only a report with no
// failed projects is auto-hidden.
const CLEAN_REPORT_TTL_MS = 5000

// The SSE stream can drop or miss the terminal frame (proxy buffering, late
// subscriber), leaving the job stuck "running" forever. Polling guarantees the
// terminal state lands even when SSE does not.
const POLL_INTERVAL_MS = 2000

export interface UseCodebaseRebuildOptions {
  /** The scope the rebuild runs over. */
  selection: WorkspaceSelection
  /** Called when the job finishes, so the tiles are refetched. */
  onFinished: () => void
  /** Called when the rebuild fails outright. */
  onError: (message: string) => void
}

/**
 * The index rebuild: one job at a time, watched over SSE.
 *
 * The job is a backend process, so the page can lose it - a refresh drops the
 * POST response and the event stream, but the backend keeps running. The job
 * id in localStorage is what lets the page find it again.
 */
export function useCodebaseRebuild({ selection, onFinished, onError }: UseCodebaseRebuildOptions) {
  const [rebuildMode, setRebuildMode] = useState<RebuildMode>("incremental")
  const [rebuildProgress, setRebuildProgress] = useState<RebuildProgress | null>(null)
  const [rebuildStarting, setRebuildStarting] = useState(false)
  // The rebuild-mode dropdown is a base-ui Menu whose radio items do not close
  // the popup on selection (radio items are toggles, not actions). Without
  // this the popup stays open after picking a mode, which reads as a checkbox.
  // Controlling `open` and closing on `onValueChange` makes a selection act
  // like a normal menu item: pick, close, done.
  const [rebuildModeMenuOpen, setRebuildModeMenuOpen] = useState(false)

  const rebuilding = rebuildStarting || (rebuildProgress?.isRunning ?? false)

  /**
   * Starts a rebuild over the selected scope and mode. The 202 body is
   * already a progress snapshot, so the UI has something to render before the
   * first SSE frame lands.
   */
  const rebuild = useCallback(
    async (mode: RebuildMode) => {
      // Always take the mode explicitly so a stale closure can never silently
      // flip incremental into a full reindex.
      setRebuildMode(mode)
      setRebuildStarting(true)
      onError("")
      try {
        const snap = await apiFetch<RebuildProgress>(
          rebuildUrl(selection, mode === "full", Date.now()),
          { method: "POST", cache: "no-store" }
        )
        setRebuildProgress(snap)
        if (snap.jobId) {
          try { localStorage.setItem(REBUILD_JOB_KEY, snap.jobId) } catch { /* ignore */ }
        }
      } catch (e) {
        onError(e instanceof Error ? e.message : "Rebuild failed")
      } finally {
        setRebuildStarting(false)
      }
    },
    [selection, onError]
  )

  /**
   * A final snapshot also refetches the tiles, so the numbers stay correct
   * even if the SSE channel is down and the per-root notifications were lost.
   */
  const handleRebuildProgress = useCallback(
    (progress: RebuildProgress) => {
      setRebuildProgress(progress)
      if (progress.jobId) {
        try {
          if (progress.isRunning) {
            localStorage.setItem(REBUILD_JOB_KEY, progress.jobId)
          } else {
            localStorage.removeItem(REBUILD_JOB_KEY)
          }
        } catch {
          // ignore storage errors
        }
      }
      if (!progress.isRunning) onFinished()
    },
    [onFinished]
  )

  // Keep SSE subscription for real-time updates (primary channel).
  useCodebaseRebuildSse(handleRebuildProgress)

  // Poll the job status as a fallback for the SSE stream.
  useEffect(() => {
    if (!rebuildProgress?.isRunning || !rebuildProgress.jobId) return
    const jobId = rebuildProgress.jobId
    const interval = setInterval(async () => {
      try {
        const snap = await apiFetch<RebuildProgress>(`/api/codebase/rebuild-index/${jobId}`, { cache: "no-store" })
        setRebuildProgress(snap)
        if (!snap.isRunning) {
          clearInterval(interval)
          onFinished()
        }
      } catch {
        // ignore transient errors; next tick retries
      }
    }, POLL_INTERVAL_MS)
    return () => clearInterval(interval)
  }, [rebuildProgress?.isRunning, rebuildProgress?.jobId, onFinished])

  // Re-attach to an in-flight rebuild after a page refresh: the POST response
  // (and the SSE stream) are lost on reload, but the backend job keeps running
  // and its snapshot is still fetchable by jobId.
  useEffect(() => {
    let cancelled = false
    const restore = async () => {
      let jobId: string | null = null
      try { jobId = localStorage.getItem(REBUILD_JOB_KEY) } catch { return }
      if (!jobId) return
      try {
        const snap = await apiFetch<RebuildProgress>(`/api/codebase/rebuild-index/${jobId}`, { cache: "no-store" })
        if (cancelled) return
        setRebuildProgress(snap)
        // The pointer only exists to re-attach to an in-flight rebuild, so drop
        // it once the job is done. Auto-hiding a clean report is the shared
        // effect below; a timer here would duplicate that rule and outlive the
        // component.
        if (!snap.isRunning) {
          try { localStorage.removeItem(REBUILD_JOB_KEY) } catch { /* ignore */ }
        }
      } catch {
        // Job gone (backend restarted, or unknown id): drop the stale pointer.
        try { localStorage.removeItem(REBUILD_JOB_KEY) } catch { /* ignore */ }
      }
    }
    void restore()
    return () => { cancelled = true }
  }, [])

  // Only a report with no failed projects is auto-hidden.
  useEffect(() => {
    if (!rebuildProgress || rebuildProgress.isRunning) return
    if (rebuildProgress.results.some((r) => !r.ok)) return
    const timer = setTimeout(() => setRebuildProgress(null), CLEAN_REPORT_TTL_MS)
    return () => clearTimeout(timer)
  }, [rebuildProgress])

  return {
    rebuildMode,
    setRebuildMode,
    rebuildProgress,
    rebuildStarting,
    rebuildModeMenuOpen,
    setRebuildModeMenuOpen,
    rebuilding,
    rebuild,
  }
}
