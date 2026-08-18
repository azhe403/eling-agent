---
id: 01m01sy6ycnyxhk1pnnxsh930m
type: fact
status: active
tags:
- workflow
- git
- pre-commit
- husky
created_at: 2026-08-15T04:12:34.1249729+00:00
updated_at: 2026-08-15T04:12:34.1249729+00:00
source:
---
For the Eling project: git commits are gated by a husky pre-commit hook (.husky/) that runs `pnpm lint:frontend`, `pnpm typecheck:frontend`, `dotnet restore Eling.slnx`, and `dotnet test Eling.slnx`. All four must exit 0 or the commit aborts. The dotnet restore step prints NU1903 high-severity warnings for Microsoft.OpenApi 2.0.0 but they do not fail the gate. Plan for the full test suite to run once per commit when committing.