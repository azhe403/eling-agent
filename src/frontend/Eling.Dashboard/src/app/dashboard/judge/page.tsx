"use client"

import { useCallback, useEffect, useState } from "react"
import Link from "next/link"
import {
  AlertCircle,
  CheckCircle2,
  HelpCircle,
  Loader2,
  PlugZap,
  RefreshCw,
  Scale,
  ShieldAlert,
  ShieldCheck,
} from "lucide-react"

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
  CardFooter,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"
import { Separator } from "@/components/ui/separator"
import { SidebarTrigger } from "@/components/ui/sidebar"
import { Switch } from "@/components/ui/switch"

interface JudgeView {
  enabled: boolean
  baseUrl: string | null
  model: string | null
  hasApiKey: boolean
  isConfigured: boolean
  timeoutSeconds: number | null
}

interface TestResponse {
  ok: boolean
  message: string
}

interface ModelsResponse {
  models: string[]
}

export default function JudgeConfigPage() {
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [testing, setTesting] = useState(false)
  const [fetchingModels, setFetchingModels] = useState(false)

  const [enabled, setEnabled] = useState(false)
  const [baseUrl, setBaseUrl] = useState("")
  const [model, setModel] = useState("")
  const [apiKey, setApiKey] = useState("")
  const [timeoutSeconds, setTimeoutSeconds] = useState("10")
  const [hasApiKey, setHasApiKey] = useState(false)
  const [isConfigured, setIsConfigured] = useState(false)

  const [availableModels, setAvailableModels] = useState<string[]>([])
  const [message, setMessage] = useState<{ type: "success" | "error"; text: string } | null>(null)
  const [testResult, setTestResult] = useState<TestResponse | null>(null)

  const fetchModelsForUrl = useCallback(
    async (
      targetBaseUrl: string,
      currentModel: string,
      key?: string,
      showFeedback = false
    ) => {
      if (!targetBaseUrl.trim()) return
      setFetchingModels(true)
      if (showFeedback) setMessage(null)

      try {
        const payload = {
          baseUrl: targetBaseUrl.trim() || null,
          apiKey: key?.trim() || null,
        }

        const res = await fetch("/api/judge/models", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(payload),
        })

        if (!res.ok) {
          const text = await res.text()
          throw new Error(text || `Failed to fetch models: HTTP ${res.status}`)
        }

        const data: ModelsResponse = await res.json()
        const list = data.models ?? []
        setAvailableModels(list)

        if (list.length > 0) {
          // If current model is empty or not in the fetched list, select the first item
          if (!currentModel || !list.includes(currentModel)) {
            setModel(list[0])
          }
        }

        if (showFeedback) {
          setMessage({
            type: "success",
            text: `Retrieved ${list.length} model(s) from provider.`,
          })
        }
      } catch (err) {
        if (showFeedback) {
          setMessage({
            type: "error",
            text: err instanceof Error ? err.message : "Failed to fetch models from provider.",
          })
        }
      } finally {
        setFetchingModels(false)
      }
    },
    []
  )

  const reload = useCallback(async () => {
    setLoading(true)
    try {
      const res = await fetch("/api/judge/config", { cache: "no-store" })
      if (!res.ok) {
        throw new Error(`Failed to fetch judge config: HTTP ${res.status}`)
      }
      const data: JudgeView = await res.json()
      setEnabled(data.enabled ?? false)
      setBaseUrl(data.baseUrl ?? "")
      const initialModel = data.model ?? ""
      setModel(initialModel)
      setHasApiKey(data.hasApiKey ?? false)
      setIsConfigured(data.isConfigured ?? false)
      setTimeoutSeconds(data.timeoutSeconds != null ? String(data.timeoutSeconds) : "10")

      if (data.baseUrl) {
        void fetchModelsForUrl(data.baseUrl, initialModel)
      }
    } catch (err) {
      setMessage({
        type: "error",
        text: err instanceof Error ? err.message : "Failed to load judge configuration.",
      })
    } finally {
      setLoading(false)
    }
  }, [fetchModelsForUrl])

  useEffect(() => {
    let ignore = false

    async function loadInitial() {
      try {
        const res = await fetch("/api/judge/config", { cache: "no-store" })
        if (!res.ok) {
          throw new Error(`Failed to fetch judge config: HTTP ${res.status}`)
        }
        const data: JudgeView = await res.json()
        if (ignore) return

        setEnabled(data.enabled ?? false)
        setBaseUrl(data.baseUrl ?? "")
        const initialModel = data.model ?? ""
        setModel(initialModel)
        setHasApiKey(data.hasApiKey ?? false)
        setIsConfigured(data.isConfigured ?? false)
        setTimeoutSeconds(data.timeoutSeconds != null ? String(data.timeoutSeconds) : "10")

        if (data.baseUrl) {
          const payload = {
            baseUrl: data.baseUrl.trim() || null,
          }
          const modelsRes = await fetch("/api/judge/models", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(payload),
          })
          if (modelsRes.ok && !ignore) {
            const modelsData: ModelsResponse = await modelsRes.json()
            const list = modelsData.models ?? []
            setAvailableModels(list)
            if (list.length > 0 && (!initialModel || !list.includes(initialModel))) {
              setModel(list[0])
            }
          }
        }
      } catch (err) {
        if (!ignore) {
          setMessage({
            type: "error",
            text: err instanceof Error ? err.message : "Failed to load judge configuration.",
          })
        }
      } finally {
        if (!ignore) {
          setLoading(false)
        }
      }
    }

    void loadInitial()

    return () => {
      ignore = true
    }
  }, [])

  const handleSave = async (e: React.FormEvent) => {
    e.preventDefault()
    setMessage(null)
    setSaving(true)

    try {
      const payload = {
        enabled,
        baseUrl: baseUrl.trim() || null,
        model: model.trim() || null,
        apiKey: apiKey.trim() || null,
        timeoutSeconds: Number(timeoutSeconds) > 0 ? Number(timeoutSeconds) : 10,
      }

      const res = await fetch("/api/judge/config", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      })

      if (!res.ok) {
        throw new Error(`Failed to save configuration: HTTP ${res.status}`)
      }

      const updated: JudgeView = await res.json()
      setEnabled(updated.enabled)
      setBaseUrl(updated.baseUrl ?? "")
      setModel(updated.model ?? "")
      setHasApiKey(updated.hasApiKey)
      setIsConfigured(updated.isConfigured)
      setTimeoutSeconds(updated.timeoutSeconds != null ? String(updated.timeoutSeconds) : "10")
      setApiKey("")

      setMessage({
        type: "success",
        text: "Semantic judge configuration saved successfully.",
      })
    } catch (err) {
      setMessage({
        type: "error",
        text: err instanceof Error ? err.message : "Failed to save configuration.",
      })
    } finally {
      setSaving(false)
    }
  }

  const handleTestConnection = async () => {
    setTesting(true)
    setTestResult(null)
    setMessage(null)

    try {
      const payload = {
        baseUrl: baseUrl.trim() || null,
        model: model.trim() || null,
        apiKey: apiKey.trim() || null,
      }

      const res = await fetch("/api/judge/test", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      })

      if (!res.ok) {
        const text = await res.text()
        throw new Error(text || `Test failed with status ${res.status}`)
      }

      const data: TestResponse = await res.json()
      setTestResult(data)
    } catch (err) {
      setTestResult({
        ok: false,
        message: err instanceof Error ? err.message : "Connection test failed.",
      })
    } finally {
      setTesting(false)
    }
  }

  const handleFetchModels = async () => {
    await fetchModelsForUrl(baseUrl, model, apiKey, true)
  }

  // Preserve the exact order of models from the fetch response
  const displayedModels =
    availableModels.length > 0
      ? model && !availableModels.includes(model)
        ? [...availableModels, model]
        : availableModels
      : model
        ? [model]
        : []

  return (
    <>
      <header className="flex h-16 shrink-0 items-center gap-2 px-4">
        <SidebarTrigger className="-ml-1" />
        <Separator
          orientation="vertical"
          className="mr-2 data-vertical:h-4 data-vertical:self-auto"
        />
        <Breadcrumb>
          <BreadcrumbList>
            <BreadcrumbItem>
              <BreadcrumbLink render={<Link href="/dashboard" />}>Dashboard</BreadcrumbLink>
            </BreadcrumbItem>
            <BreadcrumbSeparator />
            <BreadcrumbItem>
              <BreadcrumbPage>Semantic Judge</BreadcrumbPage>
            </BreadcrumbItem>
          </BreadcrumbList>
        </Breadcrumb>
      </header>

      <div className="mx-auto flex w-full max-w-4xl flex-1 flex-col gap-6 p-4 pt-0">
        {/* Top Status Card */}
        <Card>
          <CardHeader className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between pb-3">
            <div className="flex items-center gap-3">
              <div className="flex aspect-square size-10 items-center justify-center rounded-lg bg-primary/10 text-primary">
                <Scale className="size-5" />
              </div>
              <div>
                <CardTitle className="text-base font-semibold">Memory Semantic Judge</CardTitle>
                <CardDescription className="text-xs">
                  LLM-backed dedup & relationship evaluator for{" "}
                  <code className="bg-muted px-1 py-0.5 rounded text-[11px]">memory_save</code>
                </CardDescription>
              </div>
            </div>

            <div className="flex items-center gap-2">
              {loading ? (
                <div className="flex items-center gap-1.5 text-xs text-muted-foreground">
                  <Loader2 className="size-3.5 animate-spin" />
                  <span>Loading...</span>
                </div>
              ) : enabled && isConfigured ? (
                <Badge
                  variant="outline"
                  className="gap-1.5 border-emerald-500/30 bg-emerald-500/10 text-emerald-600 dark:text-emerald-400 font-medium py-1 px-2.5"
                >
                  <ShieldCheck className="size-3.5" />
                  Active & Configured
                </Badge>
              ) : enabled && !isConfigured ? (
                <Badge
                  variant="outline"
                  className="gap-1.5 border-amber-500/30 bg-amber-500/10 text-amber-600 dark:text-amber-400 font-medium py-1 px-2.5"
                >
                  <ShieldAlert className="size-3.5" />
                  Enabled (Incomplete)
                </Badge>
              ) : (
                <Badge variant="secondary" className="gap-1.5 font-medium py-1 px-2.5">
                  Disabled (Heuristic Dedup)
                </Badge>
              )}

              <Button
                variant="outline"
                size="sm"
                onClick={() => void reload()}
                disabled={loading}
                title="Reload configuration"
              >
                <RefreshCw className={`size-3.5 ${loading ? "animate-spin" : ""}`} />
              </Button>
            </div>
          </CardHeader>

          <CardContent className="pt-0">
            <div className="rounded-lg bg-muted/50 p-3 text-xs leading-relaxed text-muted-foreground">
              The semantic judge acts as an authoritative decider for incoming memories (determining if a memory is an exact revision, contradiction, superset, or unrelated). When disabled or unconfigured, Eling automatically and safely degrades to bag-of-words heuristic similarity.
            </div>
          </CardContent>
        </Card>

        {/* Notification alerts */}
        {message && (
          <div
            className={`flex items-start gap-2.5 rounded-lg border p-3.5 text-sm ${
              message.type === "success"
                ? "border-emerald-500/30 bg-emerald-500/10 text-emerald-700 dark:text-emerald-300"
                : "border-destructive/40 bg-destructive/10 text-destructive"
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

        {/* Configuration Form Card */}
        <form onSubmit={handleSave} autoComplete="off">
          <Card>
            <CardHeader className="border-b pb-4">
              <div className="flex items-center justify-between">
                <div className="flex flex-col gap-1">
                  <Label htmlFor="enabled" className="text-sm font-medium cursor-pointer">
                    Enable Semantic Judge
                  </Label>
                  <CardDescription className="text-xs">
                    Participates in dedup decisions during memory save operations.
                  </CardDescription>
                </div>
                <Switch
                  id="enabled"
                  checked={enabled}
                  onCheckedChange={(checked) => setEnabled(Boolean(checked))}
                />
              </div>
            </CardHeader>

            <CardContent className="flex flex-col gap-5 pt-5">
              {/* Provider Endpoint */}
              <div className="flex flex-col gap-2">
                <Label htmlFor="baseUrl" className="text-sm font-medium flex items-center gap-1.5">
                  Provider Base URL
                  <span className="text-muted-foreground font-normal text-xs">(OpenAI-compatible)</span>
                </Label>
                <Input
                  id="baseUrl"
                  value={baseUrl}
                  onChange={(e) => setBaseUrl(e.target.value)}
                  placeholder="https://api.openai.com/v1 or http://127.0.0.1:11434/v1"
                  required={enabled}
                  autoComplete="off"
                  data-1p-ignore="true"
                  data-lpignore="true"
                />
                <span className="text-[11px] text-muted-foreground">
                  The root API endpoint where chat completions are accessible.
                </span>
              </div>

              {/* Model Selector Dropdown */}
              <div className="flex flex-col gap-2">
                <div className="flex items-center justify-between">
                  <Label htmlFor="model" className="text-sm font-medium">
                    Model
                  </Label>
                  <Button
                    type="button"
                    variant="ghost"
                    size="sm"
                    className="h-7 text-xs gap-1.5 px-2"
                    onClick={handleFetchModels}
                    disabled={fetchingModels || !baseUrl.trim()}
                  >
                    {fetchingModels ? (
                      <Loader2 className="size-3 animate-spin" />
                    ) : (
                      <RefreshCw className="size-3" />
                    )}
                    Fetch Models
                  </Button>
                </div>

                <div className="w-full">
                  <Select
                    value={model}
                    onValueChange={(val) => {
                      if (val) setModel(val)
                    }}
                    disabled={fetchingModels || displayedModels.length === 0}
                  >
                    <SelectTrigger className="w-full">
                      <SelectValue placeholder={fetchingModels ? "Fetching models..." : "Select model"} />
                    </SelectTrigger>
                    <SelectContent>
                      {displayedModels.map((m) => (
                        <SelectItem key={m} value={m}>
                          {m}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                </div>

                <span className="text-[11px] text-muted-foreground">
                  {displayedModels.length === 0
                    ? "Click 'Fetch Models' to load available models from the provider endpoint."
                    : "LLM model to invoke for semantic deduplication decisions."}
                </span>
              </div>

              {/* API Key */}
              <div className="flex flex-col gap-2">
                <div className="flex items-center justify-between">
                  <Label htmlFor="apiKey" className="text-sm font-medium">
                    API Key
                  </Label>
                  {hasApiKey && (
                    <Badge variant="outline" className="border-emerald-500/30 text-emerald-600 dark:text-emerald-400 text-[11px] py-0">
                      ✓ Key stored on server
                    </Badge>
                  )}
                </div>
                <Input
                  id="apiKey"
                  type="password"
                  value={apiKey}
                  onChange={(e) => setApiKey(e.target.value)}
                  placeholder={hasApiKey ? "Leave blank to keep stored API key" : "sk-..."}
                  autoComplete="new-password"
                  data-1p-ignore="true"
                  data-lpignore="true"
                />
                <span className="text-[11px] text-muted-foreground">
                  Stored securely in <code className="bg-muted px-1 py-0.5 rounded text-[11px]">~/.config/eling/config/semantic-judge.json</code> and never returned to the browser.
                </span>
              </div>

              {/* Timeout Seconds */}
              <div className="flex flex-col gap-2">
                <Label htmlFor="timeoutSeconds" className="text-sm font-medium flex items-center gap-1.5">
                  Initial Attempt Timeout (seconds)
                  <span className="text-muted-foreground font-normal text-xs" title="Escalates on second retry attempt via Polly">
                    <HelpCircle className="size-3.5 inline" />
                  </span>
                </Label>
                <Input
                  id="timeoutSeconds"
                  type="number"
                  min="1"
                  max="120"
                  value={timeoutSeconds}
                  onChange={(e) => setTimeoutSeconds(e.target.value)}
                  className="max-w-[200px]"
                  autoComplete="off"
                />
                <span className="text-[11px] text-muted-foreground">
                  Attempt 1 timeout duration (default 10s). If it times out, Attempt 2 automatically scales to 1.5x before degrading safely to heuristic fallback.
                </span>
              </div>

              {/* Test connection result */}
              {testResult && (
                <div
                  className={`flex items-start gap-2.5 rounded-lg border p-3 text-xs ${
                    testResult.ok
                      ? "border-emerald-500/30 bg-emerald-500/10 text-emerald-700 dark:text-emerald-300"
                      : "border-destructive/40 bg-destructive/10 text-destructive"
                  }`}
                >
                  {testResult.ok ? (
                    <CheckCircle2 className="mt-0.5 size-4 shrink-0" />
                  ) : (
                    <AlertCircle className="mt-0.5 size-4 shrink-0" />
                  )}
                  <div className="flex-1">
                    <div className="font-semibold">{testResult.ok ? "Connection Successful" : "Connection Failed"}</div>
                    <div className="mt-0.5">{testResult.message}</div>
                  </div>
                </div>
              )}
            </CardContent>

            <CardFooter className="flex flex-wrap items-center gap-3 pt-3 border-t">
              <Button type="submit" disabled={saving}>
                {saving && <Loader2 className="size-4 animate-spin mr-1.5" />}
                Save Configuration
              </Button>

              <Button
                type="button"
                variant="outline"
                onClick={handleTestConnection}
                disabled={testing || (!baseUrl.trim() && !isConfigured)}
                className="gap-1.5"
              >
                {testing ? (
                  <Loader2 className="size-4 animate-spin" />
                ) : (
                  <PlugZap className="size-4" />
                )}
                Test Connection
              </Button>
            </CardFooter>
          </Card>
        </form>
      </div>
    </>
  )
}
