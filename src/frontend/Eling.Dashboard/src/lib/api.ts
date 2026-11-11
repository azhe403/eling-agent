/**
 * The one door to the Eling API: every dashboard call goes through apiFetch.
 * It unwraps the standard response envelope so call sites work with the
 * payload, not with the envelope.
 *
 * Envelope (design docs/superpowers/specs/2026-11-11-standard-envelope-design.md):
 *   success { code: "ok", message: "...", data: <payload> }
 *   failure { code: "snake_case", message: "human sentence", data: null }
 *
 * The helper never invents a shape: a 2xx that is not the envelope is an
 * error (a stale backend serving the SPA to /api paths answers 200 HTML),
 * not a quiet misread.
 */
export class ApiError extends Error {
  readonly status: number
  readonly code: string

  constructor(status: number, code: string, message: string) {
    super(message)
    this.name = "ApiError"
    this.status = status
    this.code = code
  }
}

/** Generic codes derived from HTTP status, used when the body carries no code. */
export const genericCode = (status: number): string => {
  switch (status) {
    case 400: return "bad_request"
    case 401: return "unauthorized"
    case 403: return "forbidden"
    case 404: return "not_found"
    case 405: return "method_not_allowed"
    case 409: return "conflict"
    case 413: return "payload_too_large"
    case 414: return "uri_too_long"
    case 415: return "unsupported_media_type"
    case 429: return "too_many_requests"
    default: return status >= 500 ? "internal_error" : "http_error"
  }
}

const isEnvelope = (body: unknown): body is { code: string; message: string; data: unknown } =>
  typeof body === "object" &&
  body !== null &&
  typeof (body as { code?: unknown }).code === "string" &&
  typeof (body as { message?: unknown }).message === "string" &&
  "data" in body

/**
 * Reads one API response. Throws ApiError on any non-2xx (message from the
 * envelope's root message, code from its root code or generic by status);
 * on a 2xx whose body is not the envelope, throws ApiError with code
 * "invalid_response". On 204 or an empty 2xx body, resolves `undefined`.
 */
export async function apiFetch<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(path, init)
  const text = await res.text()
  let body: unknown = null
  if (text.length > 0) {
    try {
      body = JSON.parse(text)
    } catch {
      body = null
    }
  }

  if (!res.ok) {
    const record = body && typeof body === "object" ? (body as Record<string, unknown>) : null
    const code = typeof record?.code === "string" ? record.code : genericCode(res.status)
    const message =
      typeof record?.message === "string" && record.message.length > 0
        ? record.message
        : text.trim() || `HTTP ${res.status}`
    throw new ApiError(res.status, code, message)
  }

  if (text.length === 0) return undefined as T
  if (!isEnvelope(body)) {
    throw new ApiError(
      res.status,
      "invalid_response",
      "the server answered with a response this client does not recognize - the backend may predate this dashboard",
    )
  }
  return body.data as T
}
