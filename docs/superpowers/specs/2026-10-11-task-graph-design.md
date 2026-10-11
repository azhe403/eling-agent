# Task Graph Replacement — Design

Date: 2026-10-11
Status: draft (fase 3, setelah session + klaim stabil)
Goal: gantikan `.tmp/tasks/<fitur>/task.json + subtask_NN.json` dengan task terstruktur di Eling.

## Scope
- Sumber benar: project-local untuk status hidup (pending/in_progress/completed), dependensi `depends_on`, flag `parallel`.
- Spec dan hasil akhir yang awet: ringkasannya naik ke project memory, detail mentah tetap di runtime kalau besar.
- Transisi: `.tmp/tasks/` tetap ditulis sebagai mirror sampai semua writer pindah.

## Model
- `task_id`, `feature`, `session_id`, `status`, `depends_on[]`, `parallel`, `deliverables[]`, `updated_at`.
- Update status harus atomik per subtask; tidak boleh dua chat menandai subtask sama completed secara buta.

## Tools (baru, minimal)
- `task_create(feature, session_id, subtasks[])` → buat graph awal.
- `task_update(subtask_id, status, deliverables)` → update atomik + heartbeat session.
- `task_get(feature)` / `task_list(session_id)` → baca status gabungan.
- `task_close(feature)` → kunci sebagai selesai.

## Aturan eksekusi
- Batch pararel hanya untuk subtask tanpa dependensi yang belum puas.
- Validasi tiap batch sebelum lanjut batch berikut (ikut pola incremental yang sudah ada).

## Validasi
- Uji satu fitur 3 subtask dua chat: tidak ada double-claim subtask, status akhir konsisten.
