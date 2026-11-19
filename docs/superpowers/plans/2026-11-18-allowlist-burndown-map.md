# Allowlist burn-down map (2026-11-18) — ✅ COMPLETE (2026-11-19)

> Goal (user, 2026-11-18): the allowlists in the convention tests are a transitional buffer, not a permanent mechanism — **they should not exist**. All four allowlists have been eliminated: every site converted, and the exception lists + their `NoNew/NoStale` companion tests deleted. `NoTuples_BeyondAllowlist` → `NoTuples`; `CatchBlocks_MustNotBeSilent` is a plain zero-exception scan. Verification: `core` 961/961, `api` 1261/1261 (2 fewer tests than the 1263 baseline — the two deleted NoStale tests), `desktop` 108/108. This file remains as the historical work order; the file lists and site tables below are the site-by-site record of what was converted and where (a few sites were converted that the original inventory missed — e.g. `MemoryService.cs:282` — the convention tests caught them).

Baseline audit at HEAD (2026-11-18, clean tree apart from unrelated WIP): **zero drift** — every allowlist entry still named a real file with the expected number of violations. Baseline commit message claimed "178 sites / 118 files"; the file today holds **177 sites across 117 allowlist entries** — minor drift between the memory note and the actual list, or one site had been quietly fixed since; no entry was silently fixed (all counts verified per file by the inventory agents).

## Allowlist inventory

| Allowlist | Where | Entries | Sites | State |
|---|---|---|---|---|
| `SilentCatchAllowlist` | `CodeOrganizationConventionTests.cs` | 117 | 177 | live; counts all verified |
| `TupleAllowlist` | `CodeOrganizationConventionTests.cs` | 12 | ~20 tuple sites | 1 entry **stale** (`MemoryService.cs` — no tuple shapes left, file-existence check only) |
| `MultiTypeAllowlist` | `CodeOrganizationConventionTests.cs` | 0 | 0 | already empty |
| `ProcessStartInfoAllowlist` | `CliWrapConventionTests.cs` | 5 | 3 real + ~1 false-positive | 1 entry **stale** (`FoldersViewModel.cs` — no `Process.Start`/`ProcessStartInfo` reference left); no `NoStale…` test exists for this list |

Context notes:

- Tests/ tuple guard does not scan `tests/`: ~113 tuple-ish matches across 18 test files sit outside the guard's scope. Widening it is separate work (needs the parenthesised-call pin first).
- The no-tuple guard's last pattern uses a negative lookahead; widening it without a pinning test risks inverting the exclusion.
- `NoStaleTupleAllowlistEntries` / `NoNewMultiTypeAllowlistEntries_AndNoStaleOnes` check file existence (+ type count for multi-type), not whether the content pattern is still present — so stale-but-present entries like `MemoryService.cs` pass silently. A content-based stale check would have caught it.
- When an allowlist reaches zero, the end state is: **delete the allowlist and the NoNew/NoStale companion tests; the guard tests stay as plain "no exceptions" assertions.**

## SilentCatch sites — by group (fix in this order)

### A. Tests: best-effort temp-dir teardown deletes — the bulk (~105 sites)

Every one of these is a `Directory.Delete` / `File.Delete` / process-kill in a `Dispose`/`finally`, deliberately swallowing `IOException` / `UnauthorizedAccessException` because the test should not fail on cleanup. None of the test files has `ITestOutputHelper` or a reachable logger (all `ILogger` occurrences are `NullLogger<T>.Instance` stubs for the SUT, not a test-side channel).

Suggested uniform fix: one `System.Diagnostics.Debug.WriteLine($"cleanup failed: {ex.GetType().Name}: {ex.Message}")` per site — **superseded during execution: everything was routed through shared MEL `ILogger` helpers (`TestLogs`) per the user's 2026-11-18 decision; `Debug.WriteLine` was rejected (compiles out in Release)**. For process-kill sites include the pid. `AppSettingsSourceResolverTests.cs:25` is one catch guarding two temp dirs — the line should name both.

Backend tests (36):

- AgentBuiltinMcpToolTests.cs:129 · AgentChatStreamTests.cs:143 · AgentChatTests.cs:182,194 · AgentHostBrowseTests.cs:78 · AgentStoreMigrationTests.cs:57 · AgentTurnPromptCompositionTests.cs:80 · AgentWorkspaceTests.cs:125
- CodebaseExclusionTests.cs:186,187,190,191 · CodebaseRebuildScopeTests.cs:185,188 (static `TryDelete` helper) · CodebaseStaleSweepTests.cs:76,77 · CodebaseUninitializedIndexTests.cs:26 · CodebaseWorkspaceRootTests.cs:155,175,206,207
- DashboardLifecycleTests.cs:38 (process kill), :55 (temp dirs), :96 (stdin close) · DatabaseDetectImportTests.cs:31 · DatabaseRegistryWatcherTests.cs:21 · Endpoints/StatsEndpointsTests.cs:117 · FileSystem/FileSystemServiceTests.cs:30 · HttpLoopConfigurationIsolationTests.cs:74 · JsonProjectScopePolicyStoreTests.cs:19 · JudgeEndpointsTests.cs:175 · Logging/LogConfigStoreTests.cs:166 · Logging/LogFormatForwardingSinkTests.cs:25,26 · Logging/LogFormatSwitcherTests.cs:64 · Logging/RollingDailyFileSinkJsonlTests.cs:31 · LoggingTests.cs:31 · Mcp/ToolPolicyFilterTests.cs:193 · Mcp/ToolsPolicyToolTests.cs:151 · McpHostConfigurationIsolationTests.cs:169 · McpProcessTests.cs:33,41 · MeaiGatewayTests.cs:152,164 · MemoryApiTests.cs:85,94 · MemoryMaintenanceApiTests.cs:48,49,56 · MemoryProjectScopePolicyToolTests.cs:20 · MemoryProjectToolsTests.cs:25 · MemoryProvenanceDtoTests.cs:125 · MemoryWriteToolProjectScopeTests.cs:23 · RecallToolCodebaseTests.cs:19 · RecallToolOverflowSummaryTests.cs:25 · RecallToolProjectScopeTests.cs:21 · RecallToolTopicAliasTests.cs:26 · Pecut10VerificationTests.cs:233,234 · PeerMemoryNotifyTests.cs:81,90 · ProjectContextNoAutoCreateTests.cs:33,34 · ProjectIdentityLoggingTests.cs:120 · ProjectMemoryChainEndpointTests.cs:69,77 · ProjectScopeCatalogTests.cs:28 · RuntimeDirWatcherTests.cs:127 · RuntimeMetricsSamplerTests.cs:111 · RuntimeRegistryWorkspacePruneTests.cs:284,285,309 · RuntimeRegistryWorkspaceRecordingTests.cs:126,127,137 · ScopeChainDiTests.cs:28 · ScopeChainSmokeTests.cs:32 · SemanticJudgeStoreTests.cs:30 · Shell/ShellConfigStoreTests.cs:344 · Shell/ShellToolsTests.cs:403 · ShellAgentToolTests.cs:191 · TestProcesses.cs:123,129,144 · Tools/ToolPolicyStoreTests.cs:93 · Updates/FileUpdateCacheTests.cs:27 · Updates/UpdateCheckerTests.cs:33 · Updates/UpdateNoticeBlockTests.cs:42 · ZombieProcessCleanerTests.cs:40,75

Core tests (20):

- AppSettingsConnectionScannerTests.cs:26 · AppSettingsSourceResolverTests.cs:25 · CacheFileGateTests.cs:153 · CodebaseIndexSizeTests.cs:25,29 · CodebaseSiblingReadOnlyFirstTests.cs:40 (after `SqliteConnection.ClearAllPools()`) · DatabaseReadServiceCacheTests.cs:171 · JsonDatabaseReadCacheTests.cs:168 · JsonRedisReadCacheTests.cs:293 · JsonWorkspaceRegistryTests.cs:313 · LogReaderJsonlTests.cs:31 · LogReaderTests.cs:18 · ProjectScopeDiscoverChainTests.cs:21 · ProjectScopeTests.cs:21 · RedisKeyReadServiceOfflineTests.cs:197 · RedisScanCommandTests.cs:36 · ScopeChainTests.cs:17 · TempWorkspace.cs:39,43 · UserScopeTests.cs:45

### B. Tests: expected-control-flow probes — log at Trace (5 sites)

Not teardown; logging would be arguably noise, but the rule is absolute, so Trace/Debug one-liners:

- `RuntimeDirWatcherTests.cs:117` — poll timeout, `catch (OperationCanceledException)` returns null
- `TestProcesses.cs:170` — free-port probe, occupied → next candidate
- `TestProcesses.cs:240` — `/health` poll loop until backend responds
- `DatabaseRegistryWatcherTests.cs:106`, `:108` — `OperationCanceledException` drain of pending `MoveNextAsync` / `enumerator.DisposeAsync()` in a `finally`

### C. src: watcher/poll OCE "superseded/shutdown" sites — log at Debug (13 sites)

Expected cancellation, indistinguishable from real errors today. `LogDebug` where a logger is in scope.

- `AuditDirWatcher.cs:145` (logger in scope) · `CodebaseDirWatcher.cs:114` (yes) · `CodebaseWatcherService.cs:107`, `:313` (`_logger?`) · `DatabaseRegistryWatcher.cs:114` (yes) · `RuntimeDirWatcher.cs:110` (yes) · `HttpLoop.cs:236` (no logger — static/jitter delay) · `RuntimeMetricsBroadcaster.cs:106`, `:123` (yes) · `UpdateCheckerService.cs:49` (yes), `:71` (no — static) · `RuntimeRegistry.cs:99`, `:126`, `:144`, `:157`, `:191`, `:926` (all `_logger`)

### D. src: fail-open / audit / flush sites — Debug, Warning only where loss is invisible (11 sites)

- `BufferedAuditLogger.cs:195`, `:249`, `:262` — Debug (audit drain/settle/flush-timeout, optional-visibility) · `:228` — **Warning** (entries not persisted, silent loss otherwise)
- `DisposableAuditLogger.cs:35` — Debug/Warning (tail lost at sync dispose)
- `SqliteAuditIndex.cs:100` (duplicate-column migration skip), `:330` (incremental_vacuum best-effort) — Debug
- `LogFormatSwitcher.cs:296`, `:308` — Debug (watermark write/delete; static, no logger)
- `RedisConnectionPool.cs:46`, `:58`, `:85`, `:98`, `:111` — Debug (stale/evicted/invalidated multiplexer dispose; no logger)
- `ScopedMemoryService.cs:456`, `:682` — **Warning** or Debug (audit before-state read failed, delete/update proceeds)
- `RuntimeRegistry.cs:157` counted above

### E. src: static methods with no ILogger in scope — each needs either an optional `ILogger? = null` param (repo's `_logger?` pattern) or `Debug.WriteLine`

`DashboardPort.cs:56` · `HttpLoop.cs:236` · `RuntimeSelfRegistration.cs:38` · `LogFormatSwitcher.cs:296,308` · `BufferedAuditLogger.cs` (4) · `SqliteAuditIndex.cs` (2) · `AppSettingsConnectionScanner.cs:286,312,338` (classification probes) · `CanonicalProjectRoot.cs:31` · `CentralAgentDirectory.cs:19,32` · `CentralAuditDirectory.cs:21` · `CentralLogDirectory.cs:20` · `HttpCoordinatorMemoryChangeNotifier.cs:40` · `RedisConnectionPool.cs` (5) · `ScopedMemoryService.cs:456,682` · `CoordinatorController.cs:296,334,339,356,359` (controller, no ILogger) · `DisposableAuditLogger.cs:35`

### F. src: parse/config fallbacks — Debug (with logger where in scope) (5 sites)

- `AppSettingsConnectionScanner.cs:286,312,338` (covered in E — static `Classify`) · `MemoryReadTool.cs:95` (`_logger?` — uninitialized project scope → global fallback) · `MemoryWriteTool.cs:168` (`_logger?`) · `SqliteAuditIndex.cs:100` (counted in D) · `CanonicalProjectRoot.cs:31` (static)

### G. src: desktop (6 sites)

- `MessageBlock.cs:38` — fire-and-forget clipboard write; plain class, needs logger or `Debug.WriteLine`
- `BackendSupervisor.cs:190` — kill spawned backend at Dispose; `logger` (primary-ctor) **in scope**
- `ElingApiClient.cs:924` — malformed SSE `data:` payload skipped; `_logger` **in scope**
- `FoldersViewModel.cs:290` — filter superseded/cancelled; `_logger` in scope
- `MainViewModel.cs:606` — prior tree load superseded by mode switch; `_logger` in scope
- `MainWindow.axaml.cs:221` — save window bounds fail-safe; no logger — add or `Debug.WriteLine`

### Already-non-silent near-misses (verified by inventory; do NOT "fix")

`BackendSupervisor.cs:85`, `MainWindow.axaml.cs:191` (return false/true) · `FileSystemServiceTests.cs:493` (rethrow) · `MemoryApiTests.cs:157,175` (retry with `Thread.Sleep`) · `JudgeEndpointsTests.cs:134` (`break;`) · `LogFormatForwardingSinkTests.cs:132,142` (`exceptions.Add`) · `CoordinatorController.cs:181,316` · `RuntimeRegistry.cs:574,937,1129,1179` · `MemoryReadTool.cs:127,178,216` · `MemoryWriteTool.cs:184,254` (log+throw) · `AppSettingsConnectionScanner.cs:387,418` · `WorkspaceExpansionStore.cs:106` · `CanonicalProjectRoot.cs:52` · `RuntimeMetricsBroadcaster.cs:106` is in list (real) vs `:123` (real) · `LogFormatSwitcher.cs:284,332` · `DashboardPort.cs:81`

## Other allowlists — burn-down notes

### TupleAllowlist (12 files, ~20 sites) — named records, then empty the list

- `SqliteMemoryIndex.cs:267,274` — `foreach (var (id, score) in …)` (porter + trigram) → record or named fields
- `ScopedMemoryService.cs:750,756,783,869` — `return (_levels[0].Service, true)` etc.
- `IntentionTriggerMatcher.cs:11,19` — `return (false, true)` / `(matched, false)`
- `RecallService.cs:77` — `var (matched, isExpired) = Match(...)`
- `RollingDailyFileSink.cs:268` — `foreach (var (dateKey, groupLines) in …)`
- `RuntimeRegistry.cs:777,840` — `foreach (var (runtime, service) in GetAliveProjectServices())`
- `FileSystemService.cs:200,641,936,941` — deconstruction + `return ("", start - 1, total)`
- `BackendFileTools.cs:72` — `return (text, truncated)`
- `MeaiChatGateway.cs:227,232,248` — tuple literal assignment into `entry` + deconstruction
- `AgentChatEndpoints.cs:73` — `var (kind, payload) = ev switch`
- `AgentWorkspaceEndpoints.cs:97` — `var (content, truncated) = tools.ReadText(full);`
- **`MemoryService.cs` — STALE: no tuple sites remain. Remove entry directly.**
- Enforcement: `NoStaleTupleAllowlistEntries` (file-existence only) → tighten to also fail when the file no longer matches `TuplePatterns`, then delete both once the list is empty.
- Widening the guard to `tests/` (~113 tuple-ish sites / 18 files) stays out of scope until the parenthesised-call pin exists; tracked here so it isn't lost.

### ProcessStartInfoAllowlist (5 files) — real migrations or documented carve-outs

- **`FoldersViewModel.cs` — STALE: no `Process.Start`/`ProcessStartInfo` reference. Remove entry.** (No stale-check test exists for this list — add one or rely on eyeball.)
- `FrontendDevSpawner.cs:573` — real (the documented probe carve-out, `System.Diagnostics.Process.Start(new ProcessStartInfo…)`); `:442` is a false positive (`configureProcessStartInfo:` parameter name, not the API). Either convert `:573` to CliWrap or keep the carve-out with its stated reason — conversion is the stated goal of the allowlist being a buffer.
- `BackendSupervisor.cs:105,153` — real (`new ProcessStartInfo` + `Process.Start`); migrates to CliWrap or needs the same treatment.
- `TestProcesses.cs:181,220` — real (test helper spawning processes); migrate or keep documented.
- `MemoryProjectToolsTests.cs:159,166` — real (`git init` fixture); migrate or keep documented.
- Enforcement: there is no `NoStaleProcessStartInfoAllowlistEntries` test — add one mirroring the tuple one (file-existence + content match), then delete `CliWrap`'s companion once empty.

## Suggested order (all done)

1. **A+B (tests slice)** — ✅ done 2026-11-19: ~115 sites filled, all `tests/…` entries removed from `SilentCatchAllowlist` (80 entries), MEL ILogger via shared `TestLogs` helpers (user decision: `Debug.WriteLine` rejected — compiles out in Release).
2. **C+D+F (src, logger in scope)** — ✅ done 2026-11-19 (21 sites).
3. **E (src, no logger in scope)** — ✅ done 2026-11-19: optional `ILogger? = null` / `ILogger<T>?` params per the Core pattern (DI-wired for `BufferedAuditLogger`, `DisposableAuditLogger`, `SqliteAuditIndex`); statics (central dirs, `CanonicalProjectRoot`, `AppSettingsConnectionScanner`, `DashboardPort`, `HttpLoop`, `RuntimeSelfRegistration`) got optional `ILogger?` params threaded one hop where needed; DI wired for `DisposableAuditLogger` + `BufferedAuditLogger` in `McpServiceExtensions`.
4. **G (desktop)** — ✅ done 2026-11-19: 5 of 6 sites via `_logger`/`ILogger?`; `MessageBlock.cs` resolves `ILogger<MessageBlock>` from `App.Services` (same pattern the clipboard path already used to reach `Application.Current`).
5. **TupleAllowlist** — ✅ done 2026-11-19: named records (`FtsRank`, `ScoredCandidate`, `SearchFields`, `ResolvedTargetService` dropped entirely — `IsCurrentHead` was never consumed —, `AliveProjectService`, `NormalizedNewlines`, `LineSlice`, `ReadTextResult`, `PendingToolCall`, SSE frames via `SSEEventFrame` in `Endpoints/`). `MemoryService.cs`'s tuple site (missed by the original inventory) caught and fixed by `NoTuples` itself.
6. **ProcessStartInfoAllowlist** — ✅ done 2026-11-19: `FoldersViewModel.cs` entry removed (stale); netstat probe → CliWrap (async chain); `BackendSupervisor` backend spawn + `TestProcesses.Start` fixture → CliWrap (with new `TestProcess` record + blocking `TestStdinStream` for MCP handshake; `McpProcessTests`/`DashboardLifecycleTests` updated); `git init` → CliWrap. Desktop gained `CliWrap` 3.10.5.
7. **Enforcement endgame** — ✅ done 2026-11-19: all allowlists + NoNew/NoStale companion tests deleted; `NoTuples_BeyondAllowlist` → `NoTuples`; `CatchBlocks_MustNotBeSilent` simplified and sorted. Verified green across core/api/desktop.
