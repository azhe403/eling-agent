# Specification: Memory Backup Export Feature (Zip Archive)

- **Date**: 2026-09-18
- **Status**: Proposed / Draft
- **Topic**: Eling Memory Backup & Export

---

## 1. Overview & Objectives
Provide a feature to export all or scoped subsets of Eling memories into a compressed `.zip` archive. This archive contains raw Markdown files (`.md`) accompanied by a JSON metadata manifest, enabling offline backup, cross-machine migration, and independent audits without losing original ULIDs, timestamps, or tag metadata.

---

## 2. Backup Archive Structure (`.zip`)

Directory layout inside the exported `.zip` archive:

```text
eling-backup-{scope}-{timestamp}.zip
├── manifest.json
└── memories/
    ├── {memory-id-1}.md
    ├── {memory-id-2}.md
    └── ...
```

### `manifest.json` Format
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

## 3. Components & Architecture

### 3.1. Core Layer (`Eling.Core`)
- **`IMemoryBackupService`**:
  - Method: `Task<BackupExportResult> ExportZipAsync(BackupExportOptions options, Stream outputStream, CancellationToken ct = default)`
  - Parameter `BackupExportOptions`:
    - `Scope`: `project` | `global` | `merged`
    - `Status`: status filter (`active` | `all`, default: `all`)
    - `IncludeIntentions`: `bool` (optional if intentions exist)
- Leverages built-in .NET `System.IO.Compression.ZipArchive` without introducing third-party NuGet dependencies.
- Reads memories directly via existing `IMemoryStorage` implementations for each requested scope.

### 3.2. Backend & HTTP API (`Eling.Backend`)
- **Endpoint**: `GET /api/memories/backup/export`
  - Query parameters:
    - `scope` (default: `project`)
    - `status` (default: `all`)
  - Response:
    - `Content-Type: application/zip`
    - `Content-Disposition: attachment; filename="eling-backup-{scope}-{yyyyMMdd-HHmmss}.zip"`
- **MCP Tool**: `eling_dev_memory_backup_export`
  - Arguments: `scope` (string, optional), `destinationPath` (string, optional).
  - If `destinationPath` is omitted, writes to the default backup directory or returns base64 / path details.

### 3.3. Web Dashboard (`Eling.Dashboard`)
- Add an **"Export Backup"** action button to the toolbar or `/dashboard/memories` view.
- Browser download handler uses `fetch` against `/api/memories/backup/export` triggering a blob download.

### 3.4. Desktop App (`Eling.Desktop`)
- Add an **"Export Backup (.zip)"** option in the `MemoriesView` / `SettingsView` toolbar.
- Uses Avalonia's `IStorageProvider.SaveFilePickerAsync` to let users pick the destination `.zip` file location.

---

## 4. Error Handling & Validation
- **Empty scope / zero memories**: Produces a valid `.zip` containing a `manifest.json` with `totalMemories: 0`.
- **Uninitialized project scope**: Returns an informative error (`ProjectScopeNotInitializedException` or 400 Bad Request).
- **Cancellation / `CancellationToken`**: Ensures partial streams and temporary files are cleaned up immediately if the operation is aborted.

---

## 5. Testing Strategy
- **Unit Tests (`Eling.Core.Tests`)**:
  - `ExportZipAsync_WritesValidZipArchiveWithManifestAndMemories`: Verifies ZIP contents and embedded `.md` files.
  - `ExportZipAsync_RespectsScopeFiltering`: Confirms strict separation between global vs. project scope.
- **Integration Tests (`Eling.Desktop.Tests` / Backend API Tests)**:
  - Validates HTTP response headers and stream integrity of generated `.zip` payloads.
