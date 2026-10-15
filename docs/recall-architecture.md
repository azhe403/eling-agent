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
                                         context.topics=['term1', 'term2']
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
4. **Session-Start Breadth:** The first-turn recall is never anchored solely on the opening message's subject. Sweep broadly — the opening message's topics plus standing rules and preferences, standing decisions, lessons and known gotchas, workflow/repo conventions, and recently touched work — and pass them with the canonical shape `context: { topics: [...] }` (a top-level `topics` alias is also accepted). The breadth floor is server-enforced: every recall response includes `standingMemories`, a topic-independent baseline of standing rules/preferences, decisions, and lessons (current-project first, then most recently updated, capped), so even a narrow topic call still carries the consistency spine.
5. **Zero-Result Self-Check:** A session-start call that returns `stats.recallCount == 0` is a failed call, not an empty world: the topics were passed in a shape the tool ignored, or nothing matched. Re-issue with the nested `context.topics` shape and broader terms (bounded by the Hard Cap) before answering.

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
* `queryMode`: Indicates whether the memory was retrieved under strict precision (`"and"`) or via adaptive fallback (`"or-fallback"`). Standing-baseline entries report `matchedVia: ["standing"]` with `queryMode: "standing-baseline"`: they are not search hits but a topic-independent baseline of standing rules/preferences, standing decisions, and lessons (current-project first, then most recently updated, capped) that every recall payload carries so a narrow topic call still surfaces the consistency spine.

---

## 5. Project-Scope Posture (`projectScope`)

`memory_recall` also returns a `projectScope` block describing whether onboarding applies, so the agent does not have to prompt repeatedly:

```json
"projectScope": {
  "posture": "uninitialized",
  "policy": "ask",
  "adoptable": true,
  "initialized": false,
  "headRoot": null
}
```

* `posture`: `own-scope` | `ancestor-scope` | `uninitialized` | `user-home`.
* `policy`: resolved machine-local policy — `ask` (offer consent-gated onboarding when adoptable) or `disabled` (project scope off; use `scope=global`).
* `adoptable`: true only when the workspace is not the user home, has no own scope, and policy is `ask`.
* `initialized` / `headRoot`: scope-chain facts.

The policy lives at `~/.config/eling/config/project-policy.json` and is managed with the `memory_project_scope_policy` MCP tool. Timestamps are intentionally not part of this block; they are returned by `memory_project_status` and the policy tool. See `docs/superpowers/specs/2026-09-11-project-scope-policy-design.md`.

---

## 6. Overflow Summary (`recallSummary`)

`recallLimit` used to end the story: the search asked for at most N hits, so relevant items ranked N+1 and beyond never left the FTS layer — present in the store, invisible to the agent. The recall service now fetches one overflow window beyond the limit (`recallLimit + 10`); the top N still arrive in full as `recallMemories`, and the rest are summarized instead of skipped.

```
                     Topic hits beyond recallLimit
                                │
              ┌─────────────────┴─────────────────┐
              │ Overflow exists?                  │ No → recallSummary omitted (lean payload)
              ▼                                   ▼
     [Semantic summarizer available?]      [No overflow to summarize]
              │
     ┌────────┴────────┐
     │ YES             │ NO
     ▼                 ▼
[Judge paragraphs]  [Deterministic digest]
source: "judge"      source: "heuristic" + note
```

* `itemCount` / `summarizedCount`: total overflow hits vs. how many the digest covers (bounded by the window).
* `source`: `judge` (semantic summarizer, the same provider-backed judge as `memory_save` uses, with the same Polly/circuit-breaker policy) or `heuristic` (deterministic digest — one compact `[Type] content` fragment per item, grouped into paragraphs).
* `note`: present only when the digest degraded — judge not configured, timed out, returned nothing usable, or failed. Mirrors the save pipeline's `judge-failed: ...; fell back` posture so a dead provider is diagnosable rather than silent.
* `paragraphs`: multi-paragraph by design — long records are split across paragraphs instead of being crammed into one block.
* `summarizeOverflow: false` skips the whole summary for minimum latency.

The judge's summary prompt (`RecallSummaryPrompt`) asks for 2–5 theme-grouped paragraphs with no ids, and parsing caps at five paragraphs of ~600 characters; the deterministic digest (`RecallSummaryFallback`) caps at ten items, five paragraphs, ~180 characters per fragment. Both are pure functions, unit-tested without a provider. See `docs/semantic-judge-architecture.md` §6 for the shared-judge rationale.
