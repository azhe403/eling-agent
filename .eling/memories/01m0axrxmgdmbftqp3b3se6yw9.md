---
id: 01m0axrxmgdmbftqp3b3se6yw9
type: fact
status: active
tags: 
created_at: 2026-08-18T17:12:44.9476026+00:00
updated_at: 2026-08-18T17:12:44.9476026+00:00
source:
---
Eling MCP server rootPath must always resolve to the .eling directory at project root. Fix in Program.cs uses Path.Combine with RepositoryRoot.Find and .eling suffix. All downstream consumers expect rootPath as the .eling dir for memories index.db and logs subdirectories. Testing override via --root-path flag works. Logs confirmed written to expected location.