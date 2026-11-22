"use client"

import { useEffect, useMemo, useRef, useState } from "react"

export type DocsSseStatus = "connected" | "connecting" | "error"

/**
 * Subscribe to the backend's docs change SSE stream (`/api/events/docs`).
 * Native `EventSource` handles reconnection — we surface `connecting` when the
 * browser is retrying and `error` when the stream is closed.
 */
export function useDocsSse(
  onMutation: () => void,
  topics: string[] = ["docs"]
): { status: DocsSseStatus } {
  const [status, setStatus] = useState<DocsSseStatus>("connecting")
  const wanted = useMemo(() => new Set<string>(topics), [topics])

  const onMutationRef = useRef(onMutation)
  const wantedRef = useRef(wanted)

  useEffect(() => {
    wantedRef.current = wanted
  }, [wanted])

  useEffect(() => {
    onMutationRef.current = onMutation
  }, [onMutation])

  useEffect(() => {
    if (typeof window === "undefined") return
    let es: EventSource | null = null
    try {
      console.log(
        "%c[DOCS SSE INIT] 🔄 Connecting to /api/events/docs...",
        "color: #d97706; font-weight: bold;"
      )
      es = new EventSource("/api/events/docs")

      es.onopen = () => {
        setStatus("connected")
        console.log(
          "%c[DOCS SSE CONNECTED] 🟢 Live event stream connected to /api/events/docs",
          "color: #16a34a; font-weight: bold; background: #dcfce7; padding: 2px 6px; border-radius: 4px;"
        )
      }

      es.onmessage = (event) => {
        console.log(
          `%c[DOCS SSE EVENT RECEIVED] ⚡ Data payload: "${event.data}" at ${new Date().toLocaleTimeString()}`,
          "color: #2563eb; font-weight: bold; background: #dbeafe; padding: 2px 6px; border-radius: 4px;"
        )

        if (!event.data || event.data === "connected") return

        // Not our topic: another page's business.
        if (!wantedRef.current.has(event.data)) {
          console.log(`[DOCS SSE IGNORED] 🚫 Topic "${event.data}" is not watched by this page`)
          return
        }

        console.log(
          "%c[DOCS AUTO REFRESH] 🚀 Triggering load('sse_mutation')...",
          "color: #9333ea; font-weight: bold;"
        )
        onMutationRef.current()
      }

      es.onerror = () => {
        if (es?.readyState === EventSource.CONNECTING) {
          setStatus("connecting")
          console.log(
            "%c[DOCS SSE RECONNECTING] 🟡 Connection lost, browser attempting auto-reconnect...",
            "color: #d97706; font-weight: bold;"
          )
        } else if (es?.readyState === EventSource.CLOSED) {
          setStatus("error")
          console.log(
            "%c[DOCS SSE CLOSED] ⚪ EventSource connection closed",
            "color: #6b7280; font-weight: bold;"
          )
        }
      }
    } catch (e) {
      console.error("[DOCS SSE INIT ERROR]", e)
    }

    return () => {
      console.log("[DOCS SSE CLEANUP] Closing EventSource connection")
      es?.close()
    }
  }, [])

  return { status }
}