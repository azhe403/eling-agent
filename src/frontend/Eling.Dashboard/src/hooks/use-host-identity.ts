"use client"

import * as React from "react"

/** Shown until the backend answers, and kept if it never does. */
const FALLBACK_NAME = "anonymous"

export type HostIdentity = {
  name: string
  /** Empty when git has no `user.email`; the UI hides the line. */
  email: string
}

/**
 * Read the display name and email from the backend's git-config identity
 * endpoint (`/api/system/identity`).
 *
 * The dashboard ships as a static export, so its prerendered HTML is built
 * without ever calling the API — the first paint always shows the fallback and
 * the real identity replaces it once this effect resolves. That is inherent to
 * the static build, not a defect in the fetch.
 *
 * Failures (backend down, aborted on unmount, unexpected shape) are swallowed:
 * cosmetic chrome is not worth an error boundary.
 */
export function useHostIdentity(): HostIdentity {
  const [identity, setIdentity] = React.useState<HostIdentity>({
    name: FALLBACK_NAME,
    email: "",
  })

  React.useEffect(() => {
    const base = process.env.NEXT_PUBLIC_API_URL ?? ""
    const url = base
      ? `${base.replace(/\/$/, "")}/api/system/identity`
      : "/api/system/identity"

    const controller = new AbortController()

    fetch(url, { signal: controller.signal })
      .then((res) => (res.ok ? res.json() : null))
      .then((data: unknown) => {
        const record = data as Record<string, unknown> | null
        // Tolerate either casing: the dashboard's other fetchers do the same,
        // since the serializer's casing is a backend detail.
        const read = (key: string) =>
          String(record?.[key] ?? record?.[key.charAt(0).toUpperCase() + key.slice(1)] ?? "").trim()

        const name = read("name")
        if (name) {
          setIdentity({ name, email: read("email") })
        }
      })
      .catch(() => {
        // Offline or aborted — the fallback already on screen stands.
      })

    return () => controller.abort()
  }, [])

  return identity
}
