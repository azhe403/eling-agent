"use client"

import { useCallback, useEffect, useMemo, useState } from "react"
import { AlertCircle, CheckCircle2, Loader2, RefreshCw, Wrench, X } from "lucide-react"

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

interface Notification {
  id: number
  type: "success" | "error"
  title: string
  text: string
}

const MAX_NOTIFICATIONS = 4
const NOTIFICATION_TTL_MS = 4000

let nextNotificationId = 0

function ToastItem({
  notification,
  onDismiss,
}: {
  notification: Notification
  onDismiss: (id: number) => void
}) {
  useEffect(() => {
    const timer = setTimeout(() => onDismiss(notification.id), NOTIFICATION_TTL_MS)
    return () => clearTimeout(timer)
  }, [notification.id, onDismiss])

  return (
    <div className="pointer-events-auto flex w-80 max-w-[calc(100vw-2rem)] items-start gap-2.5 rounded-lg border bg-background p-3 shadow-lg animate-in fade-in-0 slide-in-from-right-8 duration-300">
      <span
        className={
          notification.type === "error"
            ? "flex size-7 shrink-0 items-center justify-center rounded-full bg-destructive/15 text-destructive"
            : "flex size-7 shrink-0 items-center justify-center rounded-full bg-emerald-500/15 text-emerald-600 dark:text-emerald-400"
        }
      >
        {notification.type === "error" ? (
          <AlertCircle className="size-4" />
        ) : (
          <CheckCircle2 className="size-4" />
        )}
      </span>
      <div className="min-w-0 flex-1">
        <p className="text-sm font-medium">{notification.title}</p>
        <p className="mt-0.5 text-xs break-words text-muted-foreground">{notification.text}</p>
      </div>
      <Button
        variant="ghost"
        size="icon"
        className="size-6 shrink-0"
        onClick={() => onDismiss(notification.id)}
        aria-label="Dismiss notification"
      >
        <X className="size-3.5" />
      </Button>
    </div>
  )
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
  const [notifications, setNotifications] = useState<Notification[]>([])

  const dismissNotification = useCallback((id: number) => {
    setNotifications((current) => current.filter((item) => item.id !== id))
  }, [])

  const notify = useCallback((type: Notification["type"], title: string, text: string) => {
    nextNotificationId += 1
    const id = nextNotificationId
    setNotifications((current) => [...current.slice(-(MAX_NOTIFICATIONS - 1)), { id, type, title, text }])
  }, [])

  const reload = useCallback(async () => {
    setLoading(true)
    try {
      const res = await fetch("/api/tools", { cache: "no-store" })
      if (!res.ok) {
        throw new Error(`Failed to fetch tools: HTTP ${res.status}`)
      }
      const data: ToolItem[] = await res.json()
      setTools(data)
    } catch (err) {
      notify("error", "Failed to load tools", err instanceof Error ? err.message : "Unknown error.")
    } finally {
      setLoading(false)
    }
  }, [notify])

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
        notify("error", "Failed to load tools", err instanceof Error ? err.message : "Unknown error.")
      })
      .finally(() => {
        if (isMounted) setLoading(false)
      })
    return () => {
      isMounted = false
    }
  }, [notify])

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
      notify("error", "Update failed", err instanceof Error ? err.message : "Unknown error.")
      throw err
    }
  }

  async function toggleTool(tool: ToolItem, enabled: boolean) {
    if (tool.isProtected) return
    const previous = tools
    setTools((current) => current.map((item) => (item.name === tool.name ? { ...item, enabled } : item)))
    setPending((current) => ({ ...current, [tool.name]: true }))
    try {
      await applyUpdate({ toolName: tool.name, enabled })
      notify(
        "success",
        `${tool.name} ${enabled ? "enabled" : "disabled"}`,
        "Applies immediately, no restart needed."
      )
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
      notify(
        "success",
        `${GROUP_TITLES[group] ?? group} ${enabled ? "enabled" : "disabled"}`,
        "Applies immediately, no restart needed."
      )
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

      <div className="pointer-events-none fixed top-4 right-4 z-50 flex w-80 max-w-[calc(100vw-2rem)] flex-col gap-2">
        {notifications.map((notification) => (
          <ToastItem key={notification.id} notification={notification} onDismiss={dismissNotification} />
        ))}
      </div>

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
          const enabledCount = toggleable.filter((tool) => tool.enabled).length
          const allEnabled = toggleable.length > 0 && enabledCount === toggleable.length
          const isMixed = enabledCount > 0 && enabledCount < toggleable.length
          return (
            <Card key={group}>
              <CardHeader>
                <div className="flex items-center gap-2">
                  <CardTitle className="text-base">{GROUP_TITLES[group] ?? group}</CardTitle>
                  <Badge variant="secondary">{members.length}</Badge>
                  <Badge variant="outline" className={isMixed ? undefined : "invisible"}>
                    mixed
                  </Badge>
                  <div className="ml-auto flex items-center gap-2">
                    <span className="text-xs text-muted-foreground">
                      {enabledCount}/{toggleable.length} on
                    </span>
                    <Switch
                      checked={allEnabled}
                      disabled={toggleable.length === 0}
                      onCheckedChange={(checked) => void toggleGroup(group, checked)}
                      aria-label={`Toggle ${group} tools`}
                    />
                  </div>
                </div>
                <CardDescription>
                  {group === "memory" && "Recall, save, search, and maintenance operations."}
                  {group === "codebase" && "Indexing and full-text search over the workspace."}
                  {group === "filesystem" && "Sandboxed file and directory operations."}
                </CardDescription>
              </CardHeader>
              <CardContent className="grid grid-cols-1 gap-3 md:grid-cols-2 xl:grid-cols-3">
                {members.map((tool) => (
                  <div key={tool.name} className="flex items-start gap-3 rounded-lg border p-3">
                    <div className="min-w-0 flex-1">
                      <div className="flex min-h-6 flex-wrap items-center gap-2">
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
