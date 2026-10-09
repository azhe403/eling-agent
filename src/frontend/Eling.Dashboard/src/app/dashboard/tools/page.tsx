"use client"

import { useCallback, useEffect, useMemo, useState } from "react"
import { AlertCircle, CheckCircle2, Loader2, RefreshCw, Wrench } from "lucide-react"

import {
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbLink,
  BreadcrumbList,
  BreadcrumbPage,
  BreadcrumbSeparator,
} from "@/components/ui/breadcrumb"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
import { Input } from "@/components/ui/input"
import { Separator } from "@/components/ui/separator"
import { SidebarTrigger } from "@/components/ui/sidebar"
import { Switch } from "@/components/ui/switch"

interface ToolItem {
  name: string
  group: string
  description: string
  enabled: boolean
  isProtected: boolean
}

const GROUP_ORDER = ["memory", "codebase", "filesystem"] as const

const GROUP_TITLES: Record<string, string> = {
  memory: "Memory Tools",
  codebase: "Codebase Tools",
  filesystem: "Filesystem Tools",
}

export default function ToolsPage() {
  const [loading, setLoading] = useState(true)
  const [tools, setTools] = useState<ToolItem[]>([])
  const [query, setQuery] = useState("")
  const [pending, setPending] = useState<Record<string, boolean>>({})
  const [message, setMessage] = useState<{ type: "success" | "error"; text: string } | null>(null)

  const reload = useCallback(async () => {
    setLoading(true)
    setMessage(null)
    try {
      const res = await fetch("/api/tools", { cache: "no-store" })
      if (!res.ok) {
        throw new Error(`Failed to fetch tools: HTTP ${res.status}`)
      }
      const data: ToolItem[] = await res.json()
      setTools(data)
    } catch (err) {
      setMessage({
        type: "error",
        text: err instanceof Error ? err.message : "Failed to load tools.",
      })
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    let isMounted = true
    fetch("/api/tools", { cache: "no-store" })
      .then((res) => {
        if (!res.ok) {
          throw new Error(`Failed to fetch tools: HTTP ${res.status}`)
        }
        return res.json() as Promise<ToolItem[]>
      })
      .then((data) => {
        if (!isMounted) return
        setTools(data)
      })
      .catch((err: unknown) => {
        if (!isMounted) return
        setMessage({
          type: "error",
          text: err instanceof Error ? err.message : "Failed to load tools.",
        })
      })
      .finally(() => {
        if (isMounted) setLoading(false)
      })
    return () => {
      isMounted = false
    }
  }, [])

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase()
    if (!q) return tools
    return tools.filter(
      (tool) =>
        tool.name.toLowerCase().includes(q) || tool.description.toLowerCase().includes(q)
    )
  }, [tools, query])

  const groups = useMemo(() => {
    const known = GROUP_ORDER.filter((group) => filtered.some((tool) => tool.group === group))
    const extra = [...new Set(filtered.map((tool) => tool.group))].filter(
      (group) => !(GROUP_ORDER as readonly string[]).includes(group)
    )
    return [...known, ...extra]
  }, [filtered])

  async function applyUpdate(payload: { toolName?: string; group?: string; enabled: boolean }) {
    setMessage(null)
    try {
      const res = await fetch("/api/tools", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      })
      if (!res.ok) {
        const text = await res.text()
        throw new Error(text || `Update failed: HTTP ${res.status}`)
      }
      const data: ToolItem[] = await res.json()
      setTools(data)
    } catch (err) {
      setMessage({
        type: "error",
        text: err instanceof Error ? err.message : "Failed to update tool policy.",
      })
    }
  }

  async function toggleTool(tool: ToolItem, enabled: boolean) {
    if (tool.isProtected) return
    const previous = tools
    setTools((current) => current.map((item) => (item.name === tool.name ? { ...item, enabled } : item)))
    setPending((current) => ({ ...current, [tool.name]: true }))
    try {
      await applyUpdate({ toolName: tool.name, enabled })
      setMessage({
        type: "success",
        text: `${tool.name} ${enabled ? "enabled" : "disabled"}. Applies immediately, no restart needed.`,
      })
    } catch {
      setTools(previous)
    } finally {
      setPending((current) => {
        const next = { ...current }
        delete next[tool.name]
        return next
      })
    }
  }

  async function toggleGroup(group: string, enabled: boolean) {
    const previous = tools
    setTools((current) =>
      current.map((item) => (item.group === group && !item.isProtected ? { ...item, enabled } : item))
    )
    try {
      await applyUpdate({ group, enabled })
      setMessage({
        type: "success",
        text: `${GROUP_TITLES[group] ?? group} ${enabled ? "enabled" : "disabled"}.`,
      })
    } catch {
      setTools(previous)
    }
  }

  const enabledCount = tools.filter((tool) => tool.enabled).length

  return (
    <div className="flex flex-col gap-4 p-4">
      <header className="flex items-center gap-2">
        <SidebarTrigger />
        <Separator orientation="vertical" className="mr-2 h-4" />
        <Breadcrumb>
          <BreadcrumbList>
            <BreadcrumbItem>
              <BreadcrumbLink href="/dashboard">Dashboard</BreadcrumbLink>
            </BreadcrumbItem>
            <BreadcrumbSeparator />
            <BreadcrumbItem>
              <BreadcrumbPage>Tools</BreadcrumbPage>
            </BreadcrumbItem>
          </BreadcrumbList>
        </Breadcrumb>
      </header>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Wrench className="size-4" />
            Tool Management
          </CardTitle>
          <CardDescription>
            {loading
              ? "Loading tool policy…"
              : `${enabledCount} of ${tools.length} tools enabled. Changes apply immediately without restarting.`}
          </CardDescription>
        </CardHeader>
        <CardContent className="flex flex-col gap-3 sm:flex-row sm:items-center">
          <Input
            placeholder="Search tools by name or description…"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            className="sm:max-w-sm"
          />
          <div className="flex gap-2 sm:ml-auto">
            <Button variant="outline" size="sm" onClick={() => void reload()} disabled={loading}>
              <RefreshCw className="size-4" />
              Refresh
            </Button>
          </div>
        </CardContent>
      </Card>

      {message && (
        <div
          className={
            message.type === "error"
              ? "flex items-center gap-2 rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm"
              : "flex items-center gap-2 rounded-md border border-emerald-500/40 bg-emerald-500/10 px-3 py-2 text-sm"
          }
        >
          {message.type === "error" ? (
            <AlertCircle className="size-4 shrink-0" />
          ) : (
            <CheckCircle2 className="size-4 shrink-0" />
          )}
          <span>{message.text}</span>
        </div>
      )}

      {loading ? (
        <Card>
          <CardContent className="flex items-center gap-2 py-8 text-sm text-muted-foreground">
            <Loader2 className="size-4 animate-spin" />
            Loading tools…
          </CardContent>
        </Card>
      ) : (
        groups.map((group) => {
          const members = filtered.filter((tool) => tool.group === group)
          if (members.length === 0) return null
          const toggleable = members.filter((tool) => !tool.isProtected)
          const allEnabled = toggleable.length > 0 && toggleable.every((tool) => tool.enabled)
          return (
            <Card key={group}>
              <CardHeader>
                <div className="flex items-center gap-2">
                  <CardTitle className="text-base">{GROUP_TITLES[group] ?? group}</CardTitle>
                  <Badge variant="secondary">{members.length}</Badge>
                  <div className="ml-auto flex gap-2">
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={() => void toggleGroup(group, true)}
                      disabled={allEnabled}
                    >
                      Enable all
                    </Button>
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={() => void toggleGroup(group, false)}
                      disabled={toggleable.length === 0 || toggleable.every((tool) => !tool.enabled)}
                    >
                      Disable all
                    </Button>
                  </div>
                </div>
                <CardDescription>
                  {group === "memory" && "Recall, save, search, and maintenance operations."}
                  {group === "codebase" && "Indexing and full-text search over the workspace."}
                  {group === "filesystem" && "Sandboxed file and directory operations."}
                </CardDescription>
              </CardHeader>
              <CardContent className="flex flex-col divide-y">
                {members.map((tool) => (
                  <div key={tool.name} className="flex items-start gap-3 py-3">
                    <div className="min-w-0 flex-1">
                      <div className="flex flex-wrap items-center gap-2">
                        <code className="text-sm font-medium">{tool.name}</code>
                        {tool.isProtected && <Badge variant="outline">protected</Badge>}
                        {!tool.enabled && <Badge variant="secondary">disabled</Badge>}
                      </div>
                      <p className="mt-1 text-sm text-muted-foreground">{tool.description}</p>
                    </div>
                    <Switch
                      checked={tool.enabled}
                      disabled={tool.isProtected || pending[tool.name] === true}
                      onCheckedChange={(checked) => void toggleTool(tool, checked)}
                      aria-label={`Toggle ${tool.name}`}
                    />
                  </div>
                ))}
              </CardContent>
            </Card>
          )
        })
      )}
    </div>
  )
}
