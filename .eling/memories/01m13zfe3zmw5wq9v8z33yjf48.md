---
id: 01m13zfe3zmw5wq9v8z33yjf48
type: fact
status: active
tags:
- eling
- dev-runner
- pnpm-dev
- workflow
created_at: 2026-08-28T10:43:32.0972769+00:00
updated_at: 2026-08-29T23:27:34.4853347+00:00
source:
---
Eling dev workflow runner: Run 'pnpm dev' from root workspace to concurrently launch Backend ASP.NET dashboard (port 4417 via cross-env ELING_DASHBOARD_PORT=4417 dotnet run) and Frontend Next.js Live Development server (port 4427 via pnpm dev). This isolates dev mode completely without touching staging (4317).