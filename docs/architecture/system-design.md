# System Design Document

This is an **external** document (in `docs/` not `.eling/docs/`).

External documents:
- Are read-only in the dashboard
- Have inferred metadata (no front-matter required)
- Cannot be edited, annotated, or promoted
- Show "External" ownership badge

## Architecture Principles

1. **Separation of concerns** — Backend owns data, frontend owns presentation
2. **Event-driven** — SSE for real-time updates
3. **Type-safe** — Shared TypeScript types mirror backend DTOs
