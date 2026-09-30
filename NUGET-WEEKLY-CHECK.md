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

1. **`Microsoft.Extensions.AI.OpenAI` is pinned to a version that does not exist.**
   `Eling.Backend` asks for `9.12.0-preview.1.25501.4`; nuget.org has no such version. Restore
   silently substitutes `10.0.0-preview.1.25559.3` and emits `NU1603`. The build is therefore not
   using the package the `.csproj` names. Flag this on every run.

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
