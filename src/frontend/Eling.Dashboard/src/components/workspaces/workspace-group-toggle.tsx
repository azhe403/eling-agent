"use client"

import { Button } from "@/components/ui/button"
import type { WorkspaceGroupMode } from "./grouping"

const MODES: readonly WorkspaceGroupMode[] = ["flat", "repo", "folder"]

const COPY: Record<WorkspaceGroupMode, { label: string; title: string }> = {
  flat: {
    label: "Flat",
    title: "One row per workspace, the way the runtime registry holds them",
  },
  repo: {
    label: "Repo",
    title: "One entry per git repository, with its worktrees nested under it",
  },
  folder: {
    label: "Folder",
    title:
      "One entry per workspace, nested by the folders that hold them — a worktree still sits under its repository",
  },
}

/**
 * How the workspace list is arranged: flat, grouped by git repository, or
 * grouped by the folders that hold the workspaces.
 *
 * The same three-way shape as the search scope toggle, and for the same
 * reason: the answer is one of a small, fixed set, so a segmented control
 * states it in one glance instead of hiding it behind a menu.
 */
export function WorkspaceGroupToggle({
  mode,
  onChange,
}: {
  mode: WorkspaceGroupMode
  onChange: (mode: WorkspaceGroupMode) => void
}) {
  return (
    <div
      role="group"
      aria-label="Workspace grouping"
      className="flex w-full shrink-0 gap-0.5 rounded-lg border p-0.5"
    >
      {MODES.map((value) => (
        <Button
          key={value}
          type="button"
          size="sm"
          variant={mode === value ? "secondary" : "ghost"}
          aria-pressed={mode === value}
          onClick={() => onChange(value)}
          title={COPY[value].title}
          className="flex-1 px-2 text-xs"
        >
          {COPY[value].label}
        </Button>
      ))}
    </div>
  )
}
