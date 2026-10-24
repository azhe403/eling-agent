/* eslint-disable react-hooks/set-state-in-effect */
"use client"

import { useCallback, useEffect, useRef, useState } from "react"

export type LogTailStatus = "connected" | "connecting" | "error" | "idle"

const MAX_BUFFER = 10000

// Map the long level names used in the UI to the short tokens emitted by Serilog.
const LEVEL_TOKENS: Record<string, string[]> = {
  DEBUG: ["DEBUG", "DBG"],
  INFO: ["INFO", "INF"],
  WARN: ["WARN", "WRN"],
  ERROR: ["ERROR", "ERR"],
  FATAL: ["FATAL", "FTL", "CRT", "CRITICAL"],
}

function matchesLevel(line: string, level: string): boolean {
  if (!level || level === "all") return true
  const tokens = LEVEL_TOKENS[level.toUpperCase()] ?? [level.toUpperCase()]
  const upper = line.toUpperCase()
  return tokens.some((t) => upper.includes(`[${t}]`))
}

function matchesContains(line: string, contains: string): boolean {
  if (!contains) return true
  return line.toLowerCase().includes(contains.toLowerCase())
}

/**
 * Subscribe to /api/logs/tail SSE stream. Appends new lines to `lines`.
 */
export function useLogTail(params: {
  level: string
  contains: string
  enabled: boolean
  tail?: number
}): { status: LogTailStatus; lines: string[]; clear: () => void } {
  const [status, setStatus] = useState<LogTailStatus>("idle")
  const [lines, setLines] = useState<string[]>([])
  const esRef = useRef<EventSource | null>(null)

  const clear = useCallback(() => setLines([]), [])

  useEffect(() => {
    if (!params.enabled) {
      esRef.current?.close()
      esRef.current = null
      setStatus("idle")
      return
    }

    const qs = new URLSearchParams()
    if (params.level && params.level !== "all") qs.set("level", params.level)
    if (params.contains) qs.set("contains", params.contains)
    qs.set("tail", String(params.tail ?? 50))

    let es: EventSource | null = null
    try {
      es = new EventSource(`/api/logs/tail?${qs.toString()}`)
      esRef.current = es
      setStatus("connecting")

      es.onopen = () => setStatus("connected")

      es.onmessage = (event) => {
        if (!event.data) return
        try {
          const parsed = JSON.parse(event.data) as { line?: string }
          const line = parsed.line
          if (!line) return
          // Client-side filter (spec 6.4) aligned with backend tokens.
          if (!matchesLevel(line, params.level)) return
          if (!matchesContains(line, params.contains)) return
          setLines((prev) => {
            const next = [...prev, line]
            return next.length > MAX_BUFFER ? next.slice(next.length - MAX_BUFFER) : next
          })
        } catch (e) {
          console.error("[useLogTail] malformed SSE payload", e)
        }
      }

      es.onerror = (e) => {
        console.error("[useLogTail] EventSource error", e)
        if (es?.readyState === EventSource.CONNECTING) setStatus("connecting")
        else if (es?.readyState === EventSource.CLOSED) setStatus("error")
      }
    } catch (e) {
      console.error("[useLogTail] init failed", e)
      setStatus("error")
    }

    return () => {
      es?.close()
      esRef.current = null
      // Clear on reconnect/param change to avoid duplicate backlog.
      setLines([])
    }
  }, [params.enabled, params.level, params.contains, params.tail])

  return { status, lines, clear }
}
