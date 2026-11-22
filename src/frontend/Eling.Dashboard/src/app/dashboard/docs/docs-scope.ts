"use client"

import { useSearchParams } from "next/navigation"

export type DocKindFilter = "all" | "spec" | "plan" | "doc" | "scratch"
export type DocOwnershipFilter = "all" | "managed" | "external"
export type DocOriginFilter = "all" | "current" | "ancestor" | "shard" | "global"

const KINDS: DocKindFilter[] = ["all", "spec", "plan", "doc", "scratch"]
const OWNERSHIPS: DocOwnershipFilter[] = ["all", "managed", "external"]
const ORIGINS: DocOriginFilter[] = ["all", "current", "ancestor", "shard", "global"]

export function docKinds(): DocKindFilter[] { return KINDS }
export function docOwnerships(): DocOwnershipFilter[] { return OWNERSHIPS }
export function docOrigins(): DocOriginFilter[] { return ORIGINS }

export function docKindLabel(k: DocKindFilter): string {
  if (k === "all") return "All"
  return k.charAt(0).toUpperCase() + k.slice(1)
}

export function docOwnershipLabel(o: DocOwnershipFilter): string {
  if (o === "all") return "All"
  return o.charAt(0).toUpperCase() + o.slice(1)
}

export function docOriginLabel(o: DocOriginFilter): string {
  if (o === "all") return "All"
  return o.charAt(0).toUpperCase() + o.slice(1)
}

/** Build the query string for /api/docs from the current filters. */
export function buildDocsQuery(params: URLSearchParams): string {
  const parts: string[] = []
  const kind = params.get("kind")
  const ownership = params.get("ownership")
  const origin = params.get("origin")
  const topic = params.get("topic")
  const limit = params.get("limit")
  const offset = params.get("offset")

  if (kind && kind !== "all") parts.push(`kind=${encodeURIComponent(kind)}`)
  if (ownership && ownership !== "all") parts.push(`ownership=${encodeURIComponent(ownership)}`)
  if (origin && origin !== "all") parts.push(`origin=${encodeURIComponent(origin)}`)
  if (topic) parts.push(`topic=${encodeURIComponent(topic)}`)
  if (limit) parts.push(`limit=${limit}`)
  if (offset) parts.push(`offset=${offset}`)

  return parts.join("&")
}

/** Parse the search params into a filter object. */
export function parseDocsFilters(searchParams: URLSearchParams) {
  const kind = (searchParams.get("kind") as DocKindFilter) ?? "all"
  const ownership = (searchParams.get("ownership") as DocOwnershipFilter) ?? "all"
  const origin = (searchParams.get("origin") as DocOriginFilter) ?? "all"
  const topic = searchParams.get("topic") ?? ""
  const limit = parseInt(searchParams.get("limit") ?? "50", 10)
  const offset = parseInt(searchParams.get("offset") ?? "0", 10)

  return { kind, ownership, origin, topic, limit, offset }
}

/** All available topics (from the list response topics). */
export function extractTopics(topics: { topic: string; documents: unknown[] }[]): string[] {
  return topics.map((t) => t.topic).filter((t) => t).sort()
}