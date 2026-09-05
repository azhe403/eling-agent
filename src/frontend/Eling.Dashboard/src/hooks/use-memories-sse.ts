"use client"

import { useEffect, useRef, useState } from "react"

export type SseStatus = "connected" | "connecting" | "error"

/**
 * Subscribe to the backend's memory and coordinator change SSE stream (`/api/events/memories`).
 * Native `EventSource` handles reconnection — we surface `connecting` when the
 * browser is retrying and `error` when the stream is closed. Diagnostic
 * `console.log` calls mirror the originals so dev tools still show the
 * familiar 🔄🟢⚡🟡⚪ stream colors during debugging.
 */
export function useMemoriesSse(
  onMutation: () => void,
  onRuntimesChange?: () => void
): { status: SseStatus } {
  const [status, setStatus] = useState<SseStatus>("connecting")

  // Stash callbacks in refs so the long-lived EventSource handler
  // can call them without forcing the effect to re-subscribe on every render.
  const onMutationRef = useRef(onMutation)
  const onRuntimesChangeRef = useRef(onRuntimesChange)

  useEffect(() => {
    onMutationRef.current = onMutation
  }, [onMutation])

  useEffect(() => {
    onRuntimesChangeRef.current = onRuntimesChange
  }, [onRuntimesChange])

  useEffect(() => {
    if (typeof window === "undefined") return
    let es: EventSource | null = null
    try {
      console.log(
        "%c[SSE INIT] 🔄 Connecting to /api/events/memories...",
        "color: #d97706; font-weight: bold;"
      )
      es = new EventSource("/api/events/memories")

      es.onopen = () => {
        setStatus("connected")
        console.log(
          "%c[SSE CONNECTED] 🟢 Live event stream connected to /api/events/memories",
          "color: #16a34a; font-weight: bold; background: #dcfce7; padding: 2px 6px; border-radius: 4px;"
        )
      }

      es.onmessage = (event) => {
        console.log(
          `%c[SSE EVENT RECEIVED] ⚡ Data payload: "${event.data}" at ${new Date().toLocaleTimeString()}`,
          "color: #2563eb; font-weight: bold; background: #dbeafe; padding: 2px 6px; border-radius: 4px;"
        )

        if (!event.data || event.data === "connected") return

        if (event.data === "runtimes") {
          console.log(
            "%c[AUTO REFRESH] 🌐 Triggering onRuntimesChange('sse_runtimes')...",
            "color: #0284c7; font-weight: bold;"
          )
          if (onRuntimesChangeRef.current) {
            onRuntimesChangeRef.current()
          } else {
            onMutationRef.current()
          }
        } else {
          // Refresh memories whenever a mutation event is received.
          console.log(
            "%c[AUTO REFRESH] 🚀 Triggering load('sse_mutation')...",
            "color: #9333ea; font-weight: bold;"
          )
          onMutationRef.current()
        }
      }

      es.onerror = () => {
        if (es?.readyState === EventSource.CONNECTING) {
          setStatus("connecting")
          console.log(
            "%c[SSE RECONNECTING] 🟡 Connection lost, browser attempting auto-reconnect...",
            "color: #d97706; font-weight: bold;"
          )
        } else if (es?.readyState === EventSource.CLOSED) {
          setStatus("error")
          console.log(
            "%c[SSE CLOSED] ⚪ EventSource connection closed",
            "color: #6b7280; font-weight: bold;"
          )
        }
      }
    } catch (e) {
      console.error("[SSE INIT ERROR]", e)
    }

    return () => {
      console.log("[SSE CLEANUP] Closing EventSource connection")
      es?.close()
    }
  }, [])

  return { status }
}