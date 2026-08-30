---
id: 01m1392mqdc57dx10cpepant1h
type: lesson
status: active
tags:
- eling
- test
- isolation
- dashboard
- runtime
created_at: 2026-08-28T04:12:04.2057807+00:00
updated_at: 2026-08-28T04:12:04.2057807+00:00
source:
---
Eling project: bug/isolasi test Host. Dashboard test yang baru lahir me-resolve UserScope dari env ELING_USER_SCOPE lalu RuntimeRegistry.SyncFromDisk() membaca file runtime *.json dari folder runtime milik user scope tersebut. Karena Eling aktif (OpenChamber) menulis runtime ke ~/.config/eling/runtime global, dashboard test ikut memuat runtime lama → test First_runtime_starts_dashboard_and_registers_itself gagal (Assert.Single mengambil runtime lama 23104). Solusi: TestProcesses.cs set ELING_USER_SCOPE ke folder temp terpisah (CreateTestUserScope) di semua spawned process, sehingga dashboard test benar-benar terisolasi dari instance Eling yang sedang berjalan. Ganti port TIDAK cukup karena masalahnya di runtime dir global, bukan port (port sudah otomatis bebas via FindFreePort).