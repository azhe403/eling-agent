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
  topics: string[] = ["docs"],
  workspaceRoots: readonly string[] = []
): { status: DocsSseStatus } {
  const [status, setStatus] = useState<DocsSseStatus>("connecting")
  const wanted = useMemo(() => new Set<string>(topics), [topics])

  const onMutationRef = useRef(onMutation)
  const wantedRef = useRef(wanted)
  const scopesRef = useRef(workspaceRoots)

  useEffect(() => {
    wantedRef.current = wanted
  }, [wanted])

  useEffect(() => {
    scopesRef.current = workspaceRoots
  }, [workspaceRoots])

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

        // Scoped changes arrive as one compact JSON frame: the topic stays
        // shared, while workspace_id/workspace identify which workspace moved.
        // A plain topic string is still accepted for older frames.
        let topic = event.data
        let changedRoot: string | null = null
        if (event.data.startsWith("{")) {
          try {
            const payload = JSON.parse(event.data) as { topic?: unknown; workspace?: unknown }
            if (typeof payload.topic === "string") topic = payload.topic
            if (typeof payload.workspace === "string") changedRoot = payload.workspace
          } catch {
            // Malformed frame: fall through and treat the raw text as a topic.
          }
        }

        // Not our topic: another page's business.
        if (!wantedRef.current.has(topic)) {
          console.log(`[DOCS SSE IGNORED] 🚫 Topic "${topic}" is not watched by this page`)
          return
        }
        // All-workspaces views pass no roots; a scoped view ignores other roots.
        const scopes = scopesRef.current
        if (changedRoot !== null && scopes.length > 0 && !scopes.includes(changedRoot)) {
          console.log(`[DOCS SSE IGNORED] 🚫 Change in "${changedRoot}" is outside the selected workspaces`)
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