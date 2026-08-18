---
id: 01m0aqgngb2xdbz46v406ypshx
type: preference
status: active
tags:
- project-convention
- git-commit
- husky
created_at: 2026-08-18T15:23:23.0193401+00:00
updated_at: 2026-08-18T15:23:23.0193401+00:00
source:
---
For the Eling project: git commits are gated by a husky pre-commit hook (.husky/) running lint:frontend, typecheck:frontend, dotnet restore, and the f