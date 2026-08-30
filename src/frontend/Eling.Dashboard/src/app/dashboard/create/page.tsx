"use client"

import { useCallback, useEffect, useRef, useState } from "react"
import { useRouter } from "next/navigation"
import { Loader2 } from "lucide-react"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Separator } from "@/components/ui/separator"
import { SidebarTrigger } from "@/components/ui/sidebar"

const TYPES = ["Fact", "Preference", "Decision", "Lesson", "Note"]

type Runtime = { projectRoot: string; dataDirectory: string }

export default function CreateMemoryPage() {
  const router = useRouter()
  const [content, setContent] = useState("")
  const [type, setType] = useState("Note")
  const [tags, setTags] = useState("")
  const [scope, setScope] = useState("project")
  const [runtimes, setRuntimes] = useState<Runtime[]>([])
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const textareaRef = useRef<HTMLTextAreaElement>(null)

  const adjustHeight = useCallback(() => {
    if (textareaRef.current) {
      textareaRef.current.style.height = "auto"
      // N+1 line buffer (24px) for elegant breathing room below the last line
      const nextHeight = Math.max(80, textareaRef.current.scrollHeight + 24)
      textareaRef.current.style.height = `${nextHeight}px`
    }
  }, [])

  useEffect(() => {
    adjustHeight()
  }, [adjustHeight])

  useEffect(() => {
    async function loadRuntimes() {
      try {
        const res = await fetch(`/api/coordinator/runtimes?_t=${Date.now()}`, {
          cache: "no-store",
          headers: { "Cache-Control": "no-cache" },
        })
        if (res.ok) {
          const data: Runtime[] = await res.json()
          setRuntimes(data)
          // Read initial scope via ref-like read to avoid effect dependency loop
          if (data.length > 0) {
            setScope(prev =>
              prev === "project" ? data[0].projectRoot : prev
            )
          }
        }
      } catch {
        // ignore
      }
    }
    loadRuntimes()
  }, [])

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    if (!content.trim()) {
      setError("Content is required.")
      return
    }

    setSaving(true)
    setError(null)
    try {
      const payload = {
        content: content.trim(),
        type,
        tags: tags
          .split(",")
          .map((t) => t.trim())
          .filter(Boolean),
      }

      let url = "/api/memories"
      if (scope === "global") {
        url = "/api/global/memories"
      } else if (scope !== "project") {
        url = `/api/project/memories?projectRoot=${encodeURIComponent(scope)}`
      }

      const res = await fetch(url, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      })
      if (!res.ok) throw new Error(`API returned ${res.status}`)
      router.push("/dashboard/memories/")
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to save memory")
      setSaving(false)
    }
  }

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
              onChange={(e) => {
                setContent(e.target.value)
                adjustHeight()
              }}
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
                value={scope}
                onChange={(e) => setScope(e.target.value)}
                className="h-9 rounded-md border border-input bg-transparent px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              >
                {runtimes.map((r) => (
                  <option key={r.projectRoot} value={r.projectRoot}>
                    📁 {r.projectRoot.split("\\").pop() ?? r.projectRoot.split("/").pop()} (Project)
                  </option>
                ))}
                {runtimes.length === 0 && (
                  <option value="project">📁 Current Project</option>
                )}
                <option value="global">🌐 Global Scope</option>
              </select>
            </div>

            <div className="flex flex-col gap-2">
              <label htmlFor="type" className="text-sm font-medium">
                Type
              </label>
              <select
                id="type"
                value={type}
                onChange={(e) => setType(e.target.value)}
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
            <Button type="submit" disabled={saving || !content.trim()}>
              {saving && <Loader2 className="size-4 animate-spin" />}
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
