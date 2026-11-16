"use client"

import { Trash2 } from "lucide-react"

import { utcToLocal } from "@/lib/date-utils"
import type { SkillListItem } from "@/lib/types"
import { cn } from "@/lib/utils"

export type SkillCardProps = {
  skill: SkillListItem
  selected: boolean
  trashMode: boolean
  onSelect: (skill: SkillListItem) => void
}

export function originBadgeClass(origin: string): string {
  return origin === "external"
    ? "bg-amber-500/10 text-amber-600 dark:text-amber-400"
    : "bg-blue-500/10 text-blue-600 dark:text-blue-400"
}

export function SkillCard({ skill, selected, trashMode, onSelect }: SkillCardProps) {
  return (
    <button
      type="button"
      onClick={() => onSelect(skill)}
      aria-pressed={selected}
      aria-label={`View skill ${skill.name}`}
      className={cn(
        "group flex flex-col gap-2.5 rounded-xl border bg-card p-4 text-left transition-colors",
        "hover:border-foreground/25 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
        selected && "border-foreground/30 ring-1 ring-foreground/30",
      )}
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex flex-wrap items-center gap-1.5 text-xs">
          <span
            className={`rounded-md px-2 py-0.5 font-medium ${originBadgeClass(skill.origin)}`}
          >
            {skill.origin}
          </span>
          {trashMode && (
            <span
              className="inline-flex items-center gap-1 rounded-md bg-muted px-2 py-0.5 font-medium text-muted-foreground"
              title="Trash view — the list includes trashed skills"
            >
              <Trash2 className="size-3" />
              trash view
            </span>
          )}
        </div>
        <span className="text-[11px] text-muted-foreground">
          Updated: {utcToLocal(skill.updatedAt)}
        </span>
      </div>

      <div className="text-sm font-semibold leading-snug">{skill.name}</div>
      <p className="w-full text-sm leading-relaxed text-muted-foreground line-clamp-2">
        {skill.description}
      </p>

      {skill.tags.length > 0 && (
        <div className="flex flex-wrap items-center gap-1.5 text-xs">
          {skill.tags.map((tag) => (
            <span
              key={tag}
              className="rounded-md bg-secondary px-2 py-0.5 text-secondary-foreground"
            >
              #{tag}
            </span>
          ))}
        </div>
      )}
    </button>
  )
}
