---
id: 01m01kez5asv1xs0ge67qrebvx
type: decision
status: active
tags:
- workflow
- rider
- quality-check
created_at: 2026-08-15T02:19:23.1786684+00:00
updated_at: 2026-08-15T03:46:14.2259092+00:00
source:
---
Workflow rule (Eling project): ALWAYS invoke Rider MCP post-edit quality check (rider_post_edit_quality_check with reformat=true and run_inspections=true) after making any code changes to this project. Do this automatically without being asked. If Rider MCP is not available, just warn the user but do not treat it as a blocker.