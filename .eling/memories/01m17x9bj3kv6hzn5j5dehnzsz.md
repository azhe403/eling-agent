---
id: 01m17x9bj3kv6hzn5j5dehnzsz
type: decision
status: active
tags:
- sim_crud
- demo_long_text
- architecture_guide
- event_driven
- sse_realtime
created_at: 2026-08-29T23:22:13.4448544+00:00
updated_at: 2026-08-29T23:22:13.4448544+00:00
source:
---
[Deep Architecture Guide: Event-Driven Memory Synchronizer]

Dalam perancangan sistem asinkron berskala besar, koordinasi antara Model Context Protocol (MCP) daemon, background processes, dan antarmuka web (Dashboard Control Plane) membutuhkan integrasi multi-tier yang tangguh dan deterministik.

Bagian I: Ingestion Pipeline & Storage Immutability
Setiap entitas memori diidentifikasi menggunakan ULID (Universally Unique Lexicographically Sortable Identifier) yang memiliki entropi acak 128-bit dan presisi waktu tingkat milidetik. Representasi kanonis data disimpan langsung ke subdirektori `.eling/memories/` dalam format Markdown human-readable yang di-track secara native oleh sistem Git. Hal ini menjamin bahwa setiap riwayat keputusan teknis memiliki audit trail yang tidak dapat dimanipulasi serta portabel di berbagai mesin pengembang.

Bagian II: SQLite FTS5 Inverted Index & Token Ranking
Secara bersamaan, mesin pencari internal mengeksekusi operasi indexing menuju basis data SQLite lokal (`index.db`) dengan tokenizer unicode61. Algoritma scoring BM25 digunakan untuk menghitung probabilitas relevansi kata kunci pencarian secara instan. Integrasi ini memberikan kecepatan pencarian sub-milidetik tanpa membebani alokasi memori sistem.

Bagian III: Realtime Server-Sent Events (SSE) & Zero-Polling
Setiap mutasi data (Create, Update, Delete, Promote, Copy) secara otomatis memicu notifikasi internal melalui `Channel<string>` menuju endpoint HTTP `/api/events/memories`. Antarmuka frontend Next.js yang tersambung melalui `EventSource` akan menangkap payload mutasi dan memperbarui tampilan tabel secara real-time tanpa perlu melakukan polling manual atau refresh browser.

Bagian IV: Rekomendasi Deployment & Disaster Recovery
1. Selalu jalankan kompilasi dengan isolasi direktori artefak (`.bin/` untuk development dan `.bin-test/` untuk unit test suites).
2. Pastikan file kunci rahasia dan konfigurasi lingkungan lokal dikecualikan melalui aturan `.gitignore` yang ketat.
3. Lakukan rotasi berkala pada berkas audit log guna menjaga stabilitas performa sistem dalam jangka panjang.