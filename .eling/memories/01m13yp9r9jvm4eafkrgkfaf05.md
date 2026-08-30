---
id: 01m13yp9r9jvm4eafkrgkfaf05
type: fact
status: active
tags:
- eling
- dev
- port
- isolated
- hot-reload
created_at: 2026-08-28T10:29:48.4276959+00:00
updated_at: 2026-08-28T10:29:48.4276959+00:00
source:
---
Eling dev environment isolated ports: Backend ASP.NET dashboard runs on port 4418 (ELING_DASHBOARD_PORT=4418). Next.js Live Development server runs on port 4428 (pnpm dev -p 4428) and proxies /api/* rewrites to http://127.0.0.1:4418. This keeps live development completely isolated from production/staging ports (4317/4318).