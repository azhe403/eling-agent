# Task Graph Replacement — Design

Date: 2026-10-11
Status: draft (phase 3, after sessions + claims stabilize)
Goal: replace `.tmp/tasks/<feature>/task.json + subtask_NN.json` with structured tasks in Eling.

## Scope
- Source of truth: project-local for live status (pending/in_progress/completed), `depends_on` edges, `parallel` flags.
- Durable summaries only: promoted to project memory; raw large detail stays in runtime when big.
- Transition: `.tmp/tasks/` stays as a mirror until every writer moves over.

## Model
- `task_id`, `feature`, `session_id`, `status`, `depends_on[]`, `parallel`, `deliverables[]`, `updated_at`.
- Status updates must be atomic per subtask; two chats must not blindly complete the same subtask.

## Tools (new, minimal)
- `task_create(feature, session_id, subtasks[])` → create the initial graph.
- `task_update(subtask_id, status, deliverables)` → atomic update + session heartbeat.
- `task_get(feature)` / `task_list(session_id)` → read combined status.
- `task_close(feature)` → lock as done.

## Execution rules
- Parallel batches only for subtasks with no unsatisfied dependencies.
- Validate each batch before starting the next one (keep the existing incremental pattern).

## Validation
- One feature with 3 subtasks across two chats: no double-claimed subtask, consistent final status.
