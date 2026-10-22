/**
 * How the dashboard spells a byte count.
 *
 * Two shapes, because there are two kinds of number: one process's working
 * set reads better in megabytes, and a platform total crosses into
 * gigabytes long before it crosses into terabytes.
 */

const MEGABYTE = 1024 * 1024
const GIGABYTE = 1024 * MEGABYTE

/** Working set in MB, one decimal. Bytes are what the OS reports. */
export function formatMegabytes(bytes: number): string {
  return `${(bytes / MEGABYTE).toFixed(1)} MB`
}

/** The same number in the largest unit that still reads as a number: GB, else MB. */
export function formatBytes(bytes: number): string {
  return bytes >= GIGABYTE
    ? `${(bytes / GIGABYTE).toFixed(1)} GB`
    : formatMegabytes(bytes)
}
