# Eling Dashboard (frontend)

Next.js (App Router) user interface for the Eling memory dashboard. It lives
inside the Eling monorepo and is **not a standalone/deployable web app** — it is
built to static files and served from within the `Eling.Dashboard` ASP.NET Core
host (loopback only).

## What it is

The dashboard UI talks to the dashboard's HTTP API on `127.0.0.1:4317` to let
users browse and manage Eling memories across scopes:

- `All Open Projects` — aggregated memories from every alive project runtime
- `Global` — user-wide memories under `~/.config/eling`
- per-project — memories for one project runtime

Routes:

- `/` → landing/redirect
- `/dashboard` → dashboard home
- `/dashboard/memories` → list/search/edit/promote/copy memories
- `/dashboard/create` → create a new memory

## How it is built & served

Unlike a normal `create-next-app` project, the frontend is **not** deployed
separately and is not served by `next start`.

1. The backend `Eling.Dashboard` project drives the build and install
   automatically through an MSBuild target (`BuildDashboard` in
   `Eling.Dashboard.csproj`). When the backend builds, it runs:

   ```bash
   pnpm install
   pnpm build
   ```

   and copies the resulting static output (`out/`) next to the dashboard binary
   as `eling-dashboard-ui/`. Set `ElingSkipDashboard=true` to skip this during
   backend builds.

2. `Eling.Dashboard/Program.cs` serves that folder as its static web root
   (`WebRootPath = eling-dashboard-ui`) with a SPA fallback to `index.html`.

So to rebuild the frontend you can either build the backend, or run it directly:

```bash
pnpm install
pnpm build
```

## Development

This dashboard is a **static Next.js export** (`next.config.ts` sets
`output: "export"`) with no dev-server proxy configured. Pages call the
dashboard API with same-origin relative paths (e.g. `fetch("/api/...")`), so
they resolve correctly only when served from the `Eling.Dashboard` host (which
serves both the API and the static UI on the same origin `127.0.0.1:4317`).

To preview the real UI, start an `eling` / `eling-dashboard` instance and open
`http://127.0.0.1:4317`. If you run `pnpm dev` (Next on its own port), the UI
renders but its same-origin API calls will not reach the dashboard unless you
wire up a proxy/rewrite yourself.

## Tech notes

- **Package manager**: pnpm (see `package.json` `packageManager`). Use pnpm,
  not npm/yarn/bun.
- **Next.js 16** (App Router, Turbopack). This version has breaking changes
  versus older Next.js — read `node_modules/next/dist/docs/` before writing
  frontend code (see `AGENTS.md` in this directory).
- **shadcn/ui** + **Tailwind CSS v4**.
- Source lives under `src/app/`; shared UI components under `src/components/`.

## Integrations / scripts

- Root `AGENTS.md` and root `package.json` wire `build:frontend` /
  `typecheck:frontend` / `lint:frontend` via
  `pnpm --prefix src/frontend/Eling.Dashboard ...`.
- The `Eling.Dashboard` MSBuild target builds the frontend automatically when
  the backend is built (see the "How it is built & served" section above), so
  repo-level validation scripts that `dotnet build` the solution pick it up too.
