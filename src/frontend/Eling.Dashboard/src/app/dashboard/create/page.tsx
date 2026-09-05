"use client"

import { useEffect, useRef, useState, useTransition } from "react"
import { useRouter } from "next/navigation"
import { Loader2 } from "lucide-react"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Separator } from "@/components/ui/separator"
import { SidebarTrigger } from "@/components/ui/sidebar"
import { useAutoHeight } from "@/hooks/use-auto-height"

const TYPES = ["Fact", "Preference", "Decision", "Lesson", "Note"] as const
type MemoryType = (typeof TYPES)[number]

type Runtime = { projectRoot: string; dataDirectory: string }

// Discriminated union: scope is either the current project (no projectRoot in URL),
// an explicit project (projectRoot required), or the global scope.
type Scope =
  | { kind: "project" }
  | { kind: "scoped"; projectRoot: string }
  | { kind: "global" }

const SCOPE_PROJECT: Scope = { kind: "project" }
const SCOPE_GLOBAL: Scope = { kind: "global" }

function scopeToUrl(scope: Scope): string {
  switch (scope.kind) {
    case "project":
      return "/api/memories"
    case "global":
      return "/api/global/memories"
    case "scoped":
      return `/api/project/memories?projectRoot=${encodeURIComponent(scope.projectRoot)}`
  }
}

function scopeLabel(s: Scope): string {
  if (s.kind === "project") return "📁 Current Project"
  if (s.kind === "global") return "🌐 Global Scope"
  return `📁 ${s.projectRoot.split("\\").pop() ?? s.projectRoot.split("/").pop() ?? s.projectRoot} (Project)`
}

function scopeValue(s: Scope): string {
  if (s.kind === "project") return "project"
  if (s.kind === "global") return "global"
  return s.projectRoot
}

function parseScopeValue(value: string): Scope {
  if (value === "project") return SCOPE_PROJECT
  if (value === "global") return SCOPE_GLOBAL
  return { kind: "scoped", projectRoot: value }
}

export default function CreateMemoryPage() {
  const router = useRouter()
  const [content, setContent] = useState("")
  const [type, setType] = useState<MemoryType>("Note")
  const [tags, setTags] = useState("")
  const [scope, setScope] = useState<Scope>(SCOPE_PROJECT)
  const [runtimes, setRuntimes] = useState<Runtime[]>([])
  const [error, setError] = useState<string | null>(null)
  const [isPending, startTransition] = useTransition()
  const textareaRef = useRef<HTMLTextAreaElement | null>(null)

  useAutoHeight(textareaRef, [content])

  useEffect(() => {
    async function loadRuntimes() {
      try {
        const res = await fetch("/api/coordinator/runtimes", { cache: "no-store" })
        if (res.ok) {
          const data: Runtime[] = await res.json()
          setRuntimes(data)
          // Default to the first available project runtime if no explicit choice yet.
          if (data.length > 0) {
            setScope((prev) =>
              prev.kind === "project" ? { kind: "scoped", projectRoot: data[0].projectRoot } : prev,
            )
          }
        }
      } catch {
        // ignore — leave the default scope
      }
    }
    loadRuntimes()
  }, [])

  function submit(e: React.FormEvent) {
    e.preventDefault()
    if (!content.trim()) {
      setError("Content is required.")
      return
    }

    setError(null)
    const payload = {
      content: content.trim(),
      type,
      tags: tags
        .split(",")
        .map((t) => t.trim())
        .filter(Boolean),
    }
    const url = scopeToUrl(scope)

    startTransition(async () => {
      try {
        const res = await fetch(url, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(payload),
        })
        if (!res.ok) throw new Error(`API returned ${res.status}`)
        router.push("/dashboard/memories/")
        // Note: no setState after router.push — the component unmounts on navigation.
      } catch (e) {
        setError(e instanceof Error ? e.message : "Failed to save memory")
      }
    })
  }

  const isSubmitting = isPending || !content.trim()

  return (
    <>
      <header className="flex h-16 shrink-0 items-center gap-2 px-4">
        <SidebarTrigger className="-ml-1" />
        <Separator
          orientation="vertical"
          className="mr-2 data-vertical:h-4 data-vertical:self-auto"
        />
        <h1 className="text-sm font-medium">Create Memory</h1>
      </header>

      <div className="mx-auto flex w-full max-w-2xl flex-1 flex-col gap-4 p-4 pt-0">
        <form onSubmit={submit} className="flex flex-col gap-4">
          <div className="flex flex-col gap-2">
            <label htmlFor="content" className="text-sm font-medium">
              Content <span className="text-destructive">*</span>
            </label>
            <textarea
              ref={textareaRef}
              id="content"
              value={content}
              onChange={(e) => setContent(e.target.value)}
              placeholder="What should Eling remember?"
              className="rounded-lg border border-input bg-transparent px-3 py-2 text-sm placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring resize-y min-h-[80px] leading-relaxed overflow-hidden"
            />
          </div>

          <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
            <div className="flex flex-col gap-2">
              <label htmlFor="scope" className="text-sm font-medium">
                Scope
              </label>
              <select
                id="scope"
                value={scopeValue(scope)}
                onChange={(e) => setScope(parseScopeValue(e.target.value))}
                className="h-9 rounded-md border border-input bg-transparent px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              >
                {runtimes.map((r) => (
                  <option key={r.projectRoot} value={r.projectRoot}>
                    {scopeLabel({ kind: "scoped", projectRoot: r.projectRoot })}
                  </option>
                ))}
                {runtimes.length === 0 && <option value="project">{scopeLabel(SCOPE_PROJECT)}</option>}
                <option value="global">{scopeLabel(SCOPE_GLOBAL)}</option>
              </select>
            </div>

            <div className="flex flex-col gap-2">
              <label htmlFor="type" className="text-sm font-medium">
                Type
              </label>
              <select
                id="type"
                value={type}
                onChange={(e) => setType(e.target.value as MemoryType)}
                className="h-9 rounded-md border border-input bg-transparent px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              >
                {TYPES.map((t) => (
                  <option key={t} value={t}>
                    {t}
                  </option>
                ))}
              </select>
            </div>

            <div className="flex flex-col gap-2">
              <label htmlFor="tags" className="text-sm font-medium">
                Tags{" "}
                <span className="font-normal text-muted-foreground">
                  (comma separated)
                </span>
              </label>
              <Input
                id="tags"
                value={tags}
                onChange={(e) => setTags(e.target.value)}
                placeholder="postgresql, setup"
              />
            </div>
          </div>

          {error && (
            <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-3 text-sm text-destructive">
              {error}
            </div>
          )}

          <div className="flex items-center gap-2 pt-2">
            <Button type="submit" disabled={Boolean(isSubmitting)}>
              {isPending && <Loader2 className="size-4 animate-spin" />}
              Save Memory
            </Button>
            <Button
              type="button"
              variant="ghost"
              onClick={() => router.push("/dashboard/memories/")}
            >
              Cancel
            </Button>
          </div>
        </form>
      </div>
    </>
  )
}
