"use client"

import { useRef, useState } from "react"

import { Button } from "@/components/ui/button"
import { useAutoHeight } from "@/hooks/use-auto-height"
import { formatDate } from "@/lib/date-utils"
import { EDIT_TYPES, STATUSES } from "@/lib/types"
import type { Memory } from "@/lib/types"

export type MemoryEditorProps = {
  memory: Memory
  onCancel: () => void
  onSave: (body: { content: string; type: string; status: string }) => void
}

// Extracted from lines 735-812 of the original page. Uses the shared
// `useAutoHeight` hook so the textarea smoothly auto-grows as the user edits,
// dropping the previous manual DOM height calculation.
export function MemoryEditor({ memory, onCancel, onSave }: MemoryEditorProps) {
  const [content, setContent] = useState(memory.content)
  const [type, setType] = useState(memory.type)
  const [status, setStatus] = useState(memory.status)
  const textareaRef = useRef<HTMLTextAreaElement | null>(null)

  useAutoHeight(textareaRef, [content])

  return (
    <div className="flex flex-col gap-3 rounded-xl border border-primary/40 bg-card p-4 shadow-sm">
      <div className="flex items-center justify-between text-xs text-muted-foreground border-b border-border/40 pb-2">
        <span className="font-medium text-foreground">
          Editing Memory · ID: {memory.id}
        </span>
        <span>Created: {formatDate(memory.createdAt)}</span>
      </div>
      <textarea
        ref={textareaRef}
        value={content}
        onChange={(e) => setContent(e.target.value)}
        placeholder="Enter memory content..."
        className="w-full rounded-lg border border-input bg-transparent px-3 py-2 text-sm font-sans leading-relaxed focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring resize-y min-h-[80px] overflow-hidden"
      />
      <div className="flex flex-wrap items-center gap-2">
        <select
          value={type}
          onChange={(e) => setType(e.target.value)}
          className="h-9 rounded-md border border-input bg-transparent px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          {EDIT_TYPES.map((t) => (
            <option key={t} value={t}>
              {t}
            </option>
          ))}
        </select>
        <select
          value={status}
          onChange={(e) => setStatus(e.target.value)}
          className="h-9 rounded-md border border-input bg-transparent px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          {STATUSES.map((s) => (
            <option key={s} value={s}>
              {s}
            </option>
          ))}
        </select>
        <div className="ml-auto flex gap-2">
          <Button
            size="sm"
            onClick={() => onSave({ content, type, status })}
            disabled={!content.trim()}
          >
            Save
          </Button>
          <Button size="sm" variant="ghost" onClick={onCancel}>
            Cancel
          </Button>
        </div>
      </div>
    </div>
  )
}