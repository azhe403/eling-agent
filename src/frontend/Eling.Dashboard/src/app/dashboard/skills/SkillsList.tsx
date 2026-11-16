"use client"

import { useCallback, useEffect, useMemo, useState } from "react"
import {
  ChevronDown,
  ChevronUp,
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
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogTitle,
} from "@/components/ui/dialog"
import { DashboardHeader } from "@/components/dashboard-header"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { LoadingBar } from "@/components/ui/loading-bar"
import { Switch } from "@/components/ui/switch"
import { useMediaQuery } from "@/hooks/use-media-query"
import { usePersistedState, useResumePageParams } from "@/lib/persisted-state"
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

/** One folder the catalog scan could not index (REST rescan payload). */
type SkillRescanFailure = { path: string; reason: string }

export function SkillsList() {
  const wide = useMediaQuery(`(min-width: ${XL_BREAKPOINT}px)`)

  const [skills, setSkills] = useState<SkillListItem[]>([])
  // `total` is the request's selection (includeTrashed honored, origin ignored);
  // `matching` is what survives the q/tag/origin filters — both reported by the backend.
  const [total, setTotal] = useState(0)
  const [matching, setMatching] = useState(0)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  // Short-lived info line (rescan report, restore confirmation); auto-dismisses.
  const [notice, setNotice] = useState<string | null>(null)
  const [rescanning, setRescanning] = useState(false)
  // Failure details from the last rescan, when it reported failures > 0.
  // Stays visible until the next rescan so it can be acted on.
  const [rescanFailures, setRescanFailures] = useState<SkillRescanFailure[] | null>(null)
  const [failuresTruncated, setFailuresTruncated] = useState(false)
  const [failuresExpanded, setFailuresExpanded] = useState(true)

  // Query/scope state is URL+localStorage-backed (reload/back/forward/shared
  // links land on the same view; a bare visit resumes the last position).
  const [query, setQuery] = usePersistedState<string>({
    page: "skills",
    param: "q",
    defaultValue: "",
  })
  // Free-text search: the input owns the draft; only a debounced commit
  // reaches the persisted query (URL + localStorage) — keystrokes stay snappy.
  const [draftQuery, setDraftQuery] = useState(query)
  const [origin, setOrigin] = usePersistedState<OriginFilter>({
    page: "skills",
    param: "origin",
    defaultValue: "all",
    allowed: ORIGINS,
  })
  const [sort, setSort] = usePersistedState<"name" | "updated">({
    page: "skills",
    param: "sort",
    defaultValue: "name",
    allowed: ["name", "updated"],
  })
  const [trashModeParam, setTrashModeParam] = usePersistedState<"0" | "1">({
    page: "skills",
    param: "trash",
    defaultValue: "0",
    allowed: ["0", "1"],
  })
  const trashMode = trashModeParam === "1"
  const setTrashMode = (checked: boolean) => setTrashModeParam(checked ? "1" : "0")
  const [tagParam, setTagParam] = usePersistedState<string>({
    page: "skills",
    param: "tag",
    defaultValue: "",
  })
  const tag = tagParam === "" ? null : tagParam
  const setTag = (t: string | null) => setTagParam(t ?? "")
  // Detail panel: the selected skill name is remembered (URL + localStorage)
  // so revisiting the page reopens it; "" = panel closed.
  const [selectedName, setSelectedName] = usePersistedState<string>({
    page: "skills",
    param: "skill",
    defaultValue: "",
  })
  useResumePageParams("skills", [
    { param: "q", defaultValue: "" },
    { param: "origin", defaultValue: "all" },
    { param: "sort", defaultValue: "name" },
    { param: "trash", defaultValue: "0" },
    { param: "tag", defaultValue: "" },
    { param: "skill", defaultValue: "" },
  ])

  const selected = useMemo(
    () => (selectedName === "" ? null : (skills.find((s) => s.name === selectedName) ?? null)),
    [skills, selectedName],
  )
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

  // Keep the input in step with the persisted query (e.g. clause resumed
  // on mount into the URL after a bare visit). Derivation happens during
  // render; no effect needed.
  const [prevQuery, setPrevQuery] = useState(query)
  if (prevQuery !== query) {
    setPrevQuery(query)
    setDraftQuery(query)
  }

  // Debounced commit of the search draft into the persisted query.
  useEffect(() => {
    const timer = setTimeout(() => setQuery(draftQuery), 400)
    return () => clearTimeout(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [draftQuery])

  const load = useCallback(async () => {
    try {
      const params = new URLSearchParams()
      params.set("pageSize", "100")
      params.set("sort", sort)
      if (query.trim()) params.set("q", query.trim())
      if (tag) params.set("tag", tag)
      if (origin !== "all") params.set("origin", origin)
      if (trashMode) params.set("includeTrashed", "true")
      // The list shows everything that matches: /api/skills caps pageSize at
      // 100, so fetch every page until the backend's `matching` is reached.
      // No catalog state change happens in the fetch path; both origin
      // types are fetched.
      let items: SkillListItem[] = []
      let total = 0
      let matching = 0
      for (let page = 1; ; page++) {
        params.set("page", String(page))
        const data = await apiFetch<SkillsListResponse>(`/api/skills?${params}`, {
          cache: "no-store",
          headers: { "Cache-Control": "no-cache", Pragma: "no-cache" },
        })
        const pageItems = Array.isArray(data?.items) ? data.items : []
        const meta = data?.metadata
        const totalNow = typeof meta?.total === "number" ? meta.total : pageItems.length
        const matchingNow = typeof meta?.matching === "number" ? meta.matching : pageItems.length
        if (page === 1) {
          total = totalNow
          matching = matchingNow
        }
        items = items.concat(pageItems)
        // Stop when a page is short (end of results) or everything the
        // backend counted as matching has been fetched. `page` hard-caps the
        // loop for safety.
        if (pageItems.length < 100 || items.length >= matchingNow || page >= 20) {
          break
        }
      }
      setSkills(items)
      setTotal(total)
      setMatching(matching)
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to load skills")
    } finally {
      setLoading(false)
    }
  }, [query, origin, tag, trashMode, sort])

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
  const selectedListItem = selected
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
    setSelectedName(selectedName === card.name ? "" : card.name)
  }

  const handleNewSkill = () => {
    setSelectedName("")
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
      setSelectedName(created.name)
      // Make the new skill visible regardless of the current origin filter —
      // a freshly created Eling skill would be hidden under "External".
      setOrigin("all")
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
      setSelectedName(selectedName === target.name ? "" : selectedName)
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
        setSelectedName("")
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
    setRescanFailures(null)
    setFailuresTruncated(false)
    try {
      const report = await apiFetch<{
        discovered?: number
        external?: number
        missing?: number
        renamed?: number
        failed?: number
        failures?: SkillRescanFailure[]
        failuresTruncated?: boolean
      }>("/api/skills/rescan", { method: "POST" })
      const failedCount = report?.failed ?? 0
      const failures = Array.isArray(report?.failures) ? report.failures : []
      setRescanFailures(failedCount > 0 && failures.length > 0 ? failures : null)
      setFailuresTruncated(report?.failuresTruncated === true)
      setFailuresExpanded(true)
      setNotice(
        `Rescan complete — discovered ${report?.discovered ?? 0}, external ${report?.external ?? 0}, missing ${report?.missing ?? 0}, renamed ${report?.renamed ?? 0}, failed ${report?.failed ?? 0}.`,
      )
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : "Rescan failed")
      setRescanFailures(null)
      setFailuresTruncated(false)
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
        setSelectedName("")
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

  const panelOpen = wide && (selected !== null || createMode)
  const mainColClass = panelOpen
    ? "flex h-full min-h-0 w-[420px] shrink-0 flex-col gap-4 overflow-y-auto p-4 pt-0"
    : "flex h-full min-h-0 min-w-0 flex-1 flex-col gap-4 overflow-y-auto p-4 pt-0"

  return (
    <div className="flex h-[100dvh] w-full flex-col overflow-hidden">
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

        <Button size="sm" onClick={handleNewSkill} title="New Skill" aria-label="New Skill">
          <Plus className="size-4" />
          <span className="hidden sm:inline-block">New Skill</span>
        </Button>
      </DashboardHeader>

      <div className="flex min-h-0 w-full flex-1 overflow-hidden">
      <div className={mainColClass}>
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
          {rescanFailures && rescanFailures.length > 0 && (
            <div className="rounded-lg border border-amber-500/40 bg-amber-500/5 text-xs">
              <button
                type="button"
                onClick={() => setFailuresExpanded((v) => !v)}
                aria-expanded={failuresExpanded}
                aria-label={failuresExpanded ? "Collapse failed folders" : "Expand failed folders"}
                className="flex w-full items-center justify-between gap-2 px-4 py-2 text-left"
              >
                <span className="font-medium text-amber-700 dark:text-amber-400">
                  {rescanFailures.length} {rescanFailures.length === 1 ? "folder" : "folders"} failed to index
                </span>
                {failuresExpanded ? (
                  <ChevronUp className="size-4 shrink-0 text-amber-700 dark:text-amber-400" />
                ) : (
                  <ChevronDown className="size-4 shrink-0 text-amber-700 dark:text-amber-400" />
                )}
              </button>
              {failuresExpanded && (
                <ul className="flex max-h-40 flex-col gap-1 overflow-y-auto border-t border-amber-500/20 px-4 py-2">
                  {rescanFailures.map((failure, index) => (
                    <li
                      key={`${failure.path}-${index}`}
                      className="flex items-baseline gap-2 text-[11px] leading-snug"
                    >
                      <span
                        className="min-w-0 break-all font-mono text-foreground"
                        title={failure.path}
                      >
                        {failure.path}
                      </span>
                      <span className="shrink-0 text-muted-foreground">— {failure.reason}</span>
                    </li>
                  ))}
                  {failuresTruncated && (
                    <li className="pt-1 text-[10px] text-muted-foreground">
                      Showing the first {rescanFailures.length} failures only.
                    </li>
                  )}
                </ul>
              )}
            </div>
          )}

          {/* Toolbar: search + origin filter + trash toggle */}
          <div className="sticky top-0 z-10 flex flex-col gap-2 bg-background pb-2 pt-4">
            <div className="flex flex-wrap items-center gap-2">
              <Input
                placeholder="Search name, description or body..."
                value={draftQuery}
                onChange={(e) => setDraftQuery(e.target.value)}
                className={`min-w-[180px] flex-1 ${panelOpen ? "basis-full" : "max-lg:basis-full"}`}
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
              <div
                className="flex shrink-0 items-center gap-1 rounded-lg border bg-muted/40 p-1"
                role="group"
                aria-label="Sort order"
              >
                {(
                  [
                    ["name", "Name"],
                    ["updated", "Updated"],
                  ] as const
                ).map(([value, label]) => (
                  <button
                    key={value}
                    onClick={() => {
                      setLoading(true)
                      // The list has no page state — every change refetches
                      // from page 1.
                      setSort(value)
                    }}
                    aria-pressed={sort === value}
                    className={
                      sort === value
                        ? "rounded-md bg-background px-2.5 py-1 text-xs font-medium text-foreground shadow-sm"
                        : "rounded-md px-2.5 py-1 text-xs font-medium text-muted-foreground hover:text-foreground"
                    }
                  >
                    {label}
                  </button>
                ))}
              </div>
              <label
                className="flex shrink-0 cursor-pointer items-center gap-1.5 text-xs text-muted-foreground"
              >
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
                      setTag(tag === t ? null : t)
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
              <div className={panelOpen ? "grid grid-cols-1 gap-2.5" : "grid grid-cols-1 gap-2.5 lg:grid-cols-2"}>
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

          {/* Narrow screens: the panel opens as a popup dialog over the grid. */}
          {!wide && (selectedListItem || createMode) && (
            <Dialog
              open
              onOpenChange={(open) => {
                if (!open) {
                  setSelectedName("")
                  setCreateMode(false)
                }
              }}
            >
              <DialogContent
                showCloseButton={false}
                className="max-h-[85svh] w-[94vw] overflow-y-auto p-0 sm:max-w-2xl"
              >
                <DialogTitle className="sr-only">
                  {createMode ? "New skill" : (selectedListItem?.name ?? "Skill detail")}
                </DialogTitle>
                <DialogDescription className="sr-only">
                  {createMode ? "Create a new skill" : "Skill detail panel"}
                </DialogDescription>
                {panel("stacked")}
              </DialogContent>
            </Dialog>
          )}
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
        <AlertDialogContent className="w-[94vw] sm:max-w-2xl">
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
