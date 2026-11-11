"use client"

import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react"
import { RefreshCw } from "lucide-react"

import { DashboardHeader } from "@/components/dashboard-header"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"
import { useLogTail } from "@/hooks/use-log-tail"
import { apiFetch, ApiError } from "@/lib/api"

interface LogFile {
  name: string
  sizeBytes: number
  lastWriteTimeUtc: string
}

interface ReadResponse {
  file: string
  totalLines: number
  filteredTotal: number
  returned: number
  lines: string[]
}

const LEVELS = ["all", "DEBUG", "INFO", "WARN", "ERROR", "FATAL"] as const

type LogFormat = "text" | "jsonl"

function levelOf(line: string): string {
  // JSONL lines are objects. Serilog's core JsonFormatter (verified on 4.4.0)
  // writes plain "Level" fields with the full name, e.g. "Level":"Warning" —
  // not the OTel-compact "@lt":"WRN" form. The bracket-token regex below only
  // understands the text template, so JSONL gets its own branch.
  if (line.trimStart().startsWith("{")) {
    const jm = line.match(/"Level":"(Verbose|Debug|Information|Warning|Error|Fatal)"/i)
    if (!jm) return "info"
    const t = jm[1].toLowerCase()
    if (t === "error" || t === "fatal") return "error"
    if (t === "warning") return "warn"
    if (t === "debug" || t === "verbose") return "debug"
    return "info"
  }
  const m = line.match(/\[(DBG|INF|WRN|ERR|FTL|DEBUG|INFO|WARN|ERROR|FATAL)\]/i)
  if (!m) return "info"
  const t = m[1].toUpperCase()
  if (t.startsWith("ERR") || t === "FTL" || t === "FATAL") return "error"
  if (t === "WRN" || t === "WARN") return "warn"
  if (t === "DBG" || t === "DEBUG") return "debug"
  return "info"
}

const levelColor: Record<string, string> = {
  error: "text-destructive",
  warn: "text-amber-500 dark:text-amber-400",
  debug: "text-muted-foreground",
  info: "text-foreground",
}

export function Logs() {
  const [stream, setStream] = useState("backend")
  const [date, setDate] = useState("") // empty = active
  const [level, setLevel] = useState<string>("all")
  const [contains, setContains] = useState("")
  const [debouncedContains, setDebouncedContains] = useState("")
  const [tailCount, setTailCount] = useState(500)
  const [files, setFiles] = useState<LogFile[]>([])
  const [history, setHistory] = useState<ReadResponse | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [live, setLive] = useState(false)
  const autoLiveRef = useRef(false)
  const [newLinesCount, setNewLinesCount] = useState(0)
  const [wrap, setWrap] = useState(false)
  const [format, setFormat] = useState<LogFormat>("text")

  const activeFileName = `${stream}.${format === "jsonl" ? "jsonl" : "log"}`

  const containerRef = useRef<HTMLDivElement>(null)
  const atBottomRef = useRef(true)
  const prevLiveLinesRef = useRef(0)

  const liveTail = useLogTail({ level, contains: debouncedContains, enabled: live, tail: tailCount })

  useEffect(() => {
    const t = setTimeout(() => setDebouncedContains(contains), 300)
    return () => clearTimeout(t)
  }, [contains])

  const fetchFiles = useCallback(async () => {
    try {
      const data = await apiFetch<{ files?: LogFile[] } | undefined>(`/api/logs?stream=${stream}`)
      setFiles(data?.files ?? [])
    } catch (e) {
      setError(
        e instanceof ApiError
          ? `Failed to list logs (${e.status})`
          : e instanceof Error
            ? e.message
            : String(e)
      )
    }
  }, [stream])

  // The format is edited on the Settings page; this page only reads it, to know
  // which active file name to leave out of the date picker.
  const fetchFormat = useCallback(async () => {
    try {
      const data = await apiFetch<{ format?: string } | undefined>("/api/logs/config")
      setFormat(data?.format === "jsonl" ? "jsonl" : "text")
    } catch (e) {
      // A non-2xx answer used to be ignored silently; only unexpected
      // failures surface.
      if (!(e instanceof ApiError)) {
        setError(e instanceof Error ? e.message : String(e))
      }
    }
  }, [])

  const fetchHistory = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const qs = new URLSearchParams()
      qs.set("stream", stream)
      if (date) qs.set("date", date)
      if (level !== "all") qs.set("level", level)
      if (debouncedContains) qs.set("contains", debouncedContains)
      qs.set("tail", String(tailCount))
      const data = await apiFetch<ReadResponse | undefined>(`/api/logs/read?${qs.toString()}`)
      setHistory(data ?? null)
      if (!autoLiveRef.current) {
        autoLiveRef.current = true
        setLive(true)
      }
    } catch (e) {
      if (e instanceof ApiError) {
        setError(`Failed to read logs (${e.status})`)
      } else {
        setError(e instanceof Error ? e.message : String(e))
      }
    } finally {
      setLoading(false)
    }
  }, [stream, date, level, debouncedContains, tailCount])

  useEffect(() => {
    const t = setTimeout(() => {
      void fetchFiles()
    }, 0)
    return () => clearTimeout(t)
  }, [fetchFiles])

  useEffect(() => {
    const t = setTimeout(() => {
      void fetchFormat()
    }, 0)
    return () => clearTimeout(t)
  }, [fetchFormat])

  useEffect(() => {
    const t = setTimeout(() => {
      if (!live) void fetchHistory()
      else {
        atBottomRef.current = true
        setNewLinesCount(0)
      }
    }, 0)
    return () => clearTimeout(t)
  }, [live, fetchHistory])

  useEffect(() => {
    if (live) {
      const diff = liveTail.lines.length - prevLiveLinesRef.current
      if (diff > 0 && !atBottomRef.current) {
        setNewLinesCount((c) => c + diff)
      }
      prevLiveLinesRef.current = liveTail.lines.length
    } else {
      prevLiveLinesRef.current = 0
      setNewLinesCount(0)
    }
  }, [live, liveTail.lines.length])

  const handleScroll = useCallback(() => {
    const el = containerRef.current
    if (!el) return
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 4
    atBottomRef.current = atBottom
    if (atBottom) setNewLinesCount(0)
  }, [])

  useLayoutEffect(() => {
    const el = containerRef.current
    if (el && atBottomRef.current) el.scrollTop = el.scrollHeight
  })

  const scrollToBottom = useCallback(() => {
    const el = containerRef.current
    if (el) {
      el.scrollTop = el.scrollHeight
      atBottomRef.current = true
      setNewLinesCount(0)
    }
  }, [])

  const lines = useMemo<string[]>(() => {
    const base = history?.lines ?? []
    if (!live) return base
    // Live mode keeps the loaded history visible and appends only the lines
    // that are not already in it, so production/global entries don't vanish.
    const seen = new Set(base)
    const extra = liveTail.lines.filter((l) => !seen.has(l))
    return extra.length > 0 ? [...base, ...extra] : base
  }, [live, history, liveTail.lines])

  const contentColumn = "flex min-w-0 w-full flex-1 flex-col gap-4 p-4"

  return (
    <div className="flex w-full flex-col">
      <DashboardHeader label="Logs">
        <span
          className={
            live && liveTail.status === "connected"
              ? "rounded-full bg-emerald-500/15 px-2 py-0.5 text-xs font-medium text-emerald-600 dark:text-emerald-400"
              : "rounded-full bg-muted px-2 py-0.5 text-xs font-medium text-muted-foreground"
          }
          title={live ? liveTail.status : "history"}
        >
          {live ? (liveTail.status === "connected" ? "● Live" : "○ " + liveTail.status) : "History"}
        </span>
        <Button
          variant="outline"
          size="sm"
          onClick={() => {
            atBottomRef.current = true
            setLoading(true)
            void fetchHistory()
          }}
          disabled={loading || live}
        >
          <RefreshCw className={loading ? "size-4 animate-spin" : "size-4"} />
          <span className="hidden sm:inline-block">Refresh</span>
        </Button>
        <Button
          size="sm"
          variant="outline"
          className={live ? "border-emerald-500/50 bg-emerald-500/15 text-emerald-600 dark:text-emerald-400" : ""}
          onClick={() => setLive((v) => !v)}
        >
          {live ? "Stop" : "Live"}
        </Button>
        <Button
          size="sm"
          variant={wrap ? "default" : "outline"}
          onClick={() => setWrap((v) => !v)}
        >
          Wrap
        </Button>
      </DashboardHeader>

      <div className={contentColumn}>
        <div className="sticky top-16 z-10 flex min-w-0 flex-wrap items-center gap-2 bg-background py-1">
          <Select value={stream} onValueChange={(v) => v && setStream(v)}>
            <SelectTrigger className="w-28"><SelectValue /></SelectTrigger>
            <SelectContent>
              <SelectItem value="backend">backend</SelectItem>
            </SelectContent>
          </Select>

          <Select value={date || "active"} onValueChange={(v) => v && setDate(v === "active" ? "" : v)}>
            <SelectTrigger className="w-40"><SelectValue /></SelectTrigger>
            <SelectContent>
              <SelectItem value="active">Today (active)</SelectItem>
              {files
                .filter((f) => f.name !== activeFileName)
                .map((f) => {
                  const m = f.name.match(/-(\d{4}-\d{2}-\d{2})\.(?:log|jsonl)$/)
                  return m ? <SelectItem key={f.name} value={m[1]}>{m[1]}</SelectItem> : null
                })}
            </SelectContent>
          </Select>

          <Select value={level} onValueChange={(v) => v && setLevel(v)}>
            <SelectTrigger className="w-28"><SelectValue /></SelectTrigger>
            <SelectContent>
              {LEVELS.map((l) => (
                <SelectItem key={l} value={l}>{l}</SelectItem>
              ))}
            </SelectContent>
          </Select>

          <Input
            aria-label="Contains filter"
            className="w-44"
            value={contains}
            onChange={(e) => setContains(e.target.value)}
            placeholder="contains..."
          />

          <Select value={String(tailCount)} onValueChange={(v) => v && setTailCount(Number(v))}>
            <SelectTrigger className="w-24"><SelectValue /></SelectTrigger>
            <SelectContent>
              {[100, 500, 1000, 5000].map((n) => (
                <SelectItem key={n} value={String(n)}>tail {n}</SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>

        {error && (
          <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-3 text-sm text-destructive">
            {error}
          </div>
        )}

        <div className="relative min-w-0">
          <div
            ref={containerRef}
            onScroll={handleScroll}
            className="h-[calc(100vh-12rem)] min-h-[320px] overflow-auto rounded-xl border bg-muted/30 p-3 font-mono text-xs leading-5"
          >
            {lines.length === 0 && !loading ? (
              <div className="text-muted-foreground">No matching log lines.</div>
            ) : (
              lines.map((l, i) => (
                <div key={i} className={`${levelColor[levelOf(l)]} ${wrap ? "whitespace-pre-wrap break-all" : "whitespace-nowrap"}`}>
                  {l}
                </div>
              ))
            )}
          </div>
          {newLinesCount > 0 && (
            <button
              onClick={scrollToBottom}
              className="absolute bottom-3 left-1/2 -translate-x-1/2 rounded-full bg-primary px-3 py-1 text-xs text-primary-foreground shadow"
            >
              ↓ {newLinesCount} new lines
            </button>
          )}
        </div>

        <div className="flex justify-between text-xs text-muted-foreground">
          <span>
            {live
              ? `Live · ${lines.length} lines${history ? ` (of ${history.totalLines})` : ""}`
              : history
              ? `Showing ${history.returned} of ${history.filteredTotal} lines · ${history.file}`
              : "—"}
          </span>
          <span>{files.length} files</span>
        </div>
      </div>
    </div>
  )
}
