"use client"

import { useCallback, useEffect, useState } from "react"
import { AlertCircle, CheckCircle2, FileJson, FileText, Loader2, RefreshCw } from "lucide-react"

import { DashboardHeader } from "@/components/dashboard-header"
import { Button } from "@/components/ui/button"
import {
  Card,
  CardContent,
  CardDescription,
  CardFooter,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"

type LogFormat = "text" | "jsonl"

interface LogConfigView {
  format: string
}

const FORMATS: Array<{
  value: LogFormat
  title: string
  description: string
  ext: string
  icon: typeof FileText
}> = [
  {
    value: "text",
    title: "Text",
    description: "The historical template line: timestamp, level, pid, project, message.",
    ext: "backend.log",
    icon: FileText,
  },
  {
    value: "jsonl",
    title: "JSON Lines",
    description: "One JSON object per line, with the enriched properties as fields.",
    ext: "backend.jsonl",
    icon: FileJson,
  },
]

export function Settings() {
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [format, setFormat] = useState<LogFormat>("text")
  const [pending, setPending] = useState<LogFormat>("text")
  const [message, setMessage] = useState<{ type: "success" | "error"; text: string } | null>(null)

  const reload = useCallback(async () => {
    setLoading(true)
    setMessage(null)
    try {
      const res = await fetch("/api/logs/config")
      if (!res.ok) throw new Error(`Failed to load settings (HTTP ${res.status})`)
      const data = (await res.json()) as LogConfigView
      const next: LogFormat = data.format === "jsonl" ? "jsonl" : "text"
      setFormat(next)
      setPending(next)
    } catch (err) {
      setMessage({
        type: "error",
        text: err instanceof Error ? err.message : "Failed to load settings.",
      })
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    const t = setTimeout(() => {
      void reload()
    }, 0)
    return () => clearTimeout(t)
  }, [reload])

  const dirty = pending !== format

  const handleSave = async (e: React.FormEvent) => {
    e.preventDefault()
    setSaving(true)
    setMessage(null)
    try {
      const res = await fetch("/api/logs/config", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ format: pending }),
      })
      if (!res.ok) throw new Error(`Failed to save (HTTP ${res.status})`)
      const updated = (await res.json()) as LogConfigView
      const next: LogFormat = updated.format === "jsonl" ? "jsonl" : "text"
      setFormat(next)
      setPending(next)
      setMessage({
        type: "success",
        text: `Log format saved as ${next}. This host switched immediately; running MCP sessions keep their current format until they end.`,
      })
    } catch (err) {
      setMessage({
        type: "error",
        text: err instanceof Error ? err.message : "Failed to save settings.",
      })
    } finally {
      setSaving(false)
    }
  }

  return (
    <>
      <DashboardHeader label="Settings">
        <Button variant="outline" size="sm" onClick={() => void reload()} disabled={loading || saving}>
          <RefreshCw className={loading ? "size-4 animate-spin" : "size-4"} />
          <span className="hidden sm:inline-block">Refresh</span>
        </Button>
      </DashboardHeader>

      <div className="flex w-full flex-1 flex-col gap-4 p-4 pt-4">
        <Card>
          <CardHeader>
            <CardTitle className="text-base">Logs</CardTitle>
            <CardDescription>
              How Eling writes its rolling log files in{" "}
              <code className="bg-muted rounded px-1 py-0.5 text-xs">~/.local/share/eling/logs</code>
            </CardDescription>
          </CardHeader>

          <CardContent className="flex flex-col gap-3">
            <div className="rounded-lg bg-muted/50 p-3 text-xs leading-relaxed text-muted-foreground">
              The format is a per-user setting stored in{" "}
              <code className="bg-muted rounded px-1 py-0.5 text-xs">config/log.json</code> and shared by
              every Eling process. The console keeps the plain-text template in both modes, because it is
              read by a person, not a tool.
            </div>

            {message && (
              <div
                className={`flex items-start gap-2.5 rounded-lg border p-3 text-sm ${
                  message.type === "success"
                    ? "border-emerald-500/30 bg-emerald-500/10 text-emerald-700 dark:text-emerald-300"
                    : "border-destructive/50 bg-destructive/10 text-destructive"
                }`}
              >
                {message.type === "success" ? (
                  <CheckCircle2 className="mt-0.5 size-4 shrink-0" />
                ) : (
                  <AlertCircle className="mt-0.5 size-4 shrink-0" />
                )}
                <div className="flex-1">{message.text}</div>
              </div>
            )}
          </CardContent>
        </Card>

        <form onSubmit={handleSave}>
          <Card>
            <CardHeader className="border-b">
              <CardTitle className="text-sm font-medium">File format</CardTitle>
              <CardDescription className="text-xs">
                Switching mid-day does not rewrite history: the outgoing file is archived under its own
                date and the new one starts clean.
              </CardDescription>
            </CardHeader>

            <CardContent className="flex flex-col gap-2">
              {FORMATS.map((option) => {
                const Icon = option.icon
                const selected = pending === option.value
                return (
                  <button
                    key={option.value}
                    type="button"
                    onClick={() => setPending(option.value)}
                    disabled={loading || saving}
                    aria-pressed={selected}
                    className={`flex w-full items-start gap-3 rounded-lg border p-3 text-left transition-colors ${
                      selected
                        ? "border-primary/60 bg-primary/5"
                        : "border-border hover:bg-muted/50"
                    }`}
                  >
                    <Icon
                      className={`mt-0.5 size-4 shrink-0 ${
                        selected ? "text-primary" : "text-muted-foreground"
                      }`}
                    />
                    <div className="flex min-w-0 flex-1 flex-col gap-1">
                      <div className="flex flex-wrap items-center gap-2">
                        <span className="text-sm font-medium">{option.title}</span>
                        <code className="bg-muted rounded px-1 py-0.5 text-[11px] text-muted-foreground">
                          {option.ext}
                        </code>
                        {format === option.value && (
                          <span className="rounded-full border border-border/50 bg-muted/30 px-2 py-0.5 text-[10px] font-medium uppercase tracking-wide text-muted-foreground">
                            current
                          </span>
                        )}
                      </div>
                      <span className="text-xs text-muted-foreground">{option.description}</span>
                    </div>
                  </button>
                )
              })}
            </CardContent>

            <CardFooter className="flex flex-col gap-3 border-t sm:flex-row sm:items-center sm:justify-between">
              <span className="text-xs text-muted-foreground">
                Applies to new MCP sessions immediately; this dashboard applies it now.
              </span>
              <div className="flex items-center gap-2">
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  onClick={() => setPending(format)}
                  disabled={!dirty || saving}
                >
                  Discard
                </Button>
                <Button type="submit" size="sm" disabled={!dirty || saving || loading}>
                  {saving ? <Loader2 className="size-3.5 animate-spin" /> : null}
                  Save
                </Button>
              </div>
            </CardFooter>
          </Card>
        </form>
      </div>
    </>
  )
}
