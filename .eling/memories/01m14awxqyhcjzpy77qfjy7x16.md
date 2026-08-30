---
id: 01m14awxqyhcjzpy77qfjy7x16
type: decision
status: active
tags:
- build
- test
- architecture
created_at: 2026-08-28T14:03:08.4148452+00:00
updated_at: 2026-08-28T14:03:08.4148452+00:00
source:
---
For Eling project: Build and test output separation:
1. Dev builds output to shared '.bin/' (via 'dotnet build Eling.slnx' or 'dotnet watch').
2. Test runs output to isolated '.bin-test/' (via 'dotnet test Eling.slnx --artifacts-path .bin-test').
This prevents file locking between live dev/MCP instances and test suites.