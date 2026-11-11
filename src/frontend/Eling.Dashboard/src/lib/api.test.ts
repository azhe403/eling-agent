import { describe, it, expect, vi, beforeEach, afterEach } from "vitest"

import { apiFetch, ApiError, genericCode } from "./api"

const jsonResponse = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json" },
  })

describe("apiFetch", () => {
  const originalFetch = globalThis.fetch

  beforeEach(() => {
    vi.restoreAllMocks()
  })

  afterEach(() => {
    globalThis.fetch = originalFetch
  })

  it("unwraps data on a 200 envelope", async () => {
    globalThis.fetch = vi.fn(async () =>
      jsonResponse(200, { code: "ok", message: "The resource was read successfully from the server.", data: { hello: "world" } }),
    )
    const body = await apiFetch<{ hello: string }>("/api/health")
    expect(body).toEqual({ hello: "world" })
  })

  it("returns an array data payload untouched", async () => {
    globalThis.fetch = vi.fn(async () =>
      jsonResponse(200, { code: "ok", message: "x", data: ["a", "b"] }),
    )
    await expect(apiFetch<string[]>("/api/things")).resolves.toEqual(["a", "b"])
  })

  it("throws ApiError with envelope code/message on 404", async () => {
    globalThis.fetch = vi.fn(async () =>
      jsonResponse(404, { code: "connection_not_found", message: "Connection 'abc' is gone - the connection list is being refreshed.", data: null }),
    )
    const err = await apiFetch("/api/databases/abc/schema").catch((e: unknown) => e)
    expect(err).toBeInstanceOf(ApiError)
    const apiErr = err as ApiError
    expect(apiErr.status).toBe(404)
    expect(apiErr.code).toBe("connection_not_found")
    expect(apiErr.message).toContain("Connection 'abc'")
  })

  it("falls back to the status-derived code when the body has no code", async () => {
    globalThis.fetch = vi.fn(async () => new Response("boom", { status: 404, headers: { "content-type": "application/json" } }))
    const err = await apiFetch("/anything").catch((e: unknown) => e)
    expect((err as ApiError).status).toBe(404)
    expect((err as ApiError).code).toBe("not_found")
  })

  it("throws invalid_response on a non-envelope JSON body", async () => {
    globalThis.fetch = vi.fn(async () => jsonResponse(200, { ok: true }))
    const err = await apiFetch("/api/legacy").catch((e: unknown) => e)
    expect((err as ApiError).code).toBe("invalid_response")
  })

  it("throws invalid_response on the SPA fallback HTML content", async () => {
    globalThis.fetch = vi.fn(async () =>
      new Response("<!doctype html><html><head><title>Eling</title></head><body></body></html>", {
        status: 200,
        headers: { "content-type": "text/html" },
      }),
    )
    const err = await apiFetch("/api/this-route-does-not-exist").catch((e: unknown) => e)
    expect((err as ApiError).code).toBe("invalid_response")
  })

  it("resolves undefined on a 204 with no body", async () => {
    globalThis.fetch = vi.fn(async () => new Response(null, { status: 204 }))
    await expect(apiFetch<void>("/api/databases/abc")).resolves.toBeUndefined()
  })

  it("resolves undefined on an empty 200 body", async () => {
    globalThis.fetch = vi.fn(async () => new Response("", { status: 200, headers: { "content-type": "application/json" } }))
    await expect(apiFetch<Record<string, never>>("/api/health")).resolves.toBeUndefined()
  })
})

describe("genericCode", () => {
  it("maps common statuses", () => {
    expect(genericCode(400)).toBe("bad_request")
    expect(genericCode(403)).toBe("forbidden")
    expect(genericCode(404)).toBe("not_found")
    expect(genericCode(415)).toBe("unsupported_media_type")
    expect(genericCode(500)).toBe("internal_error")
    expect(genericCode(503)).toBe("internal_error")
  })
})
