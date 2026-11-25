# Rename `basePath` to `path` on the filesystem search tools

Status: Draft — awaiting approval

## Location note (2026-11-24)

Created under `docs/superpowers/specs/` on 2026-11-23. A short detour on
2026-11-24 moved it into `.eling/docs/specs/` under the then-current managed-docs
policy; the approved Unified Docs revamp retired that distinction, and the
one-time migration returned it to `docs/superpowers/specs/`. It is now an
ordinary Markdown doc like any other — no ownership or origin label and no
promotion step. The `doc_save` foreign-key defect that motivated the detour is
designed out by that revamp and is not part of this change.

This rename is independent of the docs subsystem: it only changes MCP/agent
tool parameter names so OpenChamber renders a tool-call subtitle.

## Intent

OpenChamber/OpenCode renders a subtitle on a tool call when the tool exposes a
parameter named `path` or `description`. Ten of Eling's filesystem MCP tools
already expose `path`, but `glob` and `file_search` expose `basePath`, so those
two calls render without a subtitle even though the agent sends a real path
value on every call.

This change renames that one wire parameter to `path` on both tool surfaces
Eling exposes (the MCP tools and the parallel internal agent tools), so both
surfaces stay aligned and the two calls get the same subtitle treatment as the
other ten filesystem tools. Nothing about validation, sandboxing, or result
shape changes.

## Goals

- `glob` and `file_search` MCP tools expose `path` instead of `basePath` in
  their generated JSON schema.
- The internal `GlobAgentTool` and `FileSearchAgentTool` hand-written JSON
  schemas and argument parsers use `path` as well.
- Tool and parameter descriptions say "path" instead of "base path" where they
  describe the search root.
- CHANGELOG records the wire rename under `Unreleased` → `Changed`.

## Non-goals

- No other path-like parameter is renamed in this change: `sourcePath` /
  `destinationPath` (copy/move), `relPath` (`doc_save`), `paths` (arrays on
  `codebase_index`, `doc_index`, `commit_lease`), `pathPrefix` (a filter on
  `codebase_search`, `doc_search`), and `workingDirectory` (`shell_execute`).
- No `description` metadata parameter is added to the other tools. That is a
  separate decision, out of scope here.
- Internal C# parameter names on `IFileSystemService`, `FileSystemService`, and
  `FakeFileSystemService` are left alone. They do not appear in any tool wire
  schema, so renaming them adds diff without benefit.
- No backward-compatible `basePath` alias is kept. The rename is clean.

## Constraints

- All artifacts in this repo are written in English.
- No commit without explicit user approval.
- Build and test with isolated output per repo convention; do not run
  `pnpm build` while the frontend dev server is running.
- Only LITE / changed-file tests are run, per the standing test rule.

## Approaches

1. **Clean rename on both surfaces (chosen).** Rename `basePath` → `path` in
   the two MCP tool methods and in the two agent-tool schemas/parsers. One
   convention across all twelve filesystem tools. Cost: callers that still send
   `basePath=` break; there are no such references in this repo's instructions,
   skills, or tests, and the agent reads the new schema from the tool metadata.
2. **MCP-only rename.** Change only `FileSystemTools.cs`, leave the agent-tool
   schemas on `basePath`. Smaller diff, but the two surfaces drift apart and a
   future reader cannot tell which name is canonical. Rejected: the user asked
   for both surfaces to stay in sync.
3. **Add `path` as an alias, keep `basePath`.** Non-breaking for old callers,
   but the agent keeps sending `basePath`, so the subtitle never appears — the
   whole point of the change is lost. Rejected.

## Design

- `src/backend/Eling.Backend/Mcp/Tools/FileSystemTools.cs`: rename the
  `basePath` parameter of `Glob` and `SearchFiles` to `path`; update the
  `_fs.Glob(...)` / `_fs.SearchFiles(...)` call sites; adjust the tool-level
  and parameter-level `Description` text where it says "base path".
- `src/backend/Eling.Backend/Agent/Services/Tools/GlobAgentTool.cs` and
  `FileSearchAgentTool.cs`: replace `basePath` in `ParametersJsonSchema`
  (`required` array and `properties` object) and in `ExecuteAsync`'s property
  reads; update each `Description` string.
- `CHANGELOG.md`: one entry under `## [Unreleased] - 2026-11-23` →
  `### Changed`, noting the wire rename on both tool surfaces and that only the
  parameter name changed.
- No service-layer, spec-document, or test-file edits. A repository-wide search
  found no test asserting these parameter names; `basePath` in the service
  layer and in the historical 2026-09-04 filesystem spec is unrelated to the
  wire schema.

## Verification

- The two new wire-name/schema tests pass, and the existing `FileSystemToolsTests`
  and `AgentBuiltinMcpToolTests` classes stay green.
- No `basePath` remains in `Mcp/Tools` or `Agent/Services/Tools`; service-layer
  names and the historical 2026-09-04 spec are out of scope.
- Build uses an isolated output directory; `pnpm build` is never run while the
  dev server is up.
- CHANGELOG carries one entry under `Unreleased` → `Changed`.
