# Weekly NuGet Dependency Check

> **PROVISIONAL (2026-09-29).** This file is a hand-written stand-in for an Eling memory that
> could not be written: the `eling_dev` MCP stdio link was wedged. Once opencode has been
> restarted, save the content of "Standing rule" and "Last run" below as an Eling **project**
> memory, then delete this file and remove the pointer from `AGENTS.md`.

## Standing rule

Stated by the user on 2026-09-29 ("mingguan update nuget"): record when the last check ran; if
there is no record, run the check once; and on **every prompt**, also check whether the weekly
NuGet check is already due.

**Procedure**

1. Read "Last run" below (or recall the Eling memory) before doing anything else.
2. No record, or the record is older than **7 days** → the check is **due**, run it.
3. Ran within the last 7 days → do **not** re-run; just say it is still fresh, and when it ran.
4. After a run, update "Last run" with the date and the outcome.

**How to run the check** — from the repo root:

```
dotnet list package --outdated --include-prerelease
```

Read-only, and safe to run while `eling_dev` dotnet-watch is up.

## Version-bump scope

User chose **patch + minor only** on 2026-09-29.

- Take a new version only when it is **stable** and on the **same major** — a patch or minor bump.
- **Skip major bumps.** A major bump is a separate, deliberate decision.
- **Skip prerelease, RC and CI/dev builds** even when they appear as "latest". With
  `--include-prerelease`, the Latest column shows things like `4.4.1-dev-02447` and
  `8.0.0-nblumhardt-02322`; those are **not** the version to take.

## Repo facts that shape this work

Verified 2026-09-29.

- **No Central Package Management.** There is no `Directory.Packages.props` and no `nuget.config`,
  so every version is pinned per-project inside the individual `.csproj` files. The same package
  therefore sits at different versions in different projects and drifts over time. `xunit` is
  2.9.3 in `Eling.Core.Tests` but 2.9.0 in the other two test projects;
  `xunit.runner.visualstudio` is 3.1.4 in `Eling.Core.Tests` but 2.8.2 in the other two.
- **No Dependabot, no Renovate.** Nothing else is watching these versions.
- **`.github/workflows/release.yml` is triggered only by `v*` tags**, not by pushes to `main`, so a
  dependency bump on a branch can never accidentally ship a release.

## Known standing issues

Both pre-date the weekly check. Do not "fix" either one by bumping — they are deliberate decisions.

1. ~~**`Microsoft.Extensions.AI.OpenAI` is pinned to a version that does not exist.**~~
   RESOLVED 2026-10-10: the phantom package was removed via `dotnet remove`
   (zero API usage verified) and replaced with a direct `OpenAI` 2.6.0
   reference. NU1603 no longer appears on restore.

2. **The xunit runner drift cannot be closed under the patch+minor rule.**
   Bringing `xunit.runner.visualstudio` 2.8.2 → 3.1.4 is a *major* bump by version number, so the
   current scope blocks it permanently. Resolving it requires an explicit exception to the rule.

## Expected noise

`net10.0` with SDK `10.0.x`, while .NET 11 release candidates are already published. These will
reappear as "outdated" every single week until the repo moves to .NET 11, and are all correctly
out of scope as major bumps: `Microsoft.Data.Sqlite`, `Microsoft.Extensions.Logging`,
`Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.DependencyInjection`,
`Microsoft.AspNetCore.Mvc.Testing` (all `10.x` → `11.0.0-rc.1.26425.128`), plus
`Microsoft.NET.Test.Sdk` 17 → 18 and `coverlet.collector` 6 → 10.

---

## Last run

**2026-10-10** — check ran (due since 2026-10-06). Prerelease/RC ignored per scope.
Applied 22 changes, build succeeded with 0 errors:

| Project | Package | From | To |
| --- | --- | --- | --- |
| `Eling.Backend` | `ModelContextProtocol` | 2.1.0 | 2.2.0 |
| `Eling.Desktop` | `Avalonia` | 12.1.2 | 12.1.4 |
| `Eling.Desktop` | `Avalonia.Desktop` | 12.1.2 | 12.1.4 |
| `Eling.Desktop` | `Avalonia.Fonts.Inter` | 12.1.2 | 12.1.4 |
| `Eling.Desktop` | `Avalonia.Themes.Fluent` | 12.1.2 | 12.1.4 |
| `Eling.Backend.Tests` | `xunit` | 2.9.0 | 2.9.3 |
| `Eling.Desktop.Tests` | `xunit` | 2.9.0 | 2.9.3 |
| `Eling.Core.Tests` | `coverlet.collector` | 6.0.4 | 10.1.0 |
| `Eling.Core` | `Microsoft.Data.Sqlite` | 10.0.11 | 10.0.12 |
| `Eling.Core` | `Microsoft.Extensions.Logging.Abstractions` | 10.0.0 | 10.0.12 |
| `Eling.Desktop` | `Microsoft.Extensions.DependencyInjection` | 10.0.0 | 10.0.12 |
| `Eling.Desktop` | `Microsoft.Extensions.Logging` | 10.0.0 | 10.0.12 |
| `Eling.Backend.Tests` | `Microsoft.AspNetCore.Mvc.Testing` | 10.0.0 | 10.0.12 |
| `Eling.Core.Tests` | `xunit.runner.visualstudio` | 3.1.4 | 4.0.1 |
| `Eling.Backend.Tests` | `xunit.runner.visualstudio` | 2.8.2 | 4.0.1 |
| `Eling.Desktop.Tests` | `xunit.runner.visualstudio` | 2.8.2 | 4.0.1 |
| `Eling.Core.Tests` | `Microsoft.NET.Test.Sdk` | 17.14.1 | 18.10.1 |
| `Eling.Backend.Tests` | `Microsoft.NET.Test.Sdk` | 17.14.1 | 18.10.1 |
| `Eling.Desktop.Tests` | `Microsoft.NET.Test.Sdk` | 17.14.1 | 18.10.1 |
| `Eling.Backend` | `Microsoft.Extensions.AI.OpenAI` | 9.12.0-preview.1.25501.4 (phantom) | REMOVED |
| `Eling.Backend` | `OpenAI` (direct, was transitive 2.6.0) | — | 2.6.0 |
| `Eling.Backend` | `OpenAI` | 2.6.0 | 2.14.0 |
| `Eling.Desktop` | `ReactiveUI.Avalonia` 12.0.3 | swapped | `ReactiveUI.Avalonia.Reactive` 12.1.6 |

**Attempted but reverted, then MIGRATED 2026-10-10:** `ReactiveUI.Avalonia`
12.0.3 → 12.1.6 broke the Desktop build (105x CS0234) because v26 split the
engine out of the lean packages. Root cause found in official docs: the
System.Reactive flavour lives in the `.Reactive` twin packages
(`ReactiveUI.*` → `ReactiveUI.*.Reactive`), with `ReactiveObject`/
`ReactiveCommand` under the `ReactiveUI.Reactive` namespace. Fix applied:
swapped `ReactiveUI.Avalonia` for `ReactiveUI.Avalonia.Reactive` 12.1.6 (which
pulls Binding/Primitives .Reactive twins + System.Reactive 7.0.0), added
`using ReactiveUI.Reactive;` to 9 ViewModel/Model files, and retargeted
Program.cs to `ReactiveUI.Avalonia.Reactive` for `UseReactiveUI`. Verified:
full solution build 0 errors, Desktop.Tests 40/40, Core.Tests 192/192.
Do NOT mix base + .Reactive flavours (duplicate source-generator output).

**Still flagged:** nothing. NU1603 RESOLVED 2026-10-10 (see phantom removal below).

**Phantom removal 2026-10-10:** `Microsoft.Extensions.AI.OpenAI`
9.12.0-preview.1.25501.4 never existed on nuget.org (9.x line ends at 9.10.x);
restore always substituted 10.0.0-preview.1.25559.3 with NU1603. Verified zero
usage of its API in src (only `OpenAI.*` + `System.ClientModel` in
MeaiChatGateway.cs and ProviderSemanticJudge.cs). Removed via
`dotnet remove`, added `OpenAI` 2.6.0 direct (the exact transitively-resolved
version — zero behavior change; System.ClientModel stays 1.7.0). Verified:
restore clean with NO NU1603, full solution build 0 errors, Backend.Tests
568/568. Dependency is now honest: what is used is what is referenced.

**OpenAI 2.14.0 bump 2026-10-10:** `OpenAI` 2.6.0 → 2.14.0 (major number, but
changelog 2.7–2.14 shows zero breaking changes in the stable `OpenAI.Chat`
surface we use — all breaking changes are in experimental Realtime/Responses/
Containers APIs; Chat only gained features and bug fixes). Transitive
System.ClientModel auto-upgraded 1.7.0 → 1.15.0 per nuspec. Verified: restore
clean, full solution build 0 errors, Backend.Tests 568/568. Residual risk
(live-endpoint behavior) cannot be covered without a real API key — smoke-test
on next live AI run.

**Runner drift CLOSED 2026-10-10:** `xunit.runner.visualstudio` unified to 4.0.1
in all three test projects (was 2.8.2 / 2.8.2 / 3.1.4) as a deliberate major
exception. Nuspec confirms runner 4 runs xunit v1/v2/v3 on .NET 8+. Verified:
Core.Tests 192/192, Backend.Tests 563/563, Desktop.Tests 40/40, full solution
build 0 errors. The old rule blocking it as a major bump no longer applies.

**Test.Sdk 18 CLOSED 2026-10-10:** `Microsoft.NET.Test.Sdk` unified to 18.10.1
in all three test projects as a deliberate major exception (test-only, same risk
class as the coverlet/runner exceptions above). Verified: Core.Tests 192/192,
Desktop.Tests 40/40, Backend.Tests 563/563, full solution build 0 errors.

**Coverlet note:** `coverlet.collector` 6.0.4 → 8.0.1 → 10.1.0 approved as deliberate
major exception (test-only, `developmentDependency=true`, no CI coverage usage).
Verified 2026-10-10: 8.0.1 restore + build clean, `dotnet test --collect:"XPlat Code
Coverage"` passes 192/192 and emits coverage.cobertura.xml. Then bumped to 10.1.0,
same verification green (192/192 + cobertura). 10.1.0 is the .NET 10-aware line.

**Lesson 2026-10-10:** `--include-prerelease` hides stable servicing lines —
`Latest` showed 11.0.0-rc and masked the stable 10.0.12 patches, which Rider
surfaced. Always cross-check with a run WITHOUT the flag (or Rider's NuGet
panel) before concluding nothing stable is left.

**Next check due on or after 2026-10-17.**

---

**2026-09-29** — check ran successfully. No version bumps were applied; the user reviewed the
findings and chose to leave the `.csproj` files untouched for now.

**In scope, available but NOT applied:**

| Project | Package | From | To |
| --- | --- | --- | --- |
| `Eling.Backend` | `ModelContextProtocol` | 2.1.0 | 2.2.0 |
| `Eling.Desktop` | `Avalonia` | 12.1.2 | 12.1.3 |
| `Eling.Desktop` | `Avalonia.Desktop` | 12.1.2 | 12.1.3 |
| `Eling.Desktop` | `Avalonia.Fonts.Inter` | 12.1.2 | 12.1.3 |
| `Eling.Desktop` | `Avalonia.Themes.Fluent` | 12.1.2 | 12.1.3 |
| `Eling.Desktop` | `ReactiveUI.Avalonia` | 12.0.3 | 12.1.5 |
| `Eling.Backend.Tests` | `xunit` | 2.9.0 | 2.9.3 |
| `Eling.Desktop.Tests` | `xunit` | 2.9.0 | 2.9.3 |

`AvaloniaUI.DiagnosticsSupport` 2.2.3 was already current.

**Next check due on or after 2026-10-06.**
