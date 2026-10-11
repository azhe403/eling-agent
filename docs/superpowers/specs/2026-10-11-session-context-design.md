# Session Context Replacement — Design

Date: 2026-10-11
Status: draft (fase 2, setelah klaim file menang)
Goal: gantikan `.tmp/sessions/*/context.md` dengan progress hidup di Eling.

## Scope
- Sumber benar: project-local (mesin saja, tidak di-commit, shared antar worktree satu repo).
- Blob besar (> ~8KB, log panjang): `.eling/runtime/sessions/<session-id>/`, gitignore.
- Yang awet saja (keputusan, pelajaran): naik ke project memory via `memory_save` biasa.
- Selama transisi: `.tmp/sessions/` tetap ditulis sebagai mirror read-only, bukan sumber benar.

## Model
- `session_id`: slug `YYYY-MM-DD-<topik>`, unik per chat.
- `branch`, `working_files[]`, `current_step`, `next_intent`, `updated_at`, `ttl_seconds`.
- Heartbeat: refresh `updated_at` tiap ganti step; dianggap basi kalau `now - updated_at > ttl`.

## Tools (baru, minimal)
- `session_register(session_id, branch, ttl)` → buat atau aktifkan lagi.
- `session_heartbeat(session_id, current_step, next_intent, working_files)` → overwrite ringan.
- `session_get(session_id)` / `session_list(active_only=true)` → baca + list.
- `session_close(session_id)` → tandai selesai, klaim ikut lepas.

## Panduan agent
- Hanya lewat ServerInstructions + deskripsi tool, tanpa ubah AGENTS.md.
- Aturan: recall tiap ganti step, heartbeat tiap ganti step, cek konflik sebelum tulis file orang.

## Validasi fase 1
- Dua chat simulasi di main: klaim tidak tabrakan, session basi hilang dari list, recall tidak berisik.
- Kriteria lolos: tidak ada overwrite diam-diam antar chat dalam uji.
