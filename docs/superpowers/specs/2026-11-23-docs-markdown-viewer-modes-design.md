# Docs Viewer — Markdown Preview & Source Modes

Status: Accepted

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
- In `DocDetailPanel.tsx`, add `const [viewMode, setViewMode] = useState<"preview" | "source">("preview")` and reset it to `"preview"` when `currentDoc.id` changes.
- In `renderContentTab`, keep the unavailable/empty guards. For real content,
  render a small segmented control (`Preview | Source`) above the scroll area,
  then either `<ReactMarkdown remarkPlugins={[remarkGfm]} className="md-preview">…</ReactMarkdown>` or the existing `<pre>` block.
- Add `.md-preview` styles to `globals.css` using the existing theme tokens
  (`--border`, `--muted-foreground`, `--primary`, etc.) so it follows light/dark.
- Keep the existing four top-level tabs unchanged.

## Verification

- `pnpm exec tsc --noEmit` and vitest for the docs panel pass.
- In the browser at 4427: open a table-heavy doc, confirm Preview renders the
  table and code block, switch to Source and back, confirm content is identical
  and the sidebar/detail stay open.
