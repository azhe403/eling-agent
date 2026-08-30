---
id: 01m1392aqj6bjrrbda5g9qw7nx
type: decision
status: active
tags:
- eling
- memory
- dedup
- architecture
created_at: 2026-08-28T04:11:53.9752863+00:00
updated_at: 2026-08-28T04:11:53.9752863+00:00
source:
---
Eling project: MemoryService.SaveAsync sekarang melakukan dedup otomatis berbasis konten. Saat save, ia menormalkan konten (trim) lalu mencari memory ber-status Active dengan konten identik (case-insensitive). Jika ditemukan → update in-place (merge tags union, prefer source dari yang baru, updatedAt=now) tanpa insert duplikat. Jika tidak → insert baru. Dedup hanya berlaku untuk Active, bukan Archived/Superseded. Keputusan: dedup pakai perbandingan string normalisasi (bukan embedding semantik) agar tetap self-contained/offline tanpa infra embedding.