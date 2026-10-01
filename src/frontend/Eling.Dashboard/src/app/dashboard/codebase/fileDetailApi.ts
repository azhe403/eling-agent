"use client"

/** A file selected in the result list, and the project that owns it. */
export type SelectedFile = {
  projectRoot: string
  path: string
}

type FileDetail = {
  projectRoot: string
  path: string
  size: number
  lastIndexedAt: string
  chunks: { startLine: number; endLine: number; content: string }[]
}

type FileContent = {
  projectRoot: string
  path: string
  status: "ok" | "notFound" | "tooLarge" | "binary" | "unreadable"
  size: number
  content: string | null
}

async function getJson<T>(url: string, signal?: AbortSignal): Promise<T> {
  const res = await fetch(url, { cache: "no-store", signal })
  if (!res.ok) throw new Error(await errorMessage(res))
  return (await res.json()) as T
}

async function errorMessage(res: Response): Promise<string> {
  // The endpoints answer with a plain-text reason, which is far more useful
  // than a bare status code — a rejected path names the offender.
  const body = await res.text().catch(() => "")
  return body.trim() || `API returned ${res.status}`
}

/** Every indexed chunk for the file, in line order. */
export function fetchFileDetail(
  file: SelectedFile,
  signal?: AbortSignal
): Promise<FileDetail> {
  return getJson<FileDetail>(fileUrl(file), signal)
}

function fileUrl(file: SelectedFile) {
  return `/api/codebase/file?path=${encodeURIComponent(file.path)}&project=${encodeURIComponent(file.projectRoot)}`
}

/**
 * The whole file as it is on disk right now. `project` is required here and
 * optional on the chunks endpoint: chunks come from the index, where the scope
 * selector can name several projects, but a disk read has to be told exactly
 * which one.
 */
export function fetchFileContent(
  file: SelectedFile,
  signal?: AbortSignal
): Promise<FileContent> {
  const url = `/api/codebase/file/content?path=${encodeURIComponent(file.path)}&project=${encodeURIComponent(file.projectRoot)}`
  return getJson<FileContent>(url, signal)
}

/** Human-readable explanation for a non-ok content status. */
export function describeStatus(status: FileContent["status"], size: number) {
  switch (status) {
    case "notFound":
      return "This file is no longer on disk. The index is out of date — rebuild to refresh it."
    case "tooLarge":
      return `This file is ${formatBytes(size)}, over the viewer's limit. The indexed chunks are still available.`
    case "binary":
      return "This file is binary, so there is no text to show."
    case "unreadable":
      return "This file could not be read — it may be locked or access-denied."
    default:
      return null
  }
}

export function formatBytes(bytes: number) {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}
