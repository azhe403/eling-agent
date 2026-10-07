# Eling Memory Semantic Judge Architecture & Flow

This document details the architecture, execution pipeline, and design rationale of the **Semantic Judge (LLM)** within the Eling memory save pipeline (`memory_save`).

---

## 1. Problem Statement & Motivation

Prior to the Semantic Judge, Eling relied strictly on bag-of-words heuristic similarity algorithms (blending the *Sørensen–Dice coefficient* and *Max Containment* in `MemorySimilarity`):

1. **Long Rewritten Memories Fragmented into Duplicates:**
   When an existing long memory was revised or rewritten with different wording (e.g., updating commit message guidelines from two paragraphs to one), the token intersection ratio dropped drastically (measured at ~0.46 similarity, below the 0.60 threshold). Consequently, the system erroneously created a duplicate memory (`Created`) rather than updating the existing one.
2. **Opposite Meanings Merged Silently (Silent Data Loss):**
   Opposing statements that shared almost identical vocabulary (e.g., `"User prefers dark mode"` vs. `"User prefers light mode"`) produced an exceptionally high heuristic similarity score (**0.75**). The heuristic blindly treated them as near-duplicates and merged them in-place, silently overwriting the user's prior preference without understanding that the directives were contradictory.

### Solution: Heuristic as Retriever, LLM as Semantic Judge
- **FTS / Heuristic Retrieval** is demoted to a fast, exhaustive **Retriever (coarse filter)** that narrows candidates with shared context in milliseconds.
- **Semantic Judge (LLM)** acts as the authoritative **Judge (qualitative decider)** determining whether an incoming memory represents a revision, a contradiction, or an independent topic.

---

## 2. End-to-End Pipeline Diagram (`memory_save`)

```
                      memory_save (Incoming Memory)
                                   │
                                   ▼
             [Stage 1: Exact Match Check (100% Identical?)]
                                   │
                  ┌────────────────┴────────────────┐
                  │ YES                             │ NO
                  ▼                                 ▼
         [Update In-Place]                [Stage 2: FTS Recall Retrieval]
         (No LLM call)                    (Porter + Trigram, Filter Type & Status)
                                                    │
                                   ┌────────────────┴────────────────┐
                                   │ 0 Candidates                    │ Candidates Found (C1..Cn)
                                   ▼                                 ▼
                            [Create Memory]               [Stage 3: Assemble Judge Prompt]
                            (No LLM call)                 (C0 = Incoming, C1..Cn = Candidates)
                                                                     │
                                                                     ▼
                                                          [Stage 4: Polly Resilience Pipeline]
                                                          ├─ Attempt 1 (10s timeout)
                                                          └─ Attempt 2 (15s timeout)
                                                                     │
                                   ┌─────────────────────────────────┴──────────────────┐
                                   │ SUCCESSFUL VERDICT                                 │ EXHAUSTED / TIMEOUT / CIRCUIT OPEN
                                   ▼                                                    ▼
                     [Stage 5: Execute Verdict]                             [Stage 6: Heuristic Fallback]
           ┌───────────────────────┴───────────────────────┐                (Degrade to heuristic decision,
           │ Unrelated / Target None                       │ Revision /      log exact failure reason in response)
           ▼                                               │ Contradiction
    [Create Memory]                                        ▼
    (Record judge reason)                         [Update Target In-Place]
                                                  (Union tags, preserve previousContent,
                                                   record verdict & confidence in reason)
```

---

## 3. Pipeline Stages in Detail

### Stage 1: Exact Match Short-Circuit
- Before making any network calls or invoking search, content is normalized (`Trim()` and case-insensitive check).
- If an active memory with identical content already exists, it is updated in-place immediately, avoiding unnecessary token usage and latency.

### Stage 2: FTS Recall Candidate Retrieval
- If not an exact match, the incoming memory's content is executed as an FTS query against the SQLite search index (**Full-Text Search** using *Porter Stemmer* and *Trigram* layers).
- Candidates are filtered to match the incoming record's `MemoryType` and `Active` status.
- Up to the top 5 relevant candidates are retrieved.
- **Cost Guard:** If recall returns 0 candidates (no topically related memories in the store), the system immediately proceeds to **`Created`** without invoking the judge.

### Stage 3: Structured Prompt Assembly & Wire Format
Candidates are labelled with short ordinal tags so the model is not burdened with echoing 26-character ULID strings:
- `C0`: The incoming memory being saved.
- `C1 .. Cn`: Existing candidate memories from recall.

The model is instructed to output strictly structured JSON:
```json
{
  "relation": "revision",
  "confidence": 0.95,
  "reason": "C0 updates timeout rules on C1 with escalating attempt configuration.",
  "target": "C1"
}
```

Available relations:
| Relation | Semantic Meaning | System Action |
|---|---|---|
| `identical` | Exact same meaning and guidance | Update in-place into target |
| `revision` | Refines, updates, or clarifies existing guidance | Update in-place into target |
| `contradiction` | Directly conflicts with or reverses existing guidance | Update in-place into target |
| `superset` | Incoming memory contains existing guidance plus more | Update in-place into target |
| `subset` | Incoming memory is a narrower version of existing guidance | Update in-place into target |
| `unrelated` | Distinct topic with no directive relationship | Create as an independent memory |

### Stage 4: Polly Resilience Pipeline (Escalating Timeouts)
To handle latency variance and inference delays across cloud and local providers, invocations are orchestrated through Polly v8:
1. **Escalating Attempt Timeouts:**
   - **Attempt 1 (10s):** Comfortably covers normal provider inference latency (~3.5s - 5.5s) on the first try without premature cutoffs.
   - **Attempt 2 (15s):** Provides an extended fallback attempt if the provider experiences transient queuing or network latency.
2. **Backoff Delay:** Exponential backoff (200ms -> capped at 1s) between attempts.
3. **Circuit Breaker:** If 5 consecutive operations fail, the circuit opens for 30 seconds. Subsequent saves fail-fast (0ms) to fallback without hammering a degraded endpoint.
4. **Outer Fail-Safe Budget (`JudgeTimeout`):** Configured with a default of **30 seconds** in `SmartSaveOptions` as an overarching cancellation deadline.

### Stage 5: Verdict Execution
- If the verdict is `revision`, `contradiction`, `identical`, `superset`, or `subset` with a valid candidate target:
  - The target's content is updated in-place with the incoming content.
  - Tags are combined via union.
  - Previous content is retained in `previousContent` on the result.
  - The model's reasoning is recorded in `reason` (e.g. `"judge: contradiction into '01m4be...' (confidence 0.95) - ..."`).
- If the verdict is `unrelated` (or target is `none`):
  - A new memory is saved with `action: "created"`.

### Stage 6: Safe Heuristic Fallback
If Polly retries are exhausted or the provider is unreachable:
- The system **never aborts the save** and **never disables deduplication entirely**.
- The operation degrades safely to the historical heuristic similarity score, with the specific failure cause documented in `reason`:
  `"judge-failed: timed out after 20s; fell back to the heuristic decision"` or
  `"judge-failed: BrokenCircuitException: ...; fell back to the heuristic decision"`.

---

## 4. Configuration & Credential Isolation

Semantic Judge configuration is persisted per-user:
📁 `~/.config/eling/config/semantic-judge.json`
*(On Windows: `C:\Users\<username>\.config\eling\config\semantic-judge.json`)*

```json
{
  "enabled": true,
  "baseUrl": "https://provider-endpoint/v1",
  "model": "model-name",
  "apiKey": "sk-..."
}
```

### Security & Architecture Principles:
1. **Git Isolation:** Stored in the user's home configuration directory (`~/.config/eling`), **never** inside a project `.eling/` folder, ensuring API keys cannot be accidentally committed to source control.
2. **Domain Separation:** Completely decoupled from Eling Desktop's `agent-provider.json` so the headless MCP server does not depend on desktop application state.
3. **Auto-Template:** If the configuration file is missing or deleted, the backend automatically scaffolds a clean, indented template on startup.
4. **Credential Redaction:** API keys are never exposed in log outputs or API responses (represented solely by a boolean `hasApiKey`). Outgoing error strings from providers are automatically sanitized to mask tokens and authorization headers.

---

## 5. Observability & Telemetry

### Inspecting `memory_save` Responses:
Check the `reason` property in the response:
- `judge: <relation> into '<id>' (confidence <score>) - <reason>`: Judge successfully evaluated and resolved the relationship.
- `judge-failed: <error detail>; fell back to the heuristic decision`: Judge timed out or encountered an error; fallback heuristic was applied.
- `new-memory: judge enabled, no candidate to judge`: FTS recall found no candidates; memory was created without calling the LLM.

### Reading Runtime Logs:
Backend logs are streamed to the central log directory:
📂 `~/.local/share/eling/logs/backend.log`

To monitor judge events in real time:
```powershell
Get-Content "$env:USERPROFILE\.local\share\eling\logs\backend.log" -Wait -Tail 30 | Select-String "Judge"
```
Example log output:
```text
[DBG] Judge calling model=google for 4 candidate(s) of type "Note"
[INF] Judge attempt 1 failed (TimeoutException); retrying with next timeout
[INF] Judge verdict "Revision" confidence=0.95 target=01m4be9j... after 8708 ms (4 candidate(s))
[INF] Saved memory '01m4be9j...' with action '"Updated"' (judge: revision into '01m4be9j...' ...)
```
