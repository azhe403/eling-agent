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

// Mirrors GET /api/coordinator/runtimes. Hand-maintained, so it drifts silently
// the moment the backend DTO changes - the field it used to declare,
// `projectRoot`, was renamed to `headScopeRoot`/`workspaceRoot` and this type
// kept claiming it existed. Because the shape is asserted rather than inferred,
// reading a renamed field still type-checks and only throws at runtime.
// `headScopeRoot` is the project's own scope root and is what the
// copy-to-project endpoint expects as `targetProjectRoot`; `workspaceRoot` is
// the broader tree containing it.
export type Runtime = {
  processId: number
  headScopeRoot: string
  workspaceRoot: string
  dataDirectory: string
  codebaseEnabled: boolean
  isAlive: boolean
  lastHeartbeat: string
  startTime: string
  mcpEnabled: boolean
  mcpTransport: string
}

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

// Wire shapes for the skill catalog (GET /api/skills[...]). The backend DTOs
// pin camelCase names via JsonPropertyName, so this mirrors the wire exactly.
// The list answers with compact items (no body, no related refs); the single
// GET adds those. `origin` stays a plain string on the wire - new origins
// must not fail type-checking, only the badge lookup needs a fallback.
export type SkillOrigin = "eling" | "external"

export type SkillRelatedRef = {
  id: string
  scope?: string
  relation: string
}

export type SkillListItem = {
  id: string
  name: string
  description: string
  path: string
  origin: SkillOrigin | string
  tags: string[]
  updatedAt: string
}

export type Skill = {
  id: string
  name: string
  description: string
  body: string
  path: string
  origin: SkillOrigin | string
  tags: string[]
  relatedMemories: SkillRelatedRef[]
  relatedSnippets: SkillRelatedRef[]
  createdAt: string
  updatedAt: string
}

export type SkillsListMetadata = {
  total: number
  matching: number
  limit?: number | null
  offset?: number | null
}

export type SkillsListResponse = {
  items: SkillListItem[]
  metadata: SkillsListMetadata
}

export type SkillFormData = {
  name: string
  description: string
  tags: string[]
  body: string
}

// --- Docs subsystem (GET /api/docs[...]) ---

export type DocOwnership = "managed" | "external"
export type DocOrigin = "current" | "ancestor" | "shard" | "global"
export type DocKind = "spec" | "plan" | "doc" | "scratch"
export type ReferenceKind = "symbol" | "path" | "anchor_link"
export type RelationKind = "implements" | "supersedes" | "references"

export type DocKindDto = 0 | 1 | 2 | 3 // Spec, Plan, Doc, Scratch
export type DocOwnershipDto = 0 | 1 // Managed, External
export type DocOriginDto = 0 | 1 | 2 | 3 // Current, Ancestor, Shard, Global
export type ReferenceKindDto = 0 | 1 | 2 // Symbol, Path, AnchorLink
export type RelationKindDto = 0 | 1 | 2 // Implements, Supersedes, References

export type DocListItem = {
  /** The wire field from the list endpoint is `docId`; the read endpoint sends `id`. */
  docId?: string
  id: string
  ownership: DocOwnershipDto
  origin: DocOriginDto
  relPath: string
  kind: DocKindDto
  title: string
  status: number
  topic: string
  order: number
  supersededBy: string | null
  stepsTotal: number
  stepsDone: number
  fileHash: string
  mTime: string
  indexedAt: string
  frontMatterError: boolean
  frontMatterErrorMessage: string | null
}

export type DocTopicGroup = {
  topic: string
  documents: DocListItem[]
}

export type DocListResponse = {
  topics: DocTopicGroup[]
}

export type DocSection = {
  docId: string
  anchor: string
  heading: string
  level: number
  lineStart: number
  lineEnd: number
  status: number
  annotation: string | null
  stepsTotal: number
  stepsDone: number
}

export type DocRef = {
  docId: string
  anchor: string
  rawText: string
  refKind: ReferenceKindDto
  resolved: boolean
  resolvedPath: string | null
  resolvedLine: number | null
  resolvedAt: string | null
}

export type DocRelation = {
  sourceId: string
  targetId: string
  kind: RelationKindDto
}

export type DocReadResult = {
  document: DocListItem
  sections: DocSection[]
  references: DocRef[]
  relations: DocRelation[]
  /** Raw markdown of the document, when the read endpoint was asked for it. */
  content?: string | null
}

export type DocSearchResult = {
  docId: string
  ownership: DocOwnershipDto
  origin: DocOriginDto
  relPath: string
  kind: DocKindDto
  title: string
  status: number
  topic: string
  stepsTotal: number
  stepsDone: number
  rank: number
}

export type StalenessFinding = {
  docId: string
  kind: number // StalenessKindDto
  message: string
  anchor: string | null
  reference: string | null
}

export type StalenessReport = {
  findings: StalenessFinding[]
  generatedAt: string
}

export type DocAuditEntry = {
  id: string
  docId: string
  actor: string
  action: string
  beforeJson: string | null
  afterJson: string | null
  at: string
}

export type ScopePolicyResponse = {
  policy: {
    include: string[]
    exclude: string[]
  }
  decision: string | null
}

export type OfferCheckResult = {
  shouldOffer: boolean
  state: number // OfferStateDto
  reason: string
}
