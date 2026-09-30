# Multi AI Provider Profiles — Design

**Date:** 2026-09-22  
**Status:** Proposed (awaiting user review)  
**Approach:** Named provider profiles + one active profile (Approach 1)

## Problem

Eling stores a single AI provider slot (`agent-provider.json`: `baseUrl`, `model`, `apiKey`). Users who run more than one OpenAI-compatible endpoint (e.g. local Ollama + a remote OpenAI-compatible gateway) must overwrite config to switch. There is no way to keep two endpoints configured side by side.

## Goals

- Persist multiple named OpenAI-compatible provider profiles on the backend.
- Select one profile as **active**; chat, model list, and test always use the active profile.
- Migrate existing single-config installs automatically with no user action.
- Keep API keys server-side only (never returned to the desktop client).
- Keep legacy `/api/agent/provider` routes working (thin wrappers over the active profile).

## Non-goals

- Native Anthropic / Gemini wire-protocol adapters (Approach 3).
- Per-workspace provider/model pinning (Approach 2).
- Automatic failover or multi-provider fan-out.
- Cloud secret managers; keys stay in the local profile store file.

## Data model

Replace the single-config file with a document:

```json
{
  "activeProfileId": "01J...",
  "profiles": [
    {
      "id": "01J...",
      "name": "Ollama local",
      "baseUrl": "http://127.0.0.1:11434/v1",
      "model": "qwen3",
      "apiKey": "…",
      "modelsCached": ["qwen3"]
    }
  ]
}
```

| Field | Rules |
|-------|--------|
| `id` | Stable unique id (Guid string) |
| `name` | Required; unique case-insensitive; max 64 chars |
| `baseUrl` | Required; same validation as today |
| `model` | Optional free text (same as today) |
| `apiKey` | Stored only; never serialized to clients |
| `modelsCached` | Per-profile cache (same semantics as today) |

Note: `streamTimeoutSeconds` is **not** part of the current provider document; streaming timeout behavior stays in `MeaiChatGateway` configuration and is out of scope unless added later.

**Migration (interacts with existing `StoreMigration`):** After any `StoreMigration.MoveFileIfNeeded` for the provider file, if `agent-provider.json` exists and `agent-providers.json` does not, create one profile named `Default` from the old file, set it active, rename the old file to `agent-provider.json.bak`.

**Id format:** ULID string (matches project preference for durable ids).

**Invariants:**

- At least one profile always remains (delete of the last profile is rejected).
- Deleting the active profile promotes the first remaining profile (stable file order) to active.
- Empty or omitted `apiKey` on update means “keep stored key” (per profile), matching current PUT semantics.

## Backend API

### Legacy routes (unchanged shape, operate on active profile)

| Method | Path | Behavior |
|--------|------|----------|
| GET | `/api/agent/provider/` | Active profile view: `baseUrl`, `model`, `hasKey`, `modelsCached` |
| PUT | `/api/agent/provider/` | Upsert fields on active profile; `null`/omitted `baseUrl` or `model` leave them unchanged; empty/omitted `apiKey` keeps stored key (matches current `ProviderStore.Update`) |
| POST | `/api/agent/provider/models` | List models for active profile |
| POST | `/api/agent/provider/test` | Ping active profile |

### Profile management routes

| Method | Path | Behavior |
|--------|------|----------|
| GET | `/api/agent/provider/profiles` | List profiles: `id`, `name`, `baseUrl`, `model`, `hasKey`, `isActive` (no API keys; `modelsCached` optional/omitted) |
| POST | `/api/agent/provider/profiles` | Create profile. Body: `name`, `baseUrl`, `model?`, `apiKey?`, `activate?` (default false) |
| PUT | `/api/agent/provider/profiles/{id}` | Update profile (same upsert-key rules as legacy PUT) |
| DELETE | `/api/agent/provider/profiles/{id}` | Delete if not last; promote if was active |
| POST | `/api/agent/provider/profiles/{id}/activate` | Set active profile |
| POST | `/api/agent/provider/profiles/{id}/models` | Fetch models for that profile |
| POST | `/api/agent/provider/profiles/{id}/test` | Test that profile. Optional body may override `baseUrl`/`model`/`apiKey` for a one-shot probe without saving (mirrors legacy `/test`) |

**Errors:**

- Unknown profile id → 404.
- Duplicate name → 400 with clear message.
- Delete last profile → 400.
- Concurrent desktop clients: last-write-wins on profile fields (same as current single config).

## Resolution path

- `ProviderStore` becomes the profile store: `GetActive()`, `SetActive(id)`, `List()`, `Create(...)`, `Update(...)`, `Delete(id)`, `TryGet(id)`.
- `MeaiChatGateway`, `HttpProviderClient`, and `AgentTurnService` continue to resolve a single config via `ProviderStore.TryGetConfig` / active-profile equivalents — no per-request provider selection.
- `TestConnectionAsync` may still receive an optional ad-hoc `baseUrl`/`model`/`apiKey` body (legacy `/test` behavior); when omitted, active profile values are used.
- DI registration shape unchanged (store + `IChatGateway` / `IProviderClient` ports).
- Streaming timeout remains gateway-level configuration (not per-profile in this iteration).

## Desktop Settings UI

The Provider card becomes a profile manager:

- **Profile selector:** ComboBox of profile names + **New** button. Selecting a profile loads it into the form; it does **not** activate until the user clicks **Set active** (avoids accidental mid-chat switches).
- **Form fields (unchanged):** base URL, API key (write-only, empty = keep), model ComboBox + Fetch Models, Test, Save.
- **Save** writes to the **selected** profile by id (not always the active one).
- **Delete** with confirm dialog; disabled when only one profile exists.
- Status line shows which profile is active.
- Explicit **Set active** button on the selected profile.
- **New** profile flow: prompt for name + base URL (+ optional model/key), then save; activate only if the user chooses Set active (or create with `activate: true` when that is the only sensible first profile).

### Client API additions

`ElingApiClient` gains: `ListProfilesAsync`, `CreateProfileAsync`, `UpdateProfileAsync`, `DeleteProfileAsync`, `ActivateProfileAsync`, plus profile-scoped `FetchModelsAsync(id)` / `TestProviderAsync(id)` wrappers that hit the `{id}/models` and `{id}/test` routes. Existing provider methods unchanged.

New DTO:

```csharp
public record ProviderProfileDto(
    string Id, string Name, string? BaseUrl, string? Model,
    bool HasKey, bool IsActive);
```

## Testing

Extend `tests/Eling.Backend.Tests/AgentProviderTests.cs` (and/or a dedicated profile test class):

1. **Migration:** legacy `agent-provider.json` → one `Default` profile active; legacy GET returns same shape; old file backed up.
2. **Profiles CRUD:** create, list, update, delete via new routes; `hasKey` never leaks `apiKey`.
3. **Activate:** switching active changes legacy GET/PUT/models/test target.
4. **Per-profile key:** empty/omitted apiKey on profile B keeps profile B’s stored key and does not affect profile A.
5. **Partial update:** null baseUrl/model on PUT leaves those fields unchanged.
6. **Delete active:** promotes another profile; GET returns promoted profile.
7. **Delete last:** rejected 400.
8. **Legacy contract:** existing round-trip / no-leak / models / test tests keep passing unchanged.

**Numbering note:** items renumber if inserted; see list below for final order.

Desktop: no dedicated Settings VM unit-test project — keep coverage in backend integration tests; optional desktop tests only if `Eling.Desktop.Tests` already has a suitable harness for the client DTOs. Manual QA checklist lives in the implementation plan (create → edit → set active → delete → chat uses active).

## Security

- API keys only in the backend store file; responses use `hasKey` only (same as today).
- No new network surface beyond existing local HTTP API pattern.

**Target:** `Eling.Backend` (`ProviderStore`, `AgentEndpoints`), `Eling.Desktop` (Settings UI + `ElingApiClient`), `Eling.Backend.Tests` (+ desktop API DTOs as needed).

## Open follow-ups (out of scope)

- Per-workspace profile binding (Approach 2).
- Native non-OpenAI adapters (Approach 3).
- Automatic fallback across profiles.
- Per-profile streaming timeout / temperature / extra headers (future if needed).
