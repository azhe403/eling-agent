# Eling Agent Workspace Instructions

## Build backend
dotnet build Eling.slnx

## Run backend tests
dotnet test Eling.slnx

## Install frontend deps
pnpm --prefix src/frontend/Eling.Dashboard install

## Build frontend
pnpm --prefix src/frontend/Eling.Dashboard build

## Git Workflow

The agent must treat Git commits as user-controlled checkpoints.

Default workflow:

1. Implement the requested change.
2. Run relevant tests and validation.
3. Report the changed files and validation results.
4. Stop and wait for the user.

Rules:

* Do NOT run `git commit` unless the user explicitly asks for a commit.
* Do NOT run `git push` unless the user explicitly asks for a push.
* Do NOT amend commits unless explicitly requested.
* Do NOT reset, rebase, or rewrite Git history unless explicitly requested.
* Do NOT use force push.
* Do NOT create commits merely because a task is complete.
* Do NOT modify unrelated files just to create a clean commit.
* Leave completed work in the working tree so the user can review it.
* Before stopping, report `git status --short` and summarize the changes.

The user decides when a checkpoint is committed.
