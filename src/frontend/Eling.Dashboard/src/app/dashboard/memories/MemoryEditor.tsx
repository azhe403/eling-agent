"use client"

import { useRef, useState } from "react"

import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { X } from "lucide-react"
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"
import { useAutoHeight } from "@/hooks/use-auto-height"
import { utcToLocal } from "@/lib/date-utils"
import { EDIT_TYPES, STATUSES } from "@/lib/types"
import type { Memory } from "@/lib/types"

export type MemoryEditorProps = {
  memory: Memory
  onCancel: () => void
  onSave: (body: { content: string; type: string; status: string; tags: string[] }) => void
}

// Extracted from lines 735-812 of the original page. Uses the shared
// `useAutoHeight` hook so the textarea smoothly auto-grows as the user edits,
// dropping the previous manual DOM height calculation.
export function MemoryEditor({ memory, onCancel, onSave }: MemoryEditorProps) {
  const [content, setContent] = useState(memory.content)
  const [type, setType] = useState(memory.type)
  const [status, setStatus] = useState(memory.status)
  const [tags, setTags] = useState<string[]>(memory.tags ?? [])
  const [tagInput, setTagInput] = useState("")
  const textareaRef = useRef<HTMLTextAreaElement | null>(null)

  useAutoHeight(textareaRef, [content])

  const addTag = () => {
    const t = tagInput.trim().toLowerCase()
    if (!t) return
    if (tags.includes(t)) {
      setTagInput("")
      return
    }
    setTags((prev) => [...prev, t])
    setTagInput("")
  }

  const removeTag = (t: string) => {
    setTags((prev) => prev.filter((x) => x !== t))
  }

  return (
    <div className="flex flex-col gap-3 rounded-xl border border-primary/40 bg-card p-4 shadow-sm">
      <div className="flex items-center justify-between text-xs text-muted-foreground border-b border-border/40 pb-2">
        <span className="font-medium text-foreground">
          Editing Memory · ID: {memory.id}
        </span>
        <span>Created: {utcToLocal(memory.createdAt)}</span>
      </div>
      <textarea
        ref={textareaRef}
        value={content}
        onChange={(e) => setContent(e.target.value)}
        placeholder="Enter memory content..."
        className="w-full rounded-lg border border-input bg-transparent px-3 py-2 text-sm font-sans leading-relaxed focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring resize-y min-h-[80px] overflow-hidden"
      />
      <div className="flex flex-col gap-2">
        <div className="flex flex-wrap items-center gap-2">
          <Select value={type} onValueChange={(v) => v && setType(v)}>
            <SelectTrigger className="h-9 w-[140px]">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {EDIT_TYPES.map((t) => (
                <SelectItem key={t} value={t}>
                  {t}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <Select value={status} onValueChange={(v) => v && setStatus(v)}>
            <SelectTrigger className="h-9 w-[140px]">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {STATUSES.map((s) => (
                <SelectItem key={s} value={s}>
                  {s}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        <div className="flex flex-col gap-1.5">
          <div className="flex items-center gap-2">
            <Input
              value={tagInput}
              onChange={(e) => setTagInput(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") {
                  e.preventDefault()
                  addTag()
                }
              }}
              placeholder="Add tag..."
              className="h-9 flex-1"
            />
            <Button size="sm" variant="outline" onClick={addTag} disabled={!tagInput.trim()}>
              Add
            </Button>
          </div>
          {tags.length > 0 && (
            <div className="flex flex-wrap items-center gap-1.5">
              {tags.map((t) => (
                <span
                  key={t}
                  className="inline-flex items-center gap-1 rounded-md bg-secondary px-2 py-0.5 text-xs text-secondary-foreground"
                >
                  #{t}
                  <button
                    type="button"
                    onClick={() => removeTag(t)}
                    className="text-muted-foreground hover:text-foreground"
                    aria-label={`Remove tag ${t}`}
                  >
                    <X className="size-3" />
                  </button>
                </span>
              ))}
            </div>
          )}
        </div>
      </div>
      <div className="flex justify-end gap-2">
        <Button
          size="sm"
          onClick={() => onSave({ content, type, status, tags })}
          disabled={!content.trim()}
        >
          Save
        </Button>
        <Button size="sm" variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
      </div>
    </div>
  )
}