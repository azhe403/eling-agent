# Spec: Fitur Export Backup Memori (Zip Archive)

- **Tanggal**: 2026-09-18
- **Status**: Draft (Menunggu Review)
- **Topik**: Backup & Export Memori Eling

---

## 1. Ringkasan & Tujuan
Menyediakan fitur untuk mengekspor seluruh atau sebagian memori Eling ke dalam format arsip `.zip`. Arsip ini memuat file markdown mentah (`.md`) beserta manifest metadata JSON yang dapat digunakan untuk keperluan backup, migrasi, dan audit mandiri tanpa kehilangan metadata aslinya.

---

## 2. Struktur Arsip Backup (.zip)

Struktur file di dalam arsip `.zip`:

```text
eling-backup-{scope}-{timestamp}.zip
├── manifest.json
└── memories/
    ├── {memory-id-1}.md
    ├── {memory-id-2}.md
    └── ...
```

### Format `manifest.json`
```json
{
  "version": "1.0",
  "exportedAt": "2026-09-18T10:00:00Z",
  "scope": "project",
  "projectName": "Eling",
  "totalMemories": 42,
  "memories": [
    {
      "id": "01m2td4jfc9b0qhkpvhsdvk39t",
      "type": "fact",
      "status": "active",
      "tags": ["backup", "export"],
      "fileName": "01m2td4jfc9b0qhkpvhsdvk39t.md"
    }
  ]
}
```

---

## 3. Komponen & Arsitektur

### 3.1. Core Layer (`Eling.Core`)
- **`IMemoryBackupService`**:
  - Metode: `Task<BackupExportResult> ExportZipAsync(BackupExportOptions options, Stream outputStream, CancellationToken ct = default)`
  - Parameter `BackupExportOptions`:
    - `Scope`: `project` | `global` | `merged`
    - `Status`: filter status (`active` | `all`, default `all`)
    - `IncludeIntentions`: `bool` (opsional jika ada file intentions)
- Menggunakan `System.IO.Compression.ZipArchive` bawaan .NET (tanpa dependensi NuGet pihak ketiga).
- Membaca memori langsung melalui `IMemoryStorage` yang sudah ada untuk setiap scope yang diminta.

### 3.2. Backend & HTTP API (`Eling.Backend`)
- **Endpoint**: `GET /api/memories/backup/export`
  - Query parameter:
    - `scope` (default: `project`)
    - `status` (default: `all`)
  - Response:
    - `Content-Type: application/zip`
    - `Content-Disposition: attachment; filename="eling-backup-{scope}-{yyyyMMdd-HHmmss}.zip"`
- **MCP Tool**: `eling_dev_memory_backup_export`
  - Argument: `scope` (string, opsional), `destinationPath` (string, opsional).
  - Jika `destinationPath` tidak diisi, simpan ke direktori backup default atau kembalikan info base64/path file.

### 3.3. Web Dashboard (`Eling.Dashboard`)
- Tambahkan tombol **"Export Backup"** pada toolbar atau halaman `/dashboard/memories`.
- Handler download browser menggunakan `fetch` ke `/api/memories/backup/export` dengan blob download.

### 3.4. Desktop App (`Eling.Desktop`)
- Tambahkan opsi **"Export Backup (.zip)"** pada toolbar `MemoriesView` / `SettingsView`.
- Menggunakan `IStorageProvider.SaveFilePickerAsync` Avalonia untuk memilih lokasi simpan file `.zip`.

---

## 4. Penanganan Error & Validasi
- **Scope kosong / tidak ada memori**: Tetap menghasilkan `.zip` valid dengan `manifest.json` berisi `totalMemories: 0`.
- **Project scope belum diinisialisasi**: Mengembalikan error informatif (`ProjectScopeNotInitializedException` atau 400 Bad Request).
- **Pembatalan / CancellationToken**: Memastikan file partial/stream dibersihkan jika operasi dibatalkan.

---

## 5. Rencana Pengujian (Testing)
- **Unit Test (`Eling.Core.Tests`)**:
  - `ExportZipAsync_WritesValidZipArchiveWithManifestAndMemories`: Verifikasi isi zip dan file `.md`.
  - `ExportZipAsync_RespectsScopeFiltering`: Pastikan pemisahan scope global vs project akurat.
- **Integration Test (`Eling.Desktop.Tests` / API Tests)**:
  - Verifikasi response HTTP header dan validitas stream `.zip`.
