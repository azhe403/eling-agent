"use client"

import { useCallback, useEffect, useState } from "react"
import { AlertCircle, FolderOpen, Loader2, Plus, Trash2 } from "lucide-react"

import { Button } from "@/components/ui/button"
import { Card, CardContent, CardDescription, CardHeader } from "@/components/ui/card"
import { Input } from "@/components/ui/input"
import { Switch } from "@/components/ui/switch"
import { apiFetch } from "@/lib/api"
import { utcToLocal, utcToLocalDate } from "@/lib/date-utils"

import { HostPathPicker } from "@/components/host-path-picker"

type ShellRule = { enabled: boolean; createdAt: string; updatedAt: string | null }

/** Wire contract of GET/PUT /api/shell/config: the whole config, camelCase, full replacement. */
interface ShellConfigDto {
  enabled: boolean
  restrictToAllowedDirectories: boolean
  allowedDirectories: Array<ShellRule & { path: string }>
  blockedCommands: Array<ShellRule & { pattern: string }>
  rootGuardrail: boolean
  defaultTimeoutSeconds: number
  maxOutputBytes: number
  updatedAt: string
}

/** One manager list: rows plus a draft input. Both rule kinds share it. */
function RuleList({ title, description, placeholder, emptyText, rows, saving, onToggle, onDelete, onAdd, onBrowse }: {
  title: string
  description: string
  placeholder: string
  emptyText: string
  rows: Array<{ label: string; enabled: boolean; createdAt: string }>
  saving: boolean
  onToggle: (index: number, enabled: boolean) => void
  onDelete: (index: number) => void
  onAdd: (value: string) => void
  onBrowse?: () => void
}) {
  const [draft, setDraft] = useState("")

  function add() {
    const value = draft.trim()
    if (!value) return
    onAdd(value)
    setDraft("")
  }

  return (
    <div className="flex flex-col gap-2 rounded-lg border p-3">
      <div>
        <p className="text-sm font-medium">{title}</p>
        <p className="mt-0.5 text-xs text-muted-foreground">{description}</p>
      </div>
      {rows.length === 0 ? <p className="text-xs text-muted-foreground">{emptyText}</p> : null}
      {rows.map((row, index) => {
        const full = utcToLocal(row.createdAt) || row.createdAt
        const short = utcToLocalDate(row.createdAt) || row.createdAt
        return (
          <div key={`${row.label}-${row.createdAt}`} className="flex items-center gap-2 rounded-md border px-2 py-1.5">
            <code className="min-w-0 flex-1 truncate text-sm">{row.label}</code>
            <span className="shrink-0 text-xs text-muted-foreground" title={full}>
              <span className="md:hidden">{short}</span>
              <span className="hidden md:inline">{full}</span>
            </span>
            <Switch checked={row.enabled} disabled={saving} onCheckedChange={(checked) => onToggle(index, checked)} aria-label={`Toggle ${row.label}`} />
            <Button variant="ghost" size="icon-sm" disabled={saving} onClick={() => onDelete(index)} aria-label={`Delete ${row.label}`}>
              <Trash2 className="size-3.5" />
            </Button>
          </div>
        )
      })}
      <div className="flex items-center gap-2">
        <Input value={draft} placeholder={placeholder} disabled={saving} onChange={(event) => setDraft(event.target.value)} onKeyDown={(event) => { if (event.key === "Enter") add() }} />
        {onBrowse ? (
          <Button variant="outline" size="sm" disabled={saving} onClick={onBrowse} aria-label="Browse host directories">
            <FolderOpen className="size-4" />
            Browse
          </Button>
        ) : null}
        <Button variant="outline" size="sm" disabled={saving || !draft.trim()} onClick={add}>
          <Plus className="size-3.5" />
          Add
        </Button>
      </div>
    </div>
  )
}

/** Label + description on the left, its switch on the right. */
function ToggleRow({ label, description, checked, disabled, onChange }: {
  label: string
  description: string
  checked: boolean
  disabled: boolean
  onChange: (checked: boolean) => void
}) {
  return (
    <div className="flex items-start gap-3">
      <div className="min-w-0 flex-1">
        <p className="text-sm font-medium">{label}</p>
        <p className="mt-0.5 text-xs text-muted-foreground">{description}</p>
      </div>
      <Switch checked={checked} disabled={disabled} onCheckedChange={onChange} aria-label={label} />
    </div>
  )
}

export function ShellConfigCard() {
  const [config, setConfig] = useState<ShellConfigDto | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [pickerOpen, setPickerOpen] = useState(false)
  const [timeoutDraft, setTimeoutDraft] = useState("")
  const [outputDraft, setOutputDraft] = useState("")
  const [timeoutError, setTimeoutError] = useState<string | null>(null)
  const [outputError, setOutputError] = useState<string | null>(null)

  const load = useCallback(async (): Promise<ShellConfigDto> => {
    return apiFetch<ShellConfigDto>("/api/shell/config", { cache: "no-store" })
  }, [])

  useEffect(() => {
    let active = true
    void (async () => {
      try {
        const data = await load()
        if (!active) return
        setConfig(data)
        setLoadError(null)
      } catch (err: unknown) {
        if (!active) return
        const message = err instanceof Error ? err.message : "Unknown error."
        setLoadError(message)
      }
    })()
    return () => {
      active = false
    }
  }, [load])

  // Optimistic write: apply the change to the UI immediately, then revert with a
  // fresh read from the server so a rejected value never lingers. The server is
  // still the authority — on success the response replaces the snapshot (it may
  // normalize e.g. empty rules). A failed re-read falls back to the pre-save snapshot.
  const save = useCallback(
    async (next: ShellConfigDto) => {
      if (!config) return
      const previous = config
      setConfig(next)
      setSaving(true)
      setSaveError(null)
      try {
        const updated = await apiFetch<ShellConfigDto>("/api/shell/config", {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(next),
        })
        setConfig(updated)
      } catch (err) {
        try {
          const fresh = await apiFetch<ShellConfigDto>("/api/shell/config", { cache: "no-store" })
          setConfig(fresh)
        } catch {
          setConfig(previous)
        }
        setSaveError(err instanceof Error ? err.message : "Unknown error.")
      } finally {
        setSaving(false)
      }
    },
    [config]
  )

  // Keep the numeric drafts in step with the persisted config: reset them
  // during render when a new snapshot arrives, before the DOM uses them.
  const [prevConfig, setPrevConfig] = useState(config)
  if (config !== prevConfig) {
    setPrevConfig(config)
    setTimeoutDraft(String(config?.defaultTimeoutSeconds ?? ""))
    setOutputDraft(String(config?.maxOutputBytes ?? ""))
    setTimeoutError(null)
    setOutputError(null)
  }

  function commitNumber(field: "defaultTimeoutSeconds" | "maxOutputBytes", draft: string) {
    if (saving) return
    const value = Number.parseInt(draft, 10)
    const isTimeout = field === "defaultTimeoutSeconds"
    const label = isTimeout ? "Default timeout" : "Max output bytes"
    const setFieldError = isTimeout ? setTimeoutError : setOutputError
    if (!Number.isFinite(value) || value < 1) {
      setFieldError(`${label} must be at least 1.`)
      return
    }
    setFieldError(null)
    if (!config || config[field] === value) return
    void save(isTimeout ? { ...config, defaultTimeoutSeconds: value } : { ...config, maxOutputBytes: value })
  }

  const commit = (patch: Partial<ShellConfigDto>) => {
    if (config) void save({ ...config, ...patch })
  }

  return (
    <Card>
      <CardHeader>
        <CardDescription>
          <code>shell_execute</code> settings. The card edits <code>shell-config.json</code> directly; changes apply immediately. See the summary above for what this tool does before enabling it.
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {!config && !loadError ? (
          <p className="flex items-center gap-2 text-sm text-muted-foreground">
            <Loader2 className="size-4 animate-spin" />
            Loading shell configuration…
          </p>
        ) : null}
        {loadError ? (
          <p className="flex items-start gap-2 text-sm text-destructive">
            <AlertCircle className="mt-0.5 size-4 shrink-0" />
            <span className="break-words">{loadError}</span>
          </p>
        ) : null}
        {saveError ? (
          <p className="flex items-start gap-2 text-sm text-destructive">
            <AlertCircle className="mt-0.5 size-4 shrink-0" />
            <span className="break-words">Last update failed: {saveError}. The UI was reverted to the persisted value.</span>
          </p>
        ) : null}

        {config ? (
          <>
            <ToggleRow label="Enable shell_execute" description="Off means the tool refuses every call and no agent can run a command." checked={config.enabled} disabled={saving} onChange={(checked) => void save({ ...config, enabled: checked })} />
            <ToggleRow
              label={config.restrictToAllowedDirectories ? "Restricted (cwd + whitelist)" : "Allow any directory"}
              description={config.restrictToAllowedDirectories ? "On: commands may only run inside the working directory and the whitelist below." : "Off: commands may run in any directory the backend process can reach."}
              checked={config.restrictToAllowedDirectories}
              disabled={saving}
              onChange={(checked) => void save({ ...config, restrictToAllowedDirectories: checked })}
            />
            <ToggleRow label="Root protection guardrail" description="Blocks destructive commands targeting a filesystem root or format volume (e.g. rm -rf /)." checked={config.rootGuardrail} disabled={saving} onChange={(checked) => void save({ ...config, rootGuardrail: checked })} />

            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <label className="flex flex-col gap-1">
                <span className="text-sm font-medium">Default timeout (seconds)</span>
                <Input type="number" min={1} value={timeoutDraft} disabled={saving} onChange={(event) => { setTimeoutDraft(event.target.value); setTimeoutError(null) }} onBlur={(event) => commitNumber("defaultTimeoutSeconds", event.target.value)} onKeyDown={(event) => { if (event.key === "Enter") event.currentTarget.blur() }} />
                <span className={timeoutError ? "text-xs text-destructive" : "text-xs text-muted-foreground"}>{timeoutError ?? "Must be at least 1."}</span>
              </label>
              <label className="flex flex-col gap-1">
                <span className="text-sm font-medium">Max output bytes</span>
                <Input type="number" min={1} value={outputDraft} disabled={saving} onChange={(event) => { setOutputDraft(event.target.value); setOutputError(null) }} onBlur={(event) => commitNumber("maxOutputBytes", event.target.value)} onKeyDown={(event) => { if (event.key === "Enter") event.currentTarget.blur() }} />
                <span className={outputError ? "text-xs text-destructive" : "text-xs text-muted-foreground"}>{outputError ?? "Must be at least 1."}</span>
              </label>
            </div>

            <RuleList
              title="Allowed directories"
              description="Extra directories commands may run in while the restriction above is on."
              placeholder={"C:\\path\\to\\dir"}
              emptyText="No whitelist entries."
              rows={config.allowedDirectories.map((rule) => ({ label: rule.path, enabled: rule.enabled, createdAt: rule.createdAt }))}
              saving={saving}
              onToggle={(index, enabled) => commit({ allowedDirectories: config.allowedDirectories.map((rule, i) => (i === index ? { ...rule, enabled } : rule)) })}
              onDelete={(index) => commit({ allowedDirectories: config.allowedDirectories.filter((_, i) => i !== index) })}
              onAdd={(path) => commit({ allowedDirectories: [...config.allowedDirectories, { path, enabled: true, createdAt: new Date().toISOString(), updatedAt: null }] })}
              onBrowse={() => setPickerOpen(true)}
            />

            <RuleList
              title="Blocked commands"
              description="Substring match, case-insensitive, anywhere in the command line. Not regex — matching is literal, so block 'git checkout' to refuse that exact fragment."
              placeholder="git checkout"
              emptyText="No blocked commands."
              rows={config.blockedCommands.map((rule) => ({ label: rule.pattern, enabled: rule.enabled, createdAt: rule.createdAt }))}
              saving={saving}
              onToggle={(index, enabled) => commit({ blockedCommands: config.blockedCommands.map((rule, i) => (i === index ? { ...rule, enabled } : rule)) })}
              onDelete={(index) => commit({ blockedCommands: config.blockedCommands.filter((_, i) => i !== index) })}
              onAdd={(pattern) => commit({ blockedCommands: [...config.blockedCommands, { pattern, enabled: true, createdAt: new Date().toISOString(), updatedAt: null }] })}
            />
          </>
        ) : null}
      </CardContent>
      <HostPathPicker
        open={pickerOpen}
        mode="directory"
        title="Select whitelist folder"
        onSelect={(path) => {
          setPickerOpen(false)
          if (config) {
            void commit(
              { allowedDirectories: [...config.allowedDirectories, { path, enabled: true, createdAt: new Date().toISOString(), updatedAt: null }] },
            )
          }
        }}
        onClose={() => setPickerOpen(false)}
      />
    </Card>
  )
}
