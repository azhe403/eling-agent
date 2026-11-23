# Docs Viewer Markdown Modes Implementation Plan

> **Status: COMPLETE.** All three tasks executed via inline (Native) execution on 2026-11-23. Commits: `7ed7f2d` (deps), `63ae75a` (viewer + tests + styles), `e822aab` (spec accepted), `56a1e92` (this plan). The step checkboxes below reflect the original plan and were not individually re-ticked; follow-up fixes landed after planning and are recorded in the spec's Implementation note: `376e1d4` (strip YAML front-matter from Preview) and `0ef5d07` (header restructure + per-field copy buttons). Verification: 8/8 panel tests, full suite 302/302, tsc clean, eslint 0 errors, browser-verified at 4427. Independent review found 0 Critical / 0 Important; two deferred Minors (stale comment in `renderContentTab`; Source→Preview round-trip not asserted).

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a rendered-markdown **Preview** mode (default) alongside the existing raw **Source** mode in the docs detail panel's Content tab.

**Architecture:** Install `react-markdown` + `remark-gfm` in the dashboard. `DocDetailPanel.tsx` gains a local `viewMode` state rendered as a small `Preview | Source` segmented control inside the Content tab. Preview renders `<ReactMarkdown>` with GFM; Source keeps the current monospace `<pre>`. Styling lives in a scoped `.md-preview` CSS block in `globals.css` using existing theme tokens — no typography plugin. The panel is already keyed by document id (`DocsList.tsx:282`), so switching documents remounts it and `viewMode` resets to `preview` without any effect.

**Tech Stack:** Next.js 16.4.5, React 19.3.1, TypeScript, Tailwind CSS v4 (`@import "tailwindcss"`, `@theme inline`), vitest 5 + @testing-library/react, pnpm.

**Spec:** `docs/superpowers/specs/2026-11-23-docs-markdown-viewer-modes-design.md`

## Global Constraints

- Before writing code, read the relevant guide under `node_modules/next/dist/docs/` (per `src/frontend/Eling.Dashboard/AGENTS.md`) — this Next.js version has breaking changes.
- Frontend verification uses `pnpm exec tsc --noEmit` and vitest — never `next build` while the dev server runs on port 4427 (overwrites `.next/`, deadlocks Turbopack on Windows).
- Raw HTML in markdown must be escaped; never enable `rehype-raw` or `dangerouslySetInnerHTML` for document content.
- Keep the four existing top-level tabs (Content / Sections / References / Relations) unchanged.
- `.md-preview` styling must use existing CSS variables (`--border`, `--muted-foreground`, `--primary`, etc.) so it follows light/dark.
- Commit must be frontend-only. Pre-commit hook enforces one layer per commit; use an isolated `GIT_INDEX_FILE` in the shared checkout to avoid sweeping other chats' staged files.
- Call `commit_lease` before any git add/commit and `commit_release` immediately after.

## Review Focus

- **Raw HTML in a document** — a doc containing `<script>alert(1)</script>` must render as escaped text, not execute. Test: Preview test feeds raw HTML and asserts no `<script>` element is created.
- **GFM table** — a pipe table must render a real `<table>`, not a literal line. Test: Preview test asserts `getByRole("table")`.
- **Task checkbox** — `- [x] done` must render a checked checkbox. Test: assert `getByRole("checkbox")` is checked.
- **Fenced code block** — triple-backtick code must render inside `<pre><code>`. Test: assert a `code` element contains the source.
- **Source/Preview parity** — both modes must show the same underlying text and switching must not lose content. Test: click Source, assert raw text present; click Preview, assert rendered text present.

---

### Task 1: Install markdown dependencies

**Files:**
- Modify: `src/frontend/Eling.Dashboard/package.json`
- Modify: `src/frontend/Eling.Dashboard/pnpm-lock.yaml`

**Interfaces:**
- Produces: `react-markdown` and `remark-gfm` available as dependencies to Task 2.

- [ ] **Step 1: Install the two packages**

Run in `src/frontend/Eling.Dashboard`:
```bash
pnpm add react-markdown remark-gfm
```
Expected: both added to `dependencies`; lockfile updated.

- [ ] **Step 2: Verify the install**

Run: `pnpm exec tsc --noEmit`
Expected: no errors.

- [ ] **Step 3: Commit**

Use `commit_lease` (paths: `src/frontend/Eling.Dashboard/package.json`, `src/frontend/Eling.Dashboard/pnpm-lock.yaml`), stage only those two files via an isolated `GIT_INDEX_FILE`, commit, then `commit_release`.
Message: `chore(dashboard): add react-markdown and remark-gfm`

---

### Task 2: Add the two view modes to the docs detail panel

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/docs/DocDetailPanel.tsx` (renderContentTab ~lines 425-459; state near the component's other hooks)
- Test: `src/frontend/Eling.Dashboard/src/app/dashboard/docs/DocDetailPanel.test.tsx`
- Modify: `src/frontend/Eling.Dashboard/src/app/globals.css` (append `.md-preview` block)

**Interfaces:**
- Consumes: `react-markdown` default export `ReactMarkdown`, `remark-gfm` default export `remarkGfm` (Task 1).
- Produces: Content tab with `Preview | Source` control; `.md-preview` class styled in globals.css.

- [ ] **Step 1: Write the failing tests**

Add to `DocDetailPanel.test.tsx` (use `render`, `getByRole`, `getByText`, `fireEvent` from @testing-library/react; build props with `detail` carrying `content`):

```tsx
it("renders GFM tables, task checkboxes and code in Preview mode", () => {
  const md = "| A | B |\n|---|---|\n| 1 | 2 |\n\n- [x] done\n\n```ts\nconst x = 1\n```";
  const { getByRole, getByText } = render(
    <DocDetailPanel {...baseProps} detailLoading={false} detail={{ ...detail, content: md }} />,
  );
  expect(getByRole("table")).toBeDefined();
  expect(getByRole("checkbox")).toBeChecked();
  expect(getByText("const x = 1")).toBeDefined();
});

it("escapes raw HTML in Preview mode", () => {
  const { container } = render(
    <DocDetailPanel {...baseProps} detailLoading={false} detail={{ ...detail, content: "<script>alert(1)</script>" }} />,
  );
  expect(container.querySelector("script")).toBeNull();
});

it("keeps raw source text in Source mode", () => {
  const md = "# Heading\n\n| A | B |\n|---|---|\n| 1 | 2 |";
  const { getByText, getByRole } = render(
    <DocDetailPanel {...baseProps} detailLoading={false} detail={{ ...detail, content: md }} />,
  );
  fireEvent.click(getByRole("button", { name: "Source" }));
  expect(getByText(/# Heading/)).toBeDefined();
});
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `pnpm exec vitest run src/app/dashboard/docs/DocDetailPanel.test.tsx`
Expected: FAIL — no "Preview"/"Source" buttons, no table/checkbox rendered.

- [ ] **Step 3: Add `viewMode` state**

In `DocDetailPanel.tsx`, alongside the other `useState` hooks in the component body, add:
```tsx
const [viewMode, setViewMode] = useState<"preview" | "source">("preview")
```
No reset effect is needed: `DocsList.tsx:282` keys the panel by document id, so a different document remounts the component and the default applies.

- [ ] **Step 4: Rewrite `renderContentTab`**

Keep the `content === undefined/null` and empty guards unchanged. Replace the final `return` (the `<pre>` block) with a fragment containing:
- a segmented control row with two buttons, `aria-pressed` reflecting `viewMode`, labels exactly `"Preview"` and `"Source"`, calling `setViewMode("preview")` / `setViewMode("source")`. Use a small bordered container with `data-state`-driven active styling consistent with the existing tab triggers (`-mb-px border-b-2`, active `border-primary text-foreground`).
- below it, a `<ScrollArea className="h-full">` containing either:
  - preview: `<ReactMarkdown remarkPlugins={[remarkGfm]} className="md-preview">{content}</ReactMarkdown>`
  - source: the existing `<pre className="whitespace-pre-wrap break-words p-4 text-xs leading-relaxed font-mono text-foreground">{content}</pre>`

Add the imports: `import ReactMarkdown from "react-markdown"` and `import remarkGfm from "remark-gfm"` at the top of the file.

- [ ] **Step 5: Add `.md-preview` styles**

Append to `src/app/globals.css` inside `@layer base` (after the existing rules, before the closing brace of the layer — check the current end of the layer first):

```css
.md-preview {
  @apply p-4 text-sm leading-relaxed;
}
.md-preview h1 { @apply mb-3 mt-5 text-xl font-semibold; }
.md-preview h2 { @apply mb-2 mt-5 text-lg font-semibold; }
.md-preview h3 { @apply mb-2 mt-4 text-base font-semibold; }
.md-preview h4 { @apply mb-2 mt-4 text-sm font-semibold; }
.md-preview p { @apply my-2; }
.md-preview ul { @apply my-2 list-disc pl-6; }
.md-preview ol { @apply my-2 list-decimal pl-6; }
.md-preview li { @apply my-0.5; }
.md-preview a { @apply text-primary underline underline-offset-2; }
.md-preview strong { @apply font-semibold; }
.md-preview blockquote { @apply my-3 border-l-2 border-border pl-4 text-muted-foreground; }
.md-preview code { @apply rounded bg-muted px-1 py-0.5 font-mono text-xs; }
.md-preview pre { @apply my-3 overflow-x-auto rounded-lg bg-muted p-3; }
.md-preview pre code { @apply bg-transparent p-0; }
.md-preview table { @apply my-3 w-full border-collapse text-xs; }
.md-preview th { @apply border border-border bg-muted px-2 py-1 text-left font-medium; }
.md-preview td { @apply border border-border px-2 py-1; }
.md-preview input[type="checkbox"] { @apply mr-1.5 accent-primary; }
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `pnpm exec vitest run src/app/dashboard/docs/DocDetailPanel.test.tsx`
Expected: PASS (all existing + new tests).

- [ ] **Step 7: Type-check and lint**

Run:
```bash
pnpm exec tsc --noEmit
pnpm exec eslint src/app/dashboard/docs/DocDetailPanel.tsx src/app/dashboard/docs/DocDetailPanel.test.tsx
```
Expected: `tsc` clean; eslint 0 errors (warnings allowed).

- [ ] **Step 8: Verify in the browser**

With the dev server on 4427, open `http://localhost:4427/dashboard/docs`, open a table-heavy document (e.g. "Client Sorting Standard" or a spec with tables). Confirm:
- Preview renders headings, tables, code blocks and task checkboxes.
- Clicking Source shows identical raw markdown in monospace.
- Clicking Preview returns to rendered view; content is unchanged.
- The left sidebar stays visible and the detail panel stays open throughout.

- [ ] **Step 9: Commit**

Use `commit_lease` (paths: the three modified files), stage only them via an isolated `GIT_INDEX_FILE`, commit, then `commit_release`.
Message: `feat(dashboard): markdown preview mode for docs viewer`

---

### Task 3: Record the viewer-mode convention

**Files:**
- Modify: `docs/superpowers/specs/2026-11-23-docs-markdown-viewer-modes-design.md` — set status to `Accepted`.

**Interfaces:**
- Consumes: the implemented feature from Task 2.

- [ ] **Step 1: Mark the spec accepted**

Change the `Status:` line from `Proposed` to `Accepted`.

- [ ] **Step 2: Commit the design doc**

Use `commit_lease` (path: the spec file), stage only it via an isolated `GIT_INDEX_FILE`, commit, then `commit_release`.
Message: `docs: accept markdown viewer modes design`
