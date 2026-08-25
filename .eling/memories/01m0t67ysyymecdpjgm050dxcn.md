---
id: 01m0t67ysyymecdpjgm050dxcn
type: note
status: active
tags:
- vestige
- dedup
- tooling
created_at: 2026-08-24T15:29:22.7522515+00:00
updated_at: 2026-08-24T15:29:22.7522515+00:00
source:
---
For Eling project tooling (Vestige MCP): dedup merge apply is asynchronous — apply stamps valid_until immediately but recall/dedup-scan keep showing old memories until the background scheduler processes events. Run vestige_maintain consolidate after applying merges; remaining duplicate clusters resolve over subsequent cycles.