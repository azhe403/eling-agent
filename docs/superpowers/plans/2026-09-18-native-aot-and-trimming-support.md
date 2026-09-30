# Eling Native AOT & Trimming Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enable full Native AOT (`PublishAot=true`) and Trimming compatibility for `Eling.Core` and `Eling.Backend` without breaking YAML front matter or JSON serialization behaviors.

**Architecture:** Replace dynamic reflection-based JSON/YAML operations with Roslyn source generators (`System.Text.Json.Serialization.JsonSerializerContext` and `YamlDotNet.Serialization.StaticSerializerBuilder`). Refactor generic/reflection-based MCP tool registrations and enum converters to strictly static, trim-safe alternatives.

**Tech Stack:** .NET 10, C# 14, System.Text.Json Source Generator, YamlDotNet Static Builders, Microsoft.Extensions.AI / Mcp Server SDK.

## Global Constraints

- Never use `#pragma warning disable` or `[UnconditionalSuppressMessage]` to silence IL trim/AOT warnings; resolve the root cause.
- Adhere to `Eling.Core.Serialization.JsonDefaults` conventions (camelCase, case-insensitive, indented) using `[JsonSourceGenerationOptions]`.
- Keep existing YAML Markdown front matter backward compatibility (fields: `id`, `type`, `status`, `tags`, `source`, `created_at`, `updated_at`, etc.).
- Never use `git checkout/reset/restore` to revert files.

---

### Task 1: Audit and Annotate Eling.Core for Trim/AOT Compatibility

**Files:**
- Modify: `src/backend/Eling.Core/Eling.Core.csproj`
- Modify: `src/backend/Eling.Core/Serialization/JsonDefaults.cs`
- Create: `src/backend/Eling.Core/Serialization/ElingJsonSerializerContext.cs`

**Interfaces:**
- Produces: `ElingJsonSerializerContext.Default` for all core models (`Memory`, `MemoryId`, `MemoryFrontMatter`, `Intention`, `SaveResult`, `Scope`).

- [ ] **Step 1: Update `Eling.Core.csproj` with AOT and Trim analyzer flags**

Add `<IsAotCompatible>true</IsAotCompatible>` and `<EnableTrimAnalyzer>true</EnableTrimAnalyzer>` to `<PropertyGroup>`.

- [ ] **Step 2: Create `ElingJsonSerializerContext`**

Define `ElingJsonSerializerContext` inheriting from `JsonSerializerContext` with `[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true)]` and register all core DTOs and value types.

- [ ] **Step 3: Update `JsonDefaults.cs` to expose `TypeInfoResolver`**

Update `JsonDefaults.Shared` to configure `.TypeInfoResolver = ElingJsonSerializerContext.Default`.

- [ ] **Step 4: Run build on `Eling.Core` to verify zero IL warnings on JSON parts**

Run: `dotnet build src/backend/Eling.Core/Eling.Core.csproj -c Release`
Expected: Build passes.

---

### Task 2: Migrate YamlDotNet Storage to Static / Source-Generated Serialization

**Files:**
- Modify: `src/backend/Eling.Core/Memory/Storage/FileSystemMemoryStorage.cs`
- Modify: `src/backend/Eling.Core/Memory/Serialization/FileSystemIntentionStorage.cs`
- Modify: `src/backend/Eling.Core/Memory/Storage/MemoryFrontMatter.cs`
- Modify: `src/backend/Eling.Core/Memory/Serialization/IntentionFrontMatter.cs`

**Interfaces:**
- Consumes: `MemoryFrontMatter`, `IntentionFrontMatter`
- Produces: Trim-safe `StaticSerializerBuilder` / `StaticDeserializerBuilder` or compiled YAML mappings.

- [ ] **Step 1: Write unit tests verifying YAML serialization & deserialization fidelity**

Run: `dotnet test tests/Eling.Core.Tests -filter "FullyQualifiedName~Storage"`
Expected: PASS.

- [ ] **Step 2: Replace dynamic `SerializerBuilder` and `DeserializerBuilder` with `StaticSerializerBuilder` & `StaticDeserializerBuilder`**

Migrate YamlDotNet builder calls in `FileSystemMemoryStorage` and `FileSystemIntentionStorage` to avoid `RequiresDynamicCode` calls.

- [ ] **Step 3: Run tests to ensure YAML parsing stays identical**

Run: `dotnet test tests/Eling.Core.Tests`
Expected: All existing storage tests PASS.

---

### Task 3: Replace Reflection Enum Converters and Unbound JSON Calls in Eling.Backend

**Files:**
- Modify: `src/backend/Eling.Backend/Mcp/Tools/FileSystemTools.cs`
- Modify: `src/backend/Eling.Backend/Mcp/Tools/MemoryMaintenanceTool.cs`
- Modify: `src/backend/Eling.Backend/Scope/JsonProjectScopePolicyStore.cs`
- Modify: `src/backend/Eling.Backend/Agent/Services/WorkspaceRegistry.cs`
- Create: `src/backend/Eling.Backend/Serialization/BackendJsonSerializerContext.cs`

**Interfaces:**
- Produces: `BackendJsonSerializerContext` covering all Mcp DTOs, scope policies, and tool responses.

- [ ] **Step 1: Create `BackendJsonSerializerContext`**

Include all Backend DTOs, Mcp payloads, and error schemas with `[JsonSerializable(typeof(...))]`.

- [ ] **Step 2: Replace non-generic `JsonStringEnumConverter` with generic `JsonStringEnumConverter<TEnum>`**

Update `FileSystemTools.cs` and `MemoryMaintenanceTool.cs` to replace `new JsonStringEnumConverter()` with generic typed enum converters or serializer context settings.

- [ ] **Step 3: Replace reflection-based `JsonSerializer.Serialize` and `Deserialize`**

Pass `JsonTypeInfo<T>` or `BackendJsonSerializerContext.Default` to all `JsonSerializer` invocations.

- [ ] **Step 4: Build Backend and verify IL warnings reduction**

Run: `dotnet build src/backend/Eling.Backend/Eling.Backend.csproj -c Release`
Expected: Zero IL2026/IL3050 warnings from internal Backend code.

---

### Task 4: Refactor Dynamic MCP Tool Registration to Static/Generic Form

**Files:**
- Modify: `src/backend/Eling.Backend/Mcp/McpServiceExtensions.cs`
- Modify: `src/backend/Eling.Backend/Eling.Backend.csproj`

**Interfaces:**
- Consumes: Mcp Tool classes (`MemoryRecallTool`, `MemorySaveTool`, `FileSystemTools`, etc.)
- Produces: Direct static registration avoiding runtime `Assembly` reflection.

- [ ] **Step 1: Replace `WithToolsFromAssembly` with explicit tool type registrations**

Register each MCP tool class individually using generic `WithTools<TTool>()` methods supported by the MCP SDK for Native AOT.

- [ ] **Step 2: Verify MCP Server stdio handshake and tool discovery**

Run: `pwsh scripts/sim-mcp-stdio.ps1`
Expected: All MCP tools discoverable and functional.

---

### Task 5: Enable `PublishAot` and Validate End-to-End Native Compilation

**Files:**
- Modify: `src/backend/Eling.Backend/Eling.Backend.csproj`
- Modify: `scripts/publish-global.ps1`
- Modify: `scripts/publish-global.sh`

- [ ] **Step 1: Add AOT configuration in `Eling.Backend.csproj`**

Add `<PublishAot>true</PublishAot>` and `<IsAotCompatible>true</IsAotCompatible>` under release conditions.

- [ ] **Step 2: Execute Native AOT Publish command**

Run: `dotnet publish src/backend/Eling.Backend/Eling.Backend.csproj -c Release -r win-x64 --self-contained true`
Expected: Publish succeeds producing native binary with zero trimming/AOT errors.

- [ ] **Step 3: Run comprehensive smoke tests against native binary**

Run: `pwsh scripts/validate-eling.ps1` & `pwsh scripts/publish-global.ps1`
Expected: All smoke tests (health endpoint, MCP initialize, memory_save, memory_get) PASS.
