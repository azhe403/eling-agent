---
id: 01m18fah8jx6x0wtyafv36m9hc
type: preference
status: active
tags:
- engineering_discipline
- no_suppress
- fix_root_cause
- code_quality
- convention
- architecture
created_at: 2026-08-30T04:37:26.4187912+00:00
updated_at: 2026-08-30T04:37:26.4187912+00:00
source:
---
[Convention] Core Engineering Discipline — Fix Root Cause, Never Suppress:
1. Strict No-Suppress Rule: Never use @ts-ignore, @ts-expect-error, or eslint-disable comments to bypass compiler or linter errors.
2. Root-Cause Resolution: Every type error, lint warning, test failure, or runtime issue MUST be properly fixed at the architecture/code level using correct idioms (e.g. proper React state initializers, unbuffered streaming, explicit type guards).
3. Backend Authoritative: Data integrity issues (such as deduplication or ordering) must be resolved authoritatively in the backend service layer, never patched up as workarounds in the frontend.
4. Clean Code Hygiene: Maintain zero compiler warnings, zero linter suppressions, and zero leakage of machine-specific paths or personal names across the entire codebase.

Reference: AGENTS.md Engineering Discipline & Quality Standards.