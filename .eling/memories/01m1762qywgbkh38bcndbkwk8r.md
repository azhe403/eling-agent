---
id: 01m1762qywgbkh38bcndbkwk8r
type: decision
status: active
tags:
- bug_observation
- compliance_fix
- project_hygiene
- path_resolution
- architecture
- patched
created_at: 2026-08-29T16:36:39.5218614+00:00
updated_at: 2026-08-30T03:39:51.0234384+00:00
source:
---
[Bug Discovery - PATCHED] 

Anomali Folder .eling di User Home. Akar masalah: McpLoggingExtensions.cs:18 (default rootPath = ".eling" relatif) + di-resolve ke CWD proses yang tidak deterministik. Patch sudah diterapkan: Path.GetFullPath(rootPath) memastikan logsDirectory selalu absolute path. Caller di Eling.Host/Program.cs:35 selalu pass projectScope.DataDirectory (yang sudah absolute via ProjectScope ctor). Tests: 119/119 backend tests PASSED, frontend build OK. Verifikasi: orphan folder masih ada dan di-lock oleh residual eling processes (akan hilang setelah restart bersih).