"use client"

import { useEffect, useMemo, useState } from "react"
import {
  Check,
  Copy,
  ExternalLink,
  Loader2,
  PackageCheck,
  Pencil,
  RotateCcw,
  Trash2,
  X,
} from "lucide-react"

import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Skeleton } from "@/components/ui/skeleton"
import { ApiError } from "@/lib/api"
import { utcToLocal } from "@/lib/date-utils"
import type { Skill, SkillFormData, SkillListItem } from "@/lib/types"
import { cn } from "@/lib/utils"
import { originBadgeClass } from "./SkillCard"

/** ~40 lines / ~4000 characters, whichever the body hits first. */
const EXCERPT_LIMIT = 4000

export type SkillDetailPanelProps = {
  /** "aside" docks right of the grid (wide screens); "stacked" renders under it. */
  variant?: "aside" | "stacked"
  /** Compact list-item shape; always present for a selected skill. */
  summary: SkillListItem | null
  /** Full detail once GET /api/skills/{id} lands. */
  detail: Skill | null
  detailLoading: boolean
  /** Non-null when the detail fetch failed (404 = trashed skill). */
  detailError: string | null
  /** Create mode: empty form, POSTs a new eling skill on save. */
  createMode: boolean
  /** Trash view: skills offer Restore instead of Edit/Delete. */
  trashMode: boolean
  onClose: () => void
  onSaveEdit: (id: string, data: SkillFormData) => Promise<void>
  onCreate: (data: SkillFormData) => Promise<void>
  onDelete: (id: string) => void
  onRestore: (id: string) => void
  onAdopt: (id: string) => void
}

function parseTags(input: string): string[] {
  return input
    .split(",")
    .map((t) => t.trim().toLowerCase())
    .filter((t) => t.length > 0)
}

export function SkillDetailPanel({
  variant = "aside",
  summary,
  detail,
  detailLoading,
  detailError,
  createMode,
  trashMode,
  onClose,
  onSaveEdit,
  onCreate,
  onDelete,
  onRestore,
  onAdopt,
}: SkillDetailPanelProps) {
  // The parent keys this panel on (createMode | skill id), so entering
  // create mode or switching skills remounts with fresh form state — no
  // reset effect required.
  const [mode, setMode] = useState<"view" | "edit">(createMode ? "edit" : "view")
  const [name, setName] = useState(createMode ? "" : (detail?.name ?? ""))
  const [description, setDescription] = useState(createMode ? "" : (detail?.description ?? ""))
  const [tagsInput, setTagsInput] = useState(createMode ? "" : (detail?.tags.join(", ") ?? ""))
  const [body, setBody] = useState(createMode ? "" : (detail?.body ?? ""))
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [copiedField, setCopiedField] = useState<string | null>(null)

  const skill: Skill | null = detail
  const isExternal = (skill?.origin ?? summary?.origin) === "external"
  const canEdit = !createMode && !trashMode && !isExternal && skill !== null
  // Delete is the one mutation open to external skills: the folder moves to
  // trash untouched and can be restored — never edited.
  const canDelete = !createMode && !trashMode && (skill !== null || summary !== null)
  // Adopt is the one-way ownership flip open to external skills: permanent,
  // in place, content preserved. Only offered for external skills in view mode.
  const canAdopt = !createMode && !trashMode && isExternal && (skill !== null || summary !== null)
  const canRestore = !createMode && trashMode

  // Escape closes the panel in view mode only — in (create|edit) forms the
  // user's typed work must not vanish on an accidental Escape.
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape" && mode !== "edit") onClose()
    }
    window.addEventListener("keydown", onKeyDown)
    return () => window.removeEventListener("keydown", onKeyDown)
  }, [onClose, mode])

  const excerpt = useMemo(() => {
    const text = skill?.body ?? ""
    if (text.length <= EXCERPT_LIMIT) return { text, truncated: false }
    return { text: text.slice(0, EXCERPT_LIMIT), truncated: true }
  }, [skill?.body])

  const copy = (text: string, field: string) => {
    navigator.clipboard?.writeText(text).catch(() => {
      // Clipboard can be blocked; the copy is a convenience, never an error.
    })
    setCopiedField(field)
    setTimeout(() => setCopiedField(null), 1500)
  }

  const enterEdit = () => {
    setName(skill?.name ?? "")
    setDescription(skill?.description ?? "")
    setTagsInput(skill?.tags.join(", ") ?? "")
    setBody(skill?.body ?? "")
    setSaveError(null)
    setMode("edit")
  }

  const submitForm = async () => {
    const trimmedName = name.trim()
    const trimmedDescription = description.trim()
    if (!trimmedName || !trimmedDescription || !body.trim()) {
      setSaveError("Name, description and body are all required.")
      return
    }
    const payload: SkillFormData = {
      name: trimmedName,
      description: trimmedDescription,
      tags: parseTags(tagsInput),
      body,
    }
    setSaving(true)
    setSaveError(null)
    try {
      if (createMode) {
        await onCreate(payload)
      } else if (skill) {
        await onSaveEdit(skill.id, payload)
        setMode("view")
      }
    } catch (e) {
      setSaveError(
        e instanceof ApiError
          ? e.message
          : e instanceof Error
            ? e.message
            : "Save failed",
      )
    } finally {
      setSaving(false)
    }
  }

  const displayName = skill?.name ?? summary?.name ?? ""
  const displayOrigin = skill?.origin ?? summary?.origin ?? "eling"
  const displayPath = skill?.path ?? summary?.path ?? ""
  const displayUpdated = skill?.updatedAt ?? summary?.updatedAt ?? ""
  const displayTags = skill?.tags ?? summary?.tags ?? []

  const relatedMemories = skill?.relatedMemories ?? []
  const relatedSnippets = skill?.relatedSnippets ?? []

  const containerClasses =
    variant === "aside"
      ? "sticky top-16 hidden max-h-[calc(100dvh-4rem)] w-[44%] shrink-0 flex-col self-start overflow-y-auto border-l bg-background xl:flex"
      : "flex w-full flex-col rounded-xl border bg-background xl:hidden"

  return (
    <aside aria-label="Skill detail" className={cn("flex-col", containerClasses)}>
      <div className="flex min-h-0 flex-col gap-3 p-4">
        {/* Header: name + origin badge + close */}
        <div className="flex items-start justify-between gap-2 border-b border-border/40 pb-3">
          <div className="min-w-0">
            <div className="flex flex-wrap items-center gap-2">
              <h2 className="break-all text-base font-semibold leading-tight">
                {createMode ? "New Skill" : displayName}
              </h2>
              {!createMode && (
                <span
                  className={`rounded-md px-2 py-0.5 text-xs font-medium ${originBadgeClass(displayOrigin)}`}
                >
                  {displayOrigin}
                </span>
              )}
            </div>
            {!createMode && displayPath && (
              <div
                className="mt-1.5 truncate font-mono text-[11px] text-muted-foreground"
                title={displayPath}
              >
                {displayPath}
              </div>
            )}
          </div>
          <Button
            variant="ghost"
            size="icon"
            className="size-7 shrink-0 text-muted-foreground hover:text-foreground"
            onClick={onClose}
            aria-label="Close panel"
          >
            <X className="size-4" />
          </Button>
        </div>

        {/* Top action bar: delete/edit (view mode) or restore (trash mode). Hidden in edit/create mode — the form has its own Save/Cancel. */}
        {mode !== "edit" && (
          <div className="flex items-center justify-end gap-2 border-b border-border/40 pb-3">
            {canRestore ? (
            <>
              <span className="mr-auto text-[11px] text-muted-foreground">
                Trash view — restore moves the skill back to the catalog.
              </span>
              <Button
                variant="outline"
                size="sm"
                onClick={() => summary && onRestore(summary.id)}
              >
                <RotateCcw className="size-4" />
                Restore
              </Button>
            </>
          ) : (
            <>
              {canAdopt && (
                <Button
                  variant="default"
                  size="sm"
                  onClick={() => summary && onAdopt(summary.id)}
                  aria-label="Adopt skill"
                  title="Adopt this skill into Eling management (permanent, in place)"
                >
                  <PackageCheck className="size-4" />
                  Adopt
                </Button>
              )}
              <Button
                variant="ghost"
                size="icon"
                className="size-8 text-muted-foreground hover:text-destructive"
                onClick={() => summary && onDelete(summary.id)}
                disabled={!canDelete}
                aria-label="Delete skill"
                title={
                  isExternal
                    ? "Move to trash (restorable — the folder is not edited)"
                    : trashMode
                      ? "Unavailable in trash view"
                      : "Move to trash"
                }
              >
                <Trash2 className="size-4" />
              </Button>
              <Button
                variant="outline"
                size="sm"
                onClick={enterEdit}
                disabled={!canEdit}
                title={
                  isExternal
                    ? "External skills are read-only for Eling — adopt them first"
                    : "Edit this skill"
                }
              >
                <Pencil className="size-4" />
                Edit
              </Button>
            </>
          )}
          </div>
        )}

        {mode === "edit" ? (
          <div className="flex flex-col gap-3">
            {saveError && (
              <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-3 text-xs text-destructive">
                {saveError}
              </div>
            )}
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="skill-name">Name</Label>
              <Input
                id="skill-name"
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="kebab-case-name"
                className="font-mono text-sm"
              />
              {!createMode && (
                <p className="text-[11px] text-muted-foreground">
                  Renaming moves the skill folder; the id stays stable.
                </p>
              )}
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="skill-description">Description</Label>
              <Input
                id="skill-description"
                value={description}
                onChange={(e) => setDescription(e.target.value)}
                placeholder="One line on when to use this skill"
              />
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="skill-tags">Tags</Label>
              <Input
                id="skill-tags"
                value={tagsInput}
                onChange={(e) => setTagsInput(e.target.value)}
                placeholder="comma, separated, tags"
              />
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="skill-body">SKILL.md body</Label>
              <textarea
                id="skill-body"
                value={body}
                onChange={(e) => setBody(e.target.value)}
                spellCheck={false}
                className="min-h-48 w-full rounded-md border border-input bg-transparent px-3 py-2 font-mono text-xs leading-relaxed shadow-xs outline-none focus-visible:border-ring focus-visible:ring-2 focus-visible:ring-ring/40"
                placeholder={"# Skill\n\nWhat the agent should do…"}
              />
            </div>
            <div className="flex items-center justify-end gap-2 pt-1">
              <Button
                variant="outline"
                size="sm"
                onClick={() => {
                  if (createMode) {
                    onClose()
                    return
                  }
                  setMode("view")
                  setSaveError(null)
                }}
                disabled={saving}
              >
                Cancel
              </Button>
              <Button size="sm" onClick={() => void submitForm()} disabled={saving}>
                {saving && <Loader2 className="size-4 animate-spin" />}
                {createMode ? "Create Skill" : "Save"}
              </Button>
            </div>
          </div>
        ) : (
          <>
            {/* Meta rows: id + updated */}
            <dl className="grid grid-cols-1 gap-2 text-xs">
              <div className="flex items-center gap-2">
                <dt className="w-16 shrink-0 text-muted-foreground">id</dt>
                <dd className="min-w-0 flex-1 truncate font-mono text-[11px]" title={skill?.id ?? summary?.id ?? ""}>
                  {skill?.id ?? summary?.id ?? "—"}
                </dd>
                {(skill?.id ?? summary?.id) && (
                  <button
                    type="button"
                    onClick={() => copy(skill?.id ?? summary?.id ?? "", "id")}
                    className="shrink-0 text-muted-foreground transition-colors hover:text-foreground"
                    aria-label="Copy skill id"
                    title="Copy skill id"
                  >
                    {copiedField === "id" ? (
                      <Check className="size-3.5 text-green-500" />
                    ) : (
                      <Copy className="size-3.5" />
                    )}
                  </button>
                )}
              </div>
              <div className="flex items-center gap-2">
                <dt className="w-16 shrink-0 text-muted-foreground">updated</dt>
                <dd className="text-[11px]">{utcToLocal(displayUpdated) || "—"}</dd>
              </div>
            </dl>

            {isExternal && (
              <div className="flex items-start gap-2 rounded-lg border border-amber-500/30 bg-amber-500/5 p-3 text-[11px] leading-snug text-amber-700 dark:text-amber-400">
                <ExternalLink className="mt-0.5 size-3.5 shrink-0" />
                <span>
                  External (managed by other tooling) — Eling never edits this
                  folder; Delete moves it to trash (restorable). Adopt takes
                  ownership in place, permanently.
                </span>
              </div>
            )}

            {detailLoading && !skill ? (
              <div className="flex flex-col gap-2">
                <Skeleton className="h-4 w-3/4" />
                <Skeleton className="h-4 w-2/3" />
                <Skeleton className="h-40 w-full rounded-lg" />
              </div>
            ) : detailError && !skill ? (
              <div className="rounded-lg border border-dashed p-3 text-xs text-muted-foreground">
                {detailError}
              </div>
            ) : skill ? (
              <>
                {/* Related references */}
                {relatedMemories.length > 0 && (
                  <div className="flex flex-col gap-1.5">
                    <div className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">
                      Related memories
                    </div>
                    <ul className="flex flex-col gap-1">
                      {relatedMemories.map((ref) => (
                        <li
                          key={`mem-${ref.id}`}
                          className="flex items-center justify-between gap-2 rounded-md bg-muted/40 px-2 py-1 text-[11px]"
                        >
                          <span
                            className="min-w-0 truncate font-mono"
                            title={ref.id}
                          >
                            {ref.id}
                          </span>
                          <span className="shrink-0 text-muted-foreground">
                            {ref.scope ? `${ref.scope} · ` : ""}
                            {ref.relation}
                          </span>
                        </li>
                      ))}
                    </ul>
                  </div>
                )}
                {relatedSnippets.length > 0 && (
                  <div className="flex flex-col gap-1.5">
                    <div className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">
                      Related snippets
                    </div>
                    <ul className="flex flex-col gap-1">
                      {relatedSnippets.map((ref) => (
                        <li
                          key={`snip-${ref.id}`}
                          className="flex items-center justify-between gap-2 rounded-md bg-muted/40 px-2 py-1 text-[11px]"
                        >
                          <span
                            className="min-w-0 truncate font-mono"
                            title={ref.id}
                          >
                            {ref.id}
                          </span>
                          <span className="shrink-0 text-muted-foreground">
                            {ref.relation}
                          </span>
                        </li>
                      ))}
                    </ul>
                  </div>
                )}

                {/* SKILL.md excerpt (preformatted, truncated) */}
                <div className="flex flex-col gap-1.5">
                  <div className="flex items-center justify-between gap-2">
                    <div className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">
                      SKILL.md excerpt
                    </div>
                    {excerpt.truncated && (
                      <span className="text-[10px] text-muted-foreground">
                        truncated — first {EXCERPT_LIMIT} chars
                      </span>
                    )}
                  </div>
                  <pre className="max-h-56 overflow-auto rounded-lg border bg-muted/30 p-3 font-mono text-[11px] leading-relaxed whitespace-pre-wrap break-words">
                    {excerpt.text}
                  </pre>
                </div>

                {/* Full body */}
                <div className="flex flex-col gap-1.5">
                  <div className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">
                    Body
                  </div>
                  <div className="markdown whitespace-pre-wrap break-words text-sm leading-relaxed">
                    {skill.body}
                  </div>
                </div>
              </>
            ) : null}

            {/* Tag chips */}
            {displayTags.length > 0 && (
              <div className="flex flex-wrap items-center gap-1.5 text-xs">
                {displayTags.map((tag) => (
                  <span
                    key={tag}
                    className="rounded-md bg-secondary px-2 py-0.5 text-secondary-foreground"
                  >
                    #{tag}
                  </span>
                ))}
              </div>
            )}

          </>
        )}
      </div>
    </aside>
  )
}
