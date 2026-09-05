# Memakai Eling — Petunjuk Singkat

Eling (Jawa: *ingat / to remember*) adalah lapisan memori untuk proyek. Cukup ajak Eling lewat kata kunci **`eling`** saat ngobrol dengan agent — sisanya diproses otomatis.

---

## Kata kunci utama

| Kalau kamu mau | Ucapkan |
|---|---|
| **Simpan** sesuatu ke memori | `eling simpan <informasi>` |
| **Ingat / recall** sesuatu | `eling <sesuatu>` (mis. `eling cara build`) |
| **Tanya** cara/kondisi tertentu | `eling gimana cara ...` / `eling kapan ...` |
| **Cari** memori yang ada | `eling cari <topik>` |
| **Lihat** daftar memori | `eling list` |
| **Hapus** memori | `eling hapus <id/topik>` |

---

## Contoh pemakaian

- `eling simpan cara build project: jalankan pnpm dev:backend` → menyimpan prosedur build.
- `eling cara build` → memanggil kembali prosedur build yang tadi disimpan.
- `eling gimana mastiin FE dev selalu nyala` → memunculkan catatan/konfigurasi terkait dev server.
- `eling cari command test unit` → mencari semua memori soal test unit.

> Tips: buat info **durable** (keputusan, prosedur, aturan tetap) — bukan request sekali jalan. Info sementara tidak perlu disimpan.

---

## Skala memori: proyek vs global

- **Project**: memori yang hanya berlaku di proyek ini (arsitektur, workflow lokal, keputusan proyek). Ini default.
- **Global**: memori lintas proyek (preferensi pribadi, aturan umum, setup CLI) — pakai `eling simpan global <info>`.

---

## Prinsip singkat

1. **Ingat dulu sebelum kerja** — mulai sesi dengan `eling <konteks>` biar agent punya konteks.
2. **Simpan yang penting** — keputusan, prosedur, perbaikan yang berulang.
3. **Jangan simpan sensitif** — hindari menyimpan credential / rahasia / path absolut disarankan tidak dimasukkan ke memori project.

---

_Untuk detail teknis & tool lengkap, lihat `AGENTS.md` dan dokumen design di `docs/superpowers/`._
