# Phase 1 Step 2: Memory Domain Model Design

## Goal
Implement the initial `Memory` domain model in `Eling.Core` and unit tests in `Eling.Core.Tests`.

## Dependencies
- Add `Ulid` NuGet package to `Eling.Core`.

## Domain Types (`Eling.Core`)

### `MemoryType` Enum
- `Fact`
- `Preference`
- `Decision`
- `Lesson`
- `Note`

### `MemoryStatus` Enum
- `Active`
- `Superseded`
- `Archived`

### `MemoryId` Value Object / Identifier Wrapper
- Wrapper `MemoryId` struct or record around `Ulid` string representation (or `Ulid` instance) so `Eling.Core` domain entities depend on `MemoryId` abstraction rather than directly coupling to the underlying ULID package type.
- Exposes `MemoryId.NewId()` or `MemoryId.Parse(string)`.

### `Memory` Model
Properties:
- `MemoryId Id`: Unique identifier.
- `MemoryType Type`: Category of memory.
- `MemoryStatus Status`: Lifecycle status (default `MemoryStatus.Active`).
- `string Content`: Memory text content.
- `IReadOnlyCollection<string> Tags`: Tags array/set for categorization.
- `DateTimeOffset CreatedAt`: Timestamp when created.
- `DateTimeOffset UpdatedAt`: Timestamp when last updated.
- `string? Source`: Optional reference or origin of memory.

Factory/Constructor:
- Constructor initializing required fields, setting `Id` to `Ulid.NewUlid()`, `Status` to `Active`, and `CreatedAt`/`UpdatedAt` to `DateTimeOffset.UtcNow`.

## Tests (`tests/Eling.Core.Tests/MemoryTests.cs`)
Replace `UnitTest1.cs` with unit tests verifying:
- Creation with defaults (`Status == Active`, `Id` is valid ULID, timestamps set).
- Property assignments (`Type`, `Content`, `Tags`, `Source`).
- Tags handling (empty vs populated).
- Updating `Status` or fields.

## Constraints
- No EF Core, SQLite, ASP.NET, Markdown/JSON serialization, embeddings, vector search, scoring, or graph relationships.
- Framework-independent domain logic only.
