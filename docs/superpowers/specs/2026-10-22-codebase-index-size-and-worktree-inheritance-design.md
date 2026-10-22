# Codebase Index Size & Worktree Index Inheritance — Design Specification

## 1. Overview

Two related improvements to the codebase indexing subsystem:

1. **Index size reporting**: every workspace card on the dashboard's Workspaces page shows the on-disk size of its codebase index (database file + WAL), and a "Total Index Size" tile rolls the whole set up.
2. **Worktree index inheritance**: a linked git worktree of an already-indexed repository builds its own index at startup without asking for consent, because the repository was indexed once, on purpose.

## 2. Index size

### 2.1 What is measured

`CodebaseIndexSize.Measure(dbPath)` returns the size of the database file plus its write-ahead log (`.db-wal`), and ignores the shared-memory sidecar (`.db-shm`), which is a transient index over the WAL and holds no data of its own. The WAL is counted because it holds committed writes that have not been checkpointed into the main file yet, so the dashboard and a directory listing agree.

A missing or zero-byte database file measures as zero. A blank path measures as zero.

### 2.2 Where it is reported

`CoordinatorController` computes `dbSizeBytes` from the file (not from the index), so a corrupt index still reports what it costs on disk — the number that decides whether the index is worth keeping. The value is carried on `ProjectCodebaseStatusDto.DbSizeBytes` and rendered on the Workspaces page beside the index file's name.

The frontend renders the size whenever the index file exists (`status.dbPath` is set and either `lastIndexedAt` is non-null or `dbSizeBytes > 0`), so a corrupt database still shows its cost. A "Total Index Size" tile sums the same set as "Total Indexed Files".

### 2.3 Display

Only the file name is printed (every index lives in the same global store, so the shared path prefix repeated on each card said the same thing thirty times over). The full path is one hover away. The size badge's tooltip reads "Index size on disk: the .db file plus its .db-wal".

## 3. Worktree index inheritance

### 3.1 Detection

`WorktreeIndexPolicy.ResolveRepositoryRoot(workspaceRoot)` asks `GitRepositoryResolver.ResolveRepository(workspaceRoot)` whether the workspace sits in a linked worktree (a `.git` FILE pointing at `<main>/.git/worktrees/<name>`, whose `commondir` names the main repository). It returns the main checkout's root when it is a linked worktree, and null otherwise (main checkout, subfolder of one, or not in a repository at all).

### 3.2 Decision

`WorktreeIndexPolicy.EvaluateAsync(workspaceRoot, isRepositoryIndexActive)` decides:

- `NotAWorktree` — the workspace is not a linked worktree; nothing changes.
- `Inherit` — the workspace is a linked worktree AND the main checkout's index holds at least one file; the worktree builds its own index straight away, with no consent question.
- `RepositoryIndexInactive` — the workspace is a linked worktree but the main checkout is not indexed; the worktree still waits for consent.

The main checkout itself inherits nothing — it is the root that decides.

### 3.3 Indexing

The worktree keeps its own database (one index per workspace root, exactly as every other workspace). It only skips the second consent question.

### 3.4 Excluded files

A worktree's `.git` is a FILE (not a directory), so the excluded-directory rule never sees it. `CodebaseIndexService.IsGitMetadataFile` skips any file named `.git` during indexing.

### 3.5 Agent instructions

The MCP server instructions (`ServerInstructions.cs`) carry two adjacent rules: the hard rule "never start indexing without consent" and the inheritance exception "a linked git worktree of a repository that is already indexed builds its own index at startup — no approval needed". A fresh worktree can show `fileCount 0` for the first seconds while its startup pass runs; the agent waits and re-reads instead of asking for approval.

## 4. Consent model

There is no committed spec for this feature prior to this document. The consent model is:

- A workspace that is not a linked worktree of an indexed repository is untouched and still waits for consent (the existing hard rule).
- A linked worktree of an indexed repository inherits the consent and builds its own index at startup.
- The inheritance is one-directional: the worktree inherits from the main checkout, never the reverse.

## 5. Known limitations

- `IsGitMetadataFile` matches any file named `.git` at any depth, not just the worktree root pointer. Harmless in practice but broader than the comment claims.
- The corrupt-index size fix (WorkspacesList.tsx) was verified by code inspection only; the frontend has no automated test runner.
- Feature A was shipped without a spec; this document is written retroactively.