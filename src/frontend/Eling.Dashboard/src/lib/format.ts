/**
 * How the dashboard spells a byte count.
 *
 * Two shapes, because there are two kinds of number: one process's working
 * set reads better in megabytes, and a platform total crosses into
 * gigabytes long before it crosses into terabytes.
 */

const KILOBYTE = 1024
const MEGABYTE = 1024 * KILOBYTE
const GIGABYTE = 1024 * MEGABYTE

/** Working set in MB, one decimal. Bytes are what the OS reports. */
export function formatMegabytes(bytes: number): string {
  return `${(bytes / MEGABYTE).toFixed(1)} MB`
}

/**
 * The same number in the largest unit that still reads as a number: GB, MB,
 * else KB.
 *
 * KB is the floor because the smallest thing this formats is a file on disk —
 * an index database can be a few hundred KB, and "0.0 MB" would say nothing.
 */
export function formatBytes(bytes: number): string {
  if (bytes >= GIGABYTE) return `${(bytes / GIGABYTE).toFixed(1)} GB`
  if (bytes >= MEGABYTE) return formatMegabytes(bytes)
  if (bytes >= KILOBYTE) return `${(bytes / KILOBYTE).toFixed(1)} KB`
  return `${bytes} B`
}
