"use client"

import { useCallback, useEffect, useMemo, useState } from "react"
import {
  Loader2,
  PackageCheck,
  Plus,
  RefreshCcw,
  RefreshCw,
  SearchX,
  Trash2,
} from "lucide-react"

import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog"
import { DashboardHeader } from "@/components/dashboard-header"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { LoadingBar } from "@/components/ui/loading-bar"
import { Switch } from "@/components/ui/switch"
import { useMediaQuery } from "@/hooks/use-media-query"
import { XL_BREAKPOINT } from "@/lib/constants"
import { apiFetch, ApiError } from "@/lib/api"
import type {
  Skill,
  SkillFormData,
  SkillListItem,
  SkillsListResponse,
} from "@/lib/types"

import { SkillCard } from "./SkillCard"
import { SkillDetailPanel } from "./SkillDetailPanel"

const ORIGINS = ["all", "eling", "external"] as const
type OriginFilter = (typeof ORIGINS)[number]

export function SkillsList() {
  const wide = useMediaQuery(`(min-width: ${XL_BREAKPOINT}px)`)

  const [skills, setSkills] = useState<SkillListItem[]>([])
  // `total` counts the whole catalog (incl. trashed); `matching` is what
  // survives the q/tag/origin filters — both reported by the backend.
  const [total, setTotal] = useState(0)
  const [matching, setMatching] = useState(0)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  // Short-lived info line (rescan report, restore confirmation); auto-dismisses.
  const [notice, setNotice] = useState<string | null>(null)
  const [rescanning, setRescanning] = useState(false)

  const [query, setQuery] = useState("")
  const [debouncedQuery, setDebouncedQuery] = useState("")
  const [origin, setOrigin] = useState<OriginFilter>("all")
  const [tag, setTag] = useState<string | null>(null)
  const [trashMode, setTrashMode] = useState(false)

  const [selected, setSelected] = useState<SkillListItem | null>(null)
  // Detail is keyed by skill id so a stale entry from a previous selection
  // can never be mistaken for the current one — no effect reset needed.
  const [detailState, setDetailState] = useState<{
    id: string
    skill: Skill | null
    error: string | null
  } | null>(null)
  const [detailLoading, setDetailLoading] = useState(false)
  const [createMode, setCreateMode] = useState(false)
  const [deleteTarget, setDeleteTarget] = useState<SkillListItem | null>(null)
  const deleteIsExternal = deleteTarget?.origin === "external"
  const [adoptTarget, setAdoptTarget] = useState<SkillListItem | null>(null)

  // Error banner auto-dismisses so a stale failure never sticks around.
  useEffect(() => {
    if (error === null) return
    const t = setTimeout(() => setError(null), 5000)
    return () => clearTimeout(t)
  }, [error])
  useEffect(() => {
    if (notice === null) return
    const t = setTimeout(() => setNotice(null), 5000)
    return () => clearTimeout(t)
  }, [notice])

  // The search box types faster than a request should go out: 400ms of quiet
  // before the query reaches the backend, which owns filtering and counting.
  useEffect(() => {
    const timer = setTimeout(() => setDebouncedQuery(query), 400)
    return () => clearTimeout(timer)
  }, [query])

  const load = useCallback(async () => {
    try {
      const params = new URLSearchParams()
      params.set("pageSize", "100")
      params.set("sort", "name")
      if (debouncedQuery.trim()) params.set("q", debouncedQuery.trim())
      if (tag) params.set("tag", tag)
      if (origin !== "all") params.set("origin", origin)
      if (trashMode) params.set("includeTrashed", "true")
      const data = await apiFetch<SkillsListResponse>(`/api/skills?${params}`, {
        cache: "no-store",
        headers: { "Cache-Control": "no-cache", Pragma: "no-cache" },
      })
      const items = Array.isArray(data?.items) ? data.items : []
      const meta = data?.metadata
      setSkills(items)
      setTotal(typeof meta?.total === "number" ? meta.total : items.length)
      setMatching(typeof meta?.matching === "number" ? meta.matching : items.length)
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to load skills")
    } finally {
      setLoading(false)
    }
  }, [debouncedQuery, origin, tag, trashMode])

  // Every revalidation goes through one local async runner (the Memories page
  // pattern): the effect body itself touches no setState synchronously.
  useEffect(() => {
    let isMounted = true
    const fetchAll = async () => {
      if (isMounted) await load()
    }
    void fetchAll()
    return () => {
      isMounted = false
    }
  }, [load])

  // Detail fetch: the list is compact (no body, no related refs), so the panel
  // always reads the full record. A 404 here means the skill is in the trash —
  // the panel falls back to the compact shape with a restore action.
  const selectedId = selected?.id ?? null
  useEffect(() => {
    if (!selectedId) return
    let cancelled = false
    const run = async () => {
      setDetailLoading(true)
      try {
        const skill = await apiFetch<Skill>(`/api/skills/${encodeURIComponent(selectedId)}`, {
          cache: "no-store",
          headers: { "Cache-Control": "no-cache", Pragma: "no-cache" },
        })
        if (cancelled) return
        setDetailState({ id: selectedId, skill, error: null })
      } catch (e) {
        if (cancelled) return
        setDetailState({
          id: selectedId,
          skill: null,
          error:
            e instanceof ApiError && e.status === 404
              ? "Not in the active catalog — this skill is in the trash (or gone). Its body is not served; restore it to view."
              : e instanceof Error
                ? e.message
                : "Failed to load the skill",
        })
      } finally {
        if (!cancelled) setDetailLoading(false)
      }
    }
    void run()
    return () => {
      cancelled = true
    }
  }, [selectedId])

  // The selection resolves against the latest list: once a skill vanishes
  // (delete), the panel closes on its own and no effect has to force it.
  const selectedListItem = useMemo(
    () => (selected ? (skills.find((s) => s.id === selected.id) ?? null) : null),
    [skills, selected],
  )
  const detail = selectedId && detailState?.id === selectedId ? detailState.skill : null
  const detailError = selectedId && detailState?.id === selectedId ? detailState.error : null
  const detailReady = selectedId !== null && detailState?.id === selectedId
  const shownDetailLoading = selectedId !== null && (detailLoading || !detailReady)

  const availableTags = useMemo(() => {
    const set = new Set<string>()
    for (const skill of skills) for (const t of skill.tags) set.add(t)
    return [...set].sort()
  }, [skills])

  const handleSelect = (card: SkillListItem) => {
    setCreateMode(false)
    setSelected((current) => (current?.id === card.id ? null : card))
  }

  const handleNewSkill = () => {
    setSelected(null)
    setCreateMode(true)
  }

  const handleSaveEdit = useCallback(
    async (id: string, data: SkillFormData) => {
      const updated = await apiFetch<Skill>(`/api/skills/${encodeURIComponent(id)}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          name: data.name,
          description: data.description,
          body: data.body,
          tags: data.tags,
        }),
      })
      setDetailState({ id: updated.id, skill: updated, error: null })
      await load()
    },
    [load],
  )

  const handleCreate = useCallback(
    async (data: SkillFormData) => {
      const created = await apiFetch<Skill>("/api/skills", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          name: data.name,
          description: data.description,
          body: data.body,
          tags: data.tags,
        }),
      })
      setCreateMode(false)
      setSelected({
        id: created.id,
        name: created.name,
        description: created.description,
        path: created.path,
        origin: created.origin,
        tags: created.tags,
        updatedAt: created.updatedAt,
      })
      setDetailState({ id: created.id, skill: created, error: null })
      await load()
    },
    [load],
  )

  const handleDelete = useCallback(async () => {
    if (!deleteTarget) return
    const target = deleteTarget
    setDeleteTarget(null)
    try {
      await apiFetch<{ trashed?: boolean }>(
        `/api/skills/${encodeURIComponent(target.id)}`,
        { method: "DELETE" },
      )
      setSelected((current) => (current?.id === target.id ? null : current))
      setNotice(`"${target.name}" moved to the trash.`)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : "Delete failed")
    }
  }, [deleteTarget, load])

  const handleRestore = useCallback(
    async (id: string) => {
      try {
        await apiFetch<Skill>(`/api/skills/${encodeURIComponent(id)}/restore`, {
          method: "POST",
        })
        setSelected(null)
        setNotice("Skill restored to the active catalog.")
        await load()
      } catch (e) {
        setError(e instanceof Error ? e.message : "Restore failed")
      }
    },
    [load],
  )

  const handleAdopt = useCallback(async () => {
    if (!adoptTarget) return
    const target = adoptTarget
    setAdoptTarget(null)
    try {
      // The response is the adopted skill (origin flipped to eling); adopting
      // is permanent, so the panel immediately re-renders as Eling-managed.
      const adopted = await apiFetch<Skill>(
        `/api/skills/${encodeURIComponent(target.id)}/adopt`,
        { method: "POST" },
      )
      setDetailState({ id: adopted.id, skill: adopted, error: null })
      setNotice(`"${target.name}" is now Eling-managed.`)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : "Adopt failed")
    }
  }, [adoptTarget, load])

  const handleRescan = useCallback(async () => {
    setRescanning(true)
    setError(null)
    try {
      const report = await apiFetch<{
        discovered?: number
        external?: number
        missing?: number
        renamed?: number
        failed?: number
      }>("/api/skills/rescan", { method: "POST" })
      setNotice(
        `Rescan complete — discovered ${report?.discovered ?? 0}, external ${report?.external ?? 0}, missing ${report?.missing ?? 0}, renamed ${report?.renamed ?? 0}, failed ${report?.failed ?? 0}.`,
      )
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : "Rescan failed")
    } finally {
      setRescanning(false)
    }
  }, [load])

  const panel = (variant: "aside" | "stacked") => (
    <SkillDetailPanel
      key={createMode ? "create" : (selectedListItem?.id ?? "none")}
      variant={variant}
      summary={selectedListItem}
      detail={detail}
      detailLoading={shownDetailLoading}
      detailError={detailError}
      createMode={createMode}
      trashMode={trashMode}
      onClose={() => {
        setSelected(null)
        setCreateMode(false)
      }}
      onSaveEdit={handleSaveEdit}
      onCreate={handleCreate}
      onDelete={(id) => {
        const target = skills.find((s) => s.id === id) ?? null
        setDeleteTarget(target)
      }}
      onAdopt={(id) => {
        const target = skills.find((s) => s.id === id) ?? null
        setAdoptTarget(target)
      }}
      onRestore={(id) => void handleRestore(id)}
    />
  )

  return (
    <div className="flex w-full flex-col">
      <DashboardHeader label="Skills">
        <span className="hidden sm:inline-block text-xs text-muted-foreground font-medium">
          {matching} of {total}
        </span>

        <Button
          variant="outline"
          size="sm"
          onClick={() => void handleRescan()}
          disabled={rescanning}
          title="Rescan the catalog folders and refresh the index"
        >
          {rescanning ? (
            <Loader2 className="size-4 animate-spin" />
          ) : (
            <RefreshCcw className="size-4" />
          )}
          <span className="hidden sm:inline-block">Rescan catalog</span>
        </Button>

        <Button
          variant="outline"
          size="sm"
          onClick={() => {
            setLoading(true)
            void load()
          }}
          disabled={loading}
        >
          <RefreshCw className={loading ? "size-4 animate-spin" : "size-4"} />
          <span className="hidden sm:inline-block">Refresh</span>
        </Button>

        <Button size="sm" onClick={handleNewSkill}>
          <Plus className="size-4" />
          <span>New Skill</span>
        </Button>
      </DashboardHeader>

      <div className="flex min-h-0 w-full flex-1">
        <div className="flex min-w-0 flex-1 flex-col gap-4 p-4 pt-4">
          {error && (
            <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-4 text-sm text-destructive">
              {error}
            </div>
          )}
          {notice && (
            <div className="rounded-lg border border-primary/30 bg-primary/5 p-4 text-sm text-foreground">
              {notice}
            </div>
          )}

          {/* Toolbar: search + origin filter + trash toggle */}
          <div className="sticky top-16 z-10 flex flex-col gap-2 bg-background pb-2 pt-1">
            <div className="flex items-center gap-2">
              <Input
                placeholder="Search name, description or body..."
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                className="min-w-0 flex-1"
                aria-label="Search skills"
              />
              <div
                className="flex shrink-0 items-center gap-1 rounded-lg border bg-muted/40 p-1"
                role="group"
                aria-label="Origin filter"
              >
                {ORIGINS.map((o) => (
                  <button
                    key={o}
                    onClick={() => {
                      setLoading(true)
                      setOrigin(o)
                    }}
                    aria-pressed={origin === o}
                    className={
                      (origin === o
                        ? "rounded-md bg-background px-2.5 py-1 text-xs font-medium text-foreground shadow-sm"
                        : "rounded-md px-2.5 py-1 text-xs font-medium text-muted-foreground hover:text-foreground") +
                      " whitespace-nowrap capitalize"
                    }
                  >
                    {o === "all" ? "All" : o === "eling" ? "Eling" : "External"}
                  </button>
                ))}
              </div>
              <label className="flex shrink-0 cursor-pointer items-center gap-1.5 text-xs text-muted-foreground">
                <Switch
                  size="sm"
                  checked={trashMode}
                  onCheckedChange={(checked) => {
                    setLoading(true)
                    setTrashMode(checked)
                  }}
                  aria-label="Show trashed skills"
                />
                <span className="hidden sm:inline">Show trashed</span>
              </label>
            </div>

            {availableTags.length > 0 && (
              <div className="flex flex-wrap items-center gap-1.5">
                {availableTags.map((t) => (
                  <button
                    key={t}
                    onClick={() => {
                      setLoading(true)
                      setTag((current) => (current === t ? null : t))
                    }}
                    aria-pressed={tag === t}
                    className={
                      tag === t
                        ? "rounded-md bg-primary px-2 py-0.5 text-xs font-medium text-primary-foreground"
                        : "rounded-md bg-secondary px-2 py-0.5 text-xs text-secondary-foreground hover:bg-secondary/80"
                    }
                  >
                    #{t}
                  </button>
                ))}
                {tag && (
                  <button
                    onClick={() => {
                      setLoading(true)
                      setTag(null)
                    }}
                    className="text-[11px] text-muted-foreground underline-offset-2 hover:text-foreground hover:underline"
                  >
                    clear tag
                  </button>
                )}
              </div>
            )}
          </div>

          <div className="relative min-w-0">
            <LoadingBar loading={loading} />
            {skills.length === 0 && !loading ? (
              <div className="flex min-h-[200px] flex-col items-center justify-center gap-2 rounded-xl border border-dashed text-sm text-muted-foreground">
                <SearchX className="size-6 opacity-60" />
                {total > 0
                  ? "No skills match this filter."
                  : trashMode
                    ? "No skills found (trash included)."
                    : "No skills found."}
              </div>
            ) : skills.length > 0 ? (
              <div className="grid grid-cols-1 gap-2.5 lg:grid-cols-2">
                {skills.map((skill) => (
                  <SkillCard
                    key={skill.id}
                    skill={skill}
                    selected={selected?.id === skill.id}
                    trashMode={trashMode}
                    onSelect={handleSelect}
                    onDelete={(s) => setDeleteTarget(s)}
                    onAdopt={(s) => setAdoptTarget(s)}
                  />
                ))}
              </div>
            ) : null}
          </div>

          {/* Narrow screens: the panel stacks under the grid. */}
          {!wide && (selectedListItem || createMode) && panel("stacked")}
        </div>

        {/* Wide screens: sticky master-detail panel on the right. */}
        {wide && (selectedListItem || createMode) && panel("aside")}
      </div>

      <AlertDialog open={deleteTarget !== null} onOpenChange={(open) => !open && setDeleteTarget(null)}>
        <AlertDialogContent className="w-[94vw] sm:max-w-lg">
          <AlertDialogHeader>
            <AlertDialogTitle className="text-destructive">
              {deleteIsExternal ? "Delete External Skill?" : "Move Skill to Trash?"}
            </AlertDialogTitle>
            <AlertDialogDescription>
              {deleteIsExternal
                ? `This folder is managed by external tooling and is not created by Eling. It will be moved to the trash folder (~/.agents/.trash/skills/${deleteTarget?.name}/) and can be restored.`
                : "This moves the skill folder to the catalog trash. You can restore it from trash view at any time."}
            </AlertDialogDescription>
          </AlertDialogHeader>
          {deleteTarget && (
            <div className="rounded-lg border border-destructive/20 bg-destructive/5 p-3 text-xs text-muted-foreground">
              <span className="font-medium text-foreground">{deleteTarget.name}</span>
              <span className="mx-2">·</span>
              {deleteIsExternal ? (
                <span className="break-all font-mono">{deleteTarget.path}</span>
              ) : (
                <span className="font-mono">{deleteTarget.id}</span>
              )}
            </div>
          )}
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction
              className="bg-destructive text-destructive-foreground hover:bg-destructive/90"
              onClick={() => void handleDelete()}
            >
              <Trash2 className="size-4" />
              Move to Trash
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

      <AlertDialog open={adoptTarget !== null} onOpenChange={(open) => !open && setAdoptTarget(null)}>
        <AlertDialogContent className="w-[94vw] sm:max-w-lg">
          <AlertDialogHeader>
            <AlertDialogTitle>Adopt {adoptTarget?.name}?</AlertDialogTitle>
            <AlertDialogDescription>
              Ownership moves to Eling in place — the folder and SKILL.md stay exactly where
              they are; only the frontmatter gains Eling keys (id, createdAt, updatedAt) and
              the index row flips to Eling-managed. This is permanent and cannot be undone
              from the UI. If the frontmatter cannot be parsed, the adoption is refused and
              nothing is changed.
            </AlertDialogDescription>
          </AlertDialogHeader>
          {adoptTarget && (
            <div className="rounded-lg border border-primary/20 bg-primary/5 p-3 text-xs text-muted-foreground">
              <span className="font-medium text-foreground">{adoptTarget.name}</span>
              <span className="mx-2">·</span>
              <span className="break-all font-mono">{adoptTarget.path}</span>
            </div>
          )}
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={() => void handleAdopt()}>
              <PackageCheck className="size-4" />
              Adopt
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  )
}
