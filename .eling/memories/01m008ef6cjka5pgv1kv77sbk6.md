---
id: 01m008ef6cjka5pgv1kv77sbk6
type: preference
status: active
tags:
- build
- test
- artifacts
- dotnet
created_at: 2026-08-14T13:47:38.0767831+00:00
updated_at: 2026-08-14T13:47:38.0767831+00:00
source:
---
Always run backend build and test commands with '--artifacts-path .artifacts' (e.g. dotnet build Eling.slnx --artifacts-path .artifacts and dotnet test Eling.slnx --artifacts-path .artifacts) to prevent binary locking with running server instances.