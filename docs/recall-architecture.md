# Eling Memory Recall Architecture & Flow

This document details the complete recall architecture in Eling, combining the strategic Quality Gate policies for AI agents and the internal 3-layer search engine mechanism.

---

## 1. Agent-Side Strategic Flow (Loop Policy & Quality Gate)

To prevent context bloat and empty retrieval loops, all agents interacting with Eling follow a standardized quality gate: **Target Baseline $\ge 3$, Retry if $\le 2$, Hard Cap at 3 Attempts**.

```
                      [User Turn / Prompt Input]
                                  │
                                  ▼
      Is it Turn 1 (Session Start) OR has Context/Topic Shifted?
                                  │
                 ┌────────────────┴────────────────┐
                 │ NO                              │ YES
                 ▼                                 ▼
       [Answer Directly with              [Initiate Recall Cycle]
         Active Context]                           │
                                                   ▼
                                         [Hop 1: Initial Query]
                                         topics=['term1', 'term2']
                                                   │
                                                   ▼
                                      Retrieved Items >= 3?
                                                   │
                                  ┌────────────────┴────────────────┐
                                  │ YES (Meets Baseline)            │ NO (<= 2 Items)
                                  ▼                                 ▼
                         [Recall Complete]              Is this Attempt #3?
                         Proceed to Execution                       │
                                                   ┌────────────────┴────────────────┐
                                                   │ YES (Hard Stop)                 │ NO
                                                   ▼                                 ▼
                                          [Accept Available                 [Hop 2 / Hop 3: Retry]
                                           Context & Proceed]               Expand with associative terms/tags
                                                                                     │
                                                                                     └───► Loop back to check
```

### Strategic Rules:
1. **Target Baseline ($\ge 3$ Items):** A recall is considered complete and sufficient as soon as it retrieves at least 3 relevant items in `recallMemories`. The agent proceeds immediately.
2. **Retry Trigger ($\le 2$ Items):** If fewer than 3 memories return, the agent must retry using broader, synonymous, or associative topics (e.g. expanding `['pre-commit']` to `['git', 'commit', 'hygiene']`).
3. **Hard Cap (3 Hops Max):** An agent must **never** execute a 4th recall on the same topic. If after 3 attempts the count remains $\le 2$, the agent accepts that the repository has limited stored entries on that subject and moves on.

---

## 2. Engine-Side Search Pipeline (3-Layer FTS5 + Adaptive Fallback)

When `memory_recall` or `memory_search` is called, `SqliteMemoryIndex` and `MemoryRecallService` execute a multi-tier search process:

```
                          1. Raw Query Topics
                                  │
                                  ▼
                     2. Normalize Tags & Tokens
                     (Split -_./\|, lowercase, dedup)
                                  │
                                  ▼
                   3. PHASE 1: Precision Search (AND)
                   - Porter Table : "term1" "term2"
                   - Trigram Table: "term1" "term2" (length >= 3)
                                  │
                                  ▼
                     Combined Unique Matches >= 3?
                                  │
                 ┌────────────────┴────────────────┐
                 │ YES                             │ NO (< 3)
                 ▼                                 ▼
      [QueryMode = "and"]                4. PHASE 2: Adaptive Fallback (OR)
      Keep Precision Hits                - Porter Table : "term1" OR "term2"
                 │                       - Trigram Table: "term1" OR "term2"
                 │                       [QueryMode = "or-fallback"]
                 │                                 │
                 └────────────────┬────────────────┘
                                  │
                                  ▼
                    5. Merge & Relevance Ranking
                    - Porter Score  = -BM25 rank * 1.0 (Primary)
                    - Trigram Score = -BM25 rank * 0.4 (Typo/Substring)
                    - Total Rank    = PorterScore + TrigramScore
                    - MatchedVia    = ["porter", "trigram"]
                                  │
                                  ▼
                    6. Hydration & Response Assembly
                    - Scoped search resolution (Project vs Global)
                    - Hydrate full markdown files
                    - Bundle with recentMemories (10 latest) + Intentions
```

---

## 3. Search Layer Responsibilities

| Layer | Technology | Primary Responsibility | Example Match |
|---|---|---|---|
| **Layer 0** | Tag Normalizer | Splits concatenated words and standardizes case | `git-hygiene` $\rightarrow$ `["git", "hygiene"]` |
| **Layer 1** | FTS5 Porter | Stemming & root-form semantic word matching | `hygienic` matches `hygiene` |
| **Layer 2** | FTS5 Trigram | Substring and typo-tolerant character matching | `loma` matches `diplomatic`, `hygene` matches `hygiene` |
| **Orchestrator** | AND $\rightarrow$ OR Fallback | Starts strict for high precision; expands automatically to avoid empty results | Multi-topic queries return relevant unions instead of 0 hits |
| **Scorer** | Weighted BM25 | Combines layer signals and ranks best multi-word overlaps at the top | Exact matches rank #1, partial matches follow |

---

## 4. Response Metadata Contract

Every recall hit surfaces transparent metadata explaining *why* it matched:

```json
{
  "id": "01m1ms7k9hq9rarbmqaf7yy9wk",
  "type": "Preference",
  "content": "[Eling project] ALWAYS RUN GIT HYGIENE AT THE START OF EVERY CODE REVIEW...",
  "tags": ["code", "review", "hygiene", "pre", "commit", "git"],
  "matchedVia": ["porter", "trigram"],
  "porterScore": 10.35,
  "trigramScore": 4.18,
  "queryMode": "or-fallback"
}
```

* `matchedVia`: Lists the exact search tables (`porter`, `trigram`) that found the record.
* `porterScore`: BM25 score contribution from whole-word and stemmed token matching.
* `trigramScore`: BM25 score contribution from 3-gram character substring matching.
* `queryMode`: Indicates whether the memory was retrieved under strict precision (`"and"`) or via adaptive fallback (`"or-fallback"`).
