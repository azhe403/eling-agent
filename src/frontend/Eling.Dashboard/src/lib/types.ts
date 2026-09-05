// Shared domain types and constants for memory records across the dashboard.
// Centralising these lets the list, editor, card and create-page agree on
// shape and on the allowed type/status strings without parallel string arrays.

export type Memory = {
  id: string
  type: string
  status: string
  content: string
  tags: string[]
  createdAt: string
  updatedAt: string
  source?: string | null
  scope?: string
  project?: { id: string; root: string } | null
}

export type Runtime = { projectRoot: string; dataDirectory: string }

// `TYPES` includes "All" so the filter UI can render a no-filter option alongside
// the editable types; `EDIT_TYPES` is the slice used inside the edit form where
// the "All" sentinel would be meaningless.
export const TYPES = ["All", "Fact", "Preference", "Decision", "Lesson", "Note"] as const
export const EDIT_TYPES = TYPES.slice(1)
export const STATUSES = ["Active", "Superseded", "Archived"] as const

export type MemoryType = (typeof TYPES)[number]
export type MemoryStatus = (typeof STATUSES)[number]

// Tailwind class lookups per memory type. Falls back to "Note" gray in callers
// when a memory carries an unknown type string.
export const typeBadge: Record<string, string> = {
  Fact: "bg-blue-500/10 text-blue-600 dark:text-blue-400",
  Preference: "bg-purple-500/10 text-purple-600 dark:text-purple-400",
  Decision: "bg-amber-500/10 text-amber-600 dark:text-amber-400",
  Lesson: "bg-green-500/10 text-green-600 dark:text-green-400",
  Note: "bg-gray-500/10 text-gray-600 dark:text-gray-400",
}

export const statusBadge: Record<string, string> = {
  Active: "bg-green-500/10 text-green-600 dark:text-green-400",
  Superseded: "bg-amber-500/10 text-amber-600 dark:text-amber-400",
  Archived: "bg-gray-500/10 text-gray-500",
}