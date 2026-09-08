# Fuzzy Tag Recall — Design Specification

**Date:** 2026-09-07
**Status:** Draft — pending user approval
**Author:** Eling Agent

---

## Problem Statement

Current `memory_recall` by-tag is exact-match only. A memory tagged `git-hygiene` is not found when the agent recalls with topic `hygiene` or `git`. This causes empty recall results even when relevant memories exist.

**Root cause:** FTS5 with default `unicode61` tokenizer tokenizes `git-hygiene` into two separate tokens (`git` and `hygiene`). Query `hygiene` matches the token `hygiene`, but query `hygienic` or `git` (without the hyphen) does not match.

---

## Proposed Solution — Opsi C: Stemming + Prefix + Normalize

Implement three complementary improvements:

1. **Tag normalization at save time** — all tags lowercased, special chars stripped, joined with single space
2. **Stemming tokenizer at index time** — FTS5 `porter` tokenizer stems words to their root form
3. **Prefix expansion at query time** — each token in the query expanded to prefix form (`token*`)

This gives the most flexible recall: `hygien` finds `hygiene`, `hygienic`, `hygiene-check`; `git` finds `git-hygiene`, `gitignore`; `codereview` finds `code-review`.

---

## Component Changes

### 1. Tag Normalization — `Memory.Tags` setter

**File:** `src/backend/Eling.Core/Memory/Memory.cs`

Normalize tags on write so they are always lowercase, whitespace-trimmed, and have special separators collapsed to single spaces.

```csharp
// Before
public IReadOnlyList<string> Tags { get; }

// After
private List<string> _tags = [];
public IReadOnlyList<string> Tags => _tags.AsReadOnly();

// On setter: normalize each tag
_tags = rawTags
    .Select(t => t.Trim().ToLowerInvariant())
    .SelectMany(t => t.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries))
    .Where(t => t.Length >= 2)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToList();
```

**Example transformations:**
- `["Git-Hygiene", "CODE_REVIEW"]` → `["git", "hygiene", "code", "review"]`
- `["some_tag"]` → `["some", "tag"]`
- `["a"]` → removed (too short)

**Backward compatibility:** Existing memories with non-normalized tags remain readable. During `RebuildIndexAsync`, tags are re-normalized from the stored Markdown frontmatter. A one-time migration runs on first deploy.

### 2. Stemming Tokenizer — FTS5 virtual table

**File:** `src/backend/Eling.Core/Memory/Storage/SqliteMemoryIndex.cs`

Change the `memory_fts` virtual table from `unicode61` to `porter unicode61`:

```sql
-- Before
CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(
    id UNINDEXED,
    content,
    tags,
    source
);

-- After
CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(
    id UNINDEXED,
    content,
    tags,
    source,
    tokenize='porter unicode61'
);
```

**What this enables:**
- `hygiene` → `hygien` (Porter stem)
- `hygienic` → `hygien`
- `remembering` → `remember`
- `git` → `git` (unchanged — Porter handles English well)

**Caveat:** Porter stemmer is English-only. Indonesian words pass through un-stemmed. This is acceptable for the Eling project's primary language (English tags and content). Indonesian tags will still work via prefix matching.

**Database migration:** On first startup, if the existing FTS table was created without `porter`, drop and recreate it, then call `RebuildIndexAsync` for all memories.

### 3. Prefix Expansion at Query Time

**File:** `src/backend/Eling.Core/Memory/Storage/SqliteMemoryIndex.cs` — `SearchAsync` method

Expand each query token to prefix form:

```csharp
// BuildFtsQuery: before
// Returns: "hygiene"

// BuildFtsQuery: after
// Returns: "hygiene* hyg* hyg"  (prefix expansion for each token >= 3 chars)
// Tokens < 3 chars: kept as-is (AND semantics)
```

```csharp
private static string BuildFtsQuery(string query)
{
    var phrases = new List<string>();
    foreach (var rawToken in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
    {
        var token = rawToken.Replace("\"", string.Empty).ToLowerInvariant();
        if (!token.Any(char.IsLetterOrDigit)) continue;

        if (token.Length >= 3)
        {
            // Prefix expansion: "hygiene" → "hygiene*" + first 4 chars prefix
            phrases.Add($"\"{token}*\"");
            if (token.Length >= 4)
            {
                phrases.Add($"\"{token[..4]}*\"");
            }
        }
        else
        {
            // Short tokens: exact match only
            phrases.Add($"\"{token}\"");
        }
    }
    return string.Join(' ', phrases);
}
```

**Alternative (more aggressive prefix):** Simply append `*` to every token ≥ 3 chars. Simpler code, slightly broader matches. Recommended for simplicity.

---

## Alternative Approaches Considered

### Opsi A — Stemming Only (no prefix, no normalize)

**Pros:** Simplest change (only FTS5 tokenizer swap).
**Cons:** `hygienic` → `hygien` (stem match), but `hygiene-checks` still won't match `hygiene` query unless prefix is used.

### Opsi B — Prefix Only (no stemming, no normalize)

**Pros:** No index schema change needed.
**Cons:** `hygienic` (longer word) won't match `hygiene` query. No root-form matching.

### Opsi D — Trigram Index

**Pros:** Substring match on all 3+ character sequences.
**Cons:** Larger index, slower writes, overkill for 50-500 memories. Trigram is appropriate for large corpuses, not personal knowledge bases.

### Recommended

**Opsi C** (stemming + prefix + normalize) because it covers all three use cases:
- Root-form matching (stemming)
- Prefix/superstring matching (prefix query)
- Case/separator normalization (tag normalization)

---

## Performance Considerations

- **Index size:** Adding `porter` tokenizer does not significantly increase index size. Prefix expansion (`token*`) is handled at query time, not index time.
- **Write latency:** Tag normalization adds negligible overhead. Stemming is done by SQLite internally.
- **Query latency:** Prefix expansion doubles the number of FTS5 terms per token (e.g., `hygiene` → `hygiene* hyg*`). Still fast for < 500 documents.
- **Rebuild time:** Full `RebuildIndexAsync` on ~65 memories is < 1 second.

---

## Testing Strategy

1. **Unit test — tag normalization:**
   - `["Git-Hygiene", "CODE_REVIEW"]` → `["git", "hygiene", "code", "review"]`
   - `["Some_Tag", "UPPERCASE"]` → `["some", "tag", "uppercase"]`
   - `["a", "bc"]` → removed (too short)

2. **Unit test — FTS query building:**
   - `hygiene` → `"hygiene*"` (single prefix)
   - `git code review` → `"git*" "code*" "review*"`
   - `a bc` → `"a" "bc*"` (`a` kept exact, `bc` expanded to prefix since ≥ 3 chars)

3. **Integration test — recall:**
   - Save memory with tag `git-hygiene`
   - Recall with topic `hygiene` → memory found (stem match)
   - Recall with topic `hygienic` → memory found (both stem to `hygien`)
   - Recall with topic `git` → memory found (prefix match)

4. **Regression test:**
   - Save memory with tag `superpower-skill`
   - Recall with `superpower` → found
   - Recall with `skill` → found

---

## Rollout Plan

1. **Add tag normalization** to `Memory.cs` (non-breaking, additive)
2. **Change FTS5 tokenizer** in `SqliteMemoryIndex.cs` (requires migration)
3. **Update `BuildFtsQuery`** for prefix expansion
4. **Add migration check** on startup: if old schema detected → drop + recreate FTS + rebuild index
5. **Write tests** for normalization + query building + recall integration
6. **Deploy:** Rebuild backend, restart server. Existing memories re-indexed on first run.

---

## Open Questions (awaiting user input)

1. **Indonesian stemming:** `tokenize='porter'` is English-only. If user expects Indo tag stems to work (e.g., `mengingat` → `ingat`), we need a different stemmer. Decision needed: English-only or add Indo support?
2. **Backward compat migration:** Should existing memories be re-indexed on first deploy automatically, or require explicit `memory_rebuild_index` call?
3. **Single-word vs multi-word behavior:** If user recalls `git hygiene` (2 tokens), should results require both tokens (AND) or either (OR)? Current FTS5 BM25 behavior is AND (all terms must match). Recommend keeping AND.

---

## Files to Modify

| File | Change |
|------|--------|
| `src/backend/Eling.Core/Memory/Memory.cs` | Add tag normalization in setter |
| `src/backend/Eling.Core/Memory/Storage/SqliteMemoryIndex.cs` | Change FTS5 tokenizer to `porter unicode61`; update `BuildFtsQuery` for prefix expansion; add migration logic |
| `tests/Eling.Core.Tests/` | Add `MemoryTagNormalizationTests.cs` and `FtsQueryBuildingTests.cs` |

---

## References

- [SQLite FTS5 Tokenizers](https://www.sqlite.org/fts5.html#tokenizers)
- [Porter Stemmer Algorithm](https://tartarus.org/martin/PorterStemmer/)
- Existing code: `src/backend/Eling.Core/Memory/Storage/SqliteMemoryIndex.cs`, `src/backend/Eling.Core/Memory/Memory.cs`
