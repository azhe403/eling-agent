# Dropdown limit + pagination di halaman Databases — design (2026-11-12)

## Intent / why

Data tab di halaman Databases mengambil baris dengan `LIMIT 100` yang di-hardcode
(`useTableData.ts`). Pembaca tidak bisa mengatur jumlah baris dan tidak bisa
berpindah halaman. Permintaan (berkembang di percakapan): dropdown limit yang diingat
per tab + default dinamis, **sekalian pagination**.

## Goals

- Dropdown limit di footer bawah grid, menyatu dengan bar "Total rows" yang
  sudah ada (varian B dari mockup; toolbar tidak diubah). Opsi:
  50 / 100 / 250 / 500 / 1000.
- Limit diingat **per tab** dan menjadi **page size** pagination.
- Default tab baru = **mode** (nilai paling sering dipakai) dari limit tab-tab
  terbuka; seri → nilai lebih besar; belum ada tab → 100. Hanya tab tabel
  (bukan Redis) yang dihitung.
- Pagination **Prev/Next + indikator "Halaman X dari Y"** di bawah grid
  (di dalam Card), Y dari `COUNT(*)` yang sudah di-fetch.
- Fetch server-side: `LIMIT n OFFSET (page-1)*n`; `maxRows` = n.
- Halaman diingat per tab (disimpan di `DbTab`); ganti limit → halaman kembali
  ke 1.
- Berlaku untuk Data tab saja. SQL runner dan Redis browser tidak diubah.
- **Tanpa ORDER BY** (keputusan user): urutan antar halaman bisa bergeser;
  diterima apa adanya.

## Non-goals / anti-goals

- SQL runner: tetap `maxRows: 100`, auto-append `LIMIT 100`, tanpa pagination.
- Redis browser: tidak disentuh.
- Tidak ada paginasi SQL-tab, tidak ada persist ke localStorage/server.
- Tidak ada ORDER BY, tidak ada sorting yang bisa dipilih.
- Tidak ada "go to page N" input — hanya Prev/Next + info halaman.

## Constraints

- Konvensi dashboard: `@/components/ui/select` (seperti Logs/Judge), toolbar
  yang sudah ada di `DataGrid.tsx`, props lewat `ExplorerPanels`.
- `useTableData` tetap pemilik logika fetch; `DataGrid` presentational.
- Poll `fromCache` (POLL_MS) berjalan pada halaman & limit tab aktif (dibaca
  dari `activeTableRef` saat poll berjalan).
- Request number guard yang ada tetap berlaku: jawaban request lama tidak
  menimpa yang baru.

## Approaches

1. **State per tab di `DbTab` (`browseLimit?`, `browsePage?`) (dipilih).**
   Tab adalah satuan state halaman ini; mode untuk default baru gampang dari
   `openTabs`; switching tab otomatis membawa limit+halaman. Trade-off: `DbTab`
   (types.ts) dapat dua field opsional; keduanya diabaikan `tabKey` dan Redis.
2. **Map terpisah `Record<tabKey, {limit, page}>` di page state.** Ditolak:
   dua sumber kebenaran, harus dijaga sinkron saat tab ditutup.
3. **Limit global + pagination state global.** Ditolak oleh user (limit per
   tab).
4. **Pagination client-side (fetch semua, potong di browser).** Ditolak:
   tabel besar mematikan; `COUNT(*)` + OFFSET server-side sudah didukung
   endpoint query.
5. **Load-more / infinite scroll.** Ditawarkan, user memilih Prev/Next.

## How (diputuskan di percakapan)

- `DbTab` dapat `browseLimit?: number` dan `browsePage?: number`;
  efektif = `browseLimit ?? 100` dan `browsePage ?? 1`.
- `browseData(id, table, database, ...)`:
  - baca `limit` dan `page` dari `activeTableRef.current`;
  - `SELECT * FROM <t> LIMIT <limit> OFFSET <(page-1)*limit>`; `maxRows: limit`;
  - `COUNT(*)` tetap di-fetch paralel → `browseTotal`;
  - offset tidak valid (page > totalPages setelah refresh): clamp ke halaman
    valid terakhir dan refetch sekali (atau biarkan Next disabled — builder yang
    tentukan, catat di commit).
- Mengganti dropdown limit: set `browseLimit` pada tab aktif, reset `browsePage`
  ke 1, refetch.
- Prev/Next: set `browsePage` ± 1 pada tab aktif, refetch. Kontrol limit dan
  Prev/Next semuanya di footer bawah tabel (dalam Card, di bar Total rows yang
  sudah ada) — keputusan user (varian B): toolbar tidak diubah. Layout footer:
  kiri "Total rows: N (showing M)", kanan berurutan: `[Rows 100 ▾]` `‹`
  "Halaman X dari Y" `›`. Tombol ‹/› kecil (24px), `Halaman X dari Y` tabular
  nums. Select memakai `@/components/ui/select`.
- DataGrid hanya presentational: props `browseLimit`, `browsePage`,
  `onBrowseLimitChange`, `onPageChange` (plus `browseTotal` yang sudah ada).
- Wiring: `useTableData` → `Databases.tsx` → `ExplorerPanels` → `DataGrid`.
- Mode untuk default tab baru: satu pass di `useExplorerTabs` atas tab tabel
  terbuka (`browseLimit ?? 100`), seri → nilai lebih besar, kosong → 100.

## Left to the builder

- Styling Select/footer agar serasi (ukuran teks, gap, posisi tombol).
- Mekanisme update `browseLimit`/`browsePage` pada tab aktif: update `openTabs`
  + `activeTableRef` (pola yang sama dipakai `closeTab`/`closeAllTabs`).
- Clamp page saat total menyusut (lihat di atas).
- Verifikasi manual: dev server 4427 + backend 4417; minimal
  `pnpm --filter eling-dashboard exec tsc --noEmit` (JANGAN `pnpm build`
  selama dev server jalan).
