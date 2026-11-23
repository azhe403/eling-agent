# Docs Viewer — Markdown Preview & Source Modes

Status: Implemented

## Implementation note (2026-11-23)

Shipped across `7ed7f2d` (deps), `63ae75a` (viewer), `376e1d4` (front-matter),
`0ef5d07` (header copy buttons). Deviations from the original Design below,
recorded here so the doc matches the code:

- `react-markdown` v10 removed the `className` prop. The renderer is wrapped in
  `<div className="md-preview">` instead of receiving `className` directly.
- No reset effect was needed: `DocsList.tsx` keys the panel by document id, so
  opening a different document remounts it and `viewMode` returns to `preview`.
- **YAML front-matter is stripped on the Preview path only.** The read endpoint
  returns the raw file; remark rendered the leading `--- … ---` block as a
  setext `<h2>`, showing metadata as prose. `stripFrontMatter()` now removes it
  before `<ReactMarkdown>`; Source keeps the full raw document including
  front-matter.
- The detail header was restructured: full ID on the top row, then badges and
  title, then a metadata list (`path`, `topic`, `updated`, optional
  `superseded`) one field per line. Each string field has its own copy button
  using the shared `copyText()` helper (`lib/clipboard.ts`), which handles
  insecure-context fallback.

## Intent

The docs detail panel's **Content** tab currently shows only raw markdown in a
monospace `<pre>`. Give it two view modes: a rendered **Preview** and the
existing raw **Source**, so clicking a card lets the reader actually read the
document the way it was meant to look, while keeping the honest source one
click away.

## Goals

- Two modes inside the Content tab: **Preview** (rendered markdown) and
  **Source** (the current monospace raw text).
- Preview defaults to **Preview**; the choice is local panel state and resets
  when a different document is opened.
- GitHub-flavored markdown works: headings, lists, tables, task checkboxes,
  fenced code, links, blockquotes, strikethrough.
- Rendering is safe: raw HTML in the markdown is escaped, never injected.

## Non-goals

- Syntax highlighting for code fences — plain styled `<pre>` is enough.
- Mermaid / diagrams, math, or embedded media.
- Editing from the preview; the Source mode stays read-only here too.
- A markdown renderer shared with other subsystems. This is local to the docs
  viewer, consistent with the deliberate Docs/Skills separation.

## Constraints

- Next.js 16 / React 19 dashboard; no markdown renderer or typography plugin
  is currently installed (`react-markdown`, `remark-gfm`, and
  `@tailwindcss/typography` are all absent).
- The detail column is ~420px wide on wide screens; the toolbar must fit.
- Frontend verification uses `tsc --noEmit` + vitest, never `next build`
  while the dev server is on 4427.

## Approaches

- **A — `react-markdown` + `remark-gfm` (chosen).** One well-maintained pair of
  dependencies; GFM tables/task-lists/links render correctly; raw HTML is
  escaped by default, so no XSS surface. Cost: two new packages.
- **B — hand-rolled minimal renderer.** No dependencies, but heading/bold/code/
  link/table parsing must be written and maintained by hand. Docs here are
  table-heavy, so this path drops content or renders it wrong. Rejected.

Styling call: rather than add a third dependency (`@tailwindcss/typography`),
style the rendered output with a small scoped CSS block in `globals.css`
(`.md-preview` targeting `h1`–`h4`, `p`, `ul`/`ol`, `table`, `pre`/`code`,
`blockquote`, `a`). This keeps control over the look and avoids another package.

## Design

- Install `react-markdown` and `remark-gfm` in the dashboard package.
- In `DocDetailPanel.tsx`, add `const [viewMode, setViewMode] = useState<"preview" | "source">("preview")`. It is declared among the unconditional hooks before all early returns; there is no reset effect because the panel is keyed by document id in `DocsList.tsx`.
- In `renderContentTab`, keep the unavailable/empty guards. For real content,
  render a small segmented control (`Preview | Source`) above the scroll area,
  then either `<div className="md-preview"><ReactMarkdown remarkPlugins={[remarkGfm]}>{stripFrontMatter(content)}</ReactMarkdown></div>` or the existing `<pre>` block with the unmodified `content`.
- Add `.md-preview` styles to `globals.css` using the existing theme tokens
  (`--border`, `--muted-foreground`, `--primary`, etc.) so it follows light/dark.
- Keep the existing four top-level tabs unchanged.

## Verification

- `pnpm exec tsc --noEmit` and vitest for the docs panel pass.
- In the browser at 4427: open a table-heavy doc, confirm Preview renders the
  table and code block, switch to Source and back, confirm content is identical
  and the sidebar/detail stay open.
