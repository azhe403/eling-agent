"use client"

import { useEffect, useRef, useState } from "react"

/** One project's index pass. A failing project is reported, not thrown. */
export type RebuildProjectResult = {
  projectRoot: string
  ok: boolean
  files: number
  chunks: number
  skipped: number
  deleted: number
  error: string | null
}

/** Immutable job snapshot, camelCased by the backend serializer. */
export type RebuildProgress = {
  jobId: string
  scope: string
  full: boolean
  isRunning: boolean
  done: number
  total: number
  skippedRoots: string[]
  currentProject: string | null
  results: RebuildProjectResult[]
  error: string | null
}

export type RebuildSseStatus = "connected" | "connecting" | "error"

/**
 * Subscribe to the backend's rebuild-progress SSE stream
 * (`/api/events/codebase-rebuild`). Native `EventSource` handles
 * reconnection, so we only surface `connecting` while the browser retries and
 * `error` once the stream is closed.
 *
 * A subscription created after a job started misses its earlier snapshots —
 * the `GET /api/codebase/rebuild-index/{jobId}` status endpoint stays the
 * source of truth, which is why the page also seeds state from the POST
 * response.
 */
export function useCodebaseRebuildSse(
  onProgress: (progress: RebuildProgress) => void
): { status: RebuildSseStatus } {
  const [status, setStatus] = useState<RebuildSseStatus>("connecting")

  // Stash the callback in a ref so the long-lived EventSource handler can call
  // it without forcing the effect to re-subscribe on every render.
  const onProgressRef = useRef(onProgress)

  useEffect(() => {
    onProgressRef.current = onProgress
  }, [onProgress])

  useEffect(() => {
    if (typeof window === "undefined") return
    let es: EventSource | null = null
    try {
      es = new EventSource("/api/events/codebase-rebuild")

      es.onopen = () => {
        setStatus("connected")
      }

      es.onmessage = (event) => {
        if (!event.data || event.data === "connected") return
        try {
          onProgressRef.current(JSON.parse(event.data) as RebuildProgress)
        } catch (e) {
          // A malformed frame must never tear down a live stream — but it is
          // dropped silently otherwise, and a backend emitting bad JSON looks
          // identical to one that never progresses.
          console.warn("[REBUILD SSE] malformed progress frame", event.data, e)
        }
      }

      es.onerror = () => {
        if (es?.readyState === EventSource.CONNECTING) {
          setStatus("connecting")
        } else if (es?.readyState === EventSource.CLOSED) {
          setStatus("error")
        }
      }
    } catch (e) {
      console.error("[REBUILD SSE INIT ERROR]", e)
    }

    return () => {
      es?.close()
    }
  }, [])

  return { status }
}
