# File Claim Replacement — Design (Fase 1, dimenangkan dulu)

Date: 2026-10-11
Status: draft aktif
Goal: penanda milik antar chat di branch main yang sama biar tidak cakar-cakaran. Gantikan fungsi implisit `.tmp/checkpoints/*.txt` + cek manual git status.

## Scope
- Sumber benar: project-local (tidak di-commit). Ground truth isi tetap filesystem + git.
- Granularitas fase 1: level file. Level hunk/baris ditunda.
- Sifat: lunak dulu (peringatan), mengeras jadi penolakan setelah stabil.

## Model
- `claim_id`, `session_id`, `branch`, `paths[]`, `since`, `last_heartbeat`, `ttl_seconds` (default 300).
- Klaim hidup kalau `now - last_heartbeat <= ttl`; selain itu dianggap basi dan hilang dari list aktif.

## Tools (baru, minimal)
- `file_claim(session_id, paths[])` → klaim lunak, gagal lunak kalau diklaim session hidup lain (kembalikan pemilik).
- `file_release(session_id, paths[])` → lepas eksplisit; `session_close` melepas semua.
- `file_list_active(branch)` → daftar klaim hidup per branch.
- `file_check_conflict(session_id, paths[])` → gabungkan klaim aktif + git dirty + mtime untuk putusan tulis/jangan.

## Panduan agent (server saja)
- ServerInstructions: wajib `file_check_conflict` sebelum tulis file yang bukan diklaim sendiri; wajib heartbeat tiap ganti step; wajib `file_release` saat selesai.
- Tidak ubah AGENTS.md di fase ini.

## Error handling
- Session mati mendadak → klaim basi otomatis, tidak perlu cleanup manual.
- Dua klaim barengan → yang pertama menang, yang kedua dapat pesan pemilik + umur klaim.
- Git kotor tanpa klaim → tampilkan sebagai peringatan, bukan error keras di fase lunak.

## Validasi
- Simulasi dua chat di main pegang file sama: chat kedua dapat peringatan pemilik yang benar.
- Klaim basi (heartbeat berhenti) hilang dari list dalam satu TTL.
