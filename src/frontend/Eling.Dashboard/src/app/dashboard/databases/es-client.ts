/**
 * The Elasticsearch read client of the dashboard: one fetcher per route of
 * the cluster read surface.
 *
 * Everything here is thin: the shapes are the wire shapes of
 * /api/databases/{id}/es/... (see `types.ts`), and every fetch is read-only.
 * A non-2xx answer comes back as `{ status, message }` so the panel can show
 * the server's own words.
 */

import { API } from "./utils/connection-helpers"
import { ApiError, apiFetch } from "@/lib/api"
import type {
  ElasticsearchCountResult,
  ElasticsearchDocumentPage,
  ElasticsearchHistogram,
  ElasticsearchIndexDetail,
  ElasticsearchIndexInfo,
  ElasticsearchSearchResult,
  EsDocumentsOpts,
  EsHistogramOpts,
  EsSearchBody,
} from "./types"

/** One door to the ES routes: unwrap the standard envelope; rethrow refusals as `{ status, message }`. */
const esFetch = async <T>(path: string, init?: RequestInit): Promise<T> => {
  try {
    return await apiFetch<T>(path, init)
  } catch (e) {
    if (e instanceof ApiError) throw { status: e.status, message: e.message }
    throw e
  }
}

/**
 * The indices of the cluster, ordered by name (the backend orders them).
 *
 * The tree filters what it draws with `visibleIndices`; pass `includeSystem`
 * to get the dot-prefixed system indices back.
 */
export const fetchEsIndices = (connId: string, includeSystem = false): Promise<ElasticsearchIndexInfo[]> => {
  const qs = includeSystem ? "?includeSystem=true" : ""
  return esFetch<ElasticsearchIndexInfo[]>(`${API}/${connId}/es/indices${qs}`)
}

/** The mappings and settings of one index, plus its stats. */
export const fetchEsIndex = (connId: string, index: string): Promise<ElasticsearchIndexDetail> =>
  esFetch<ElasticsearchIndexDetail>(
    `${API}/${connId}/es/index?${new URLSearchParams({ index }).toString()}`,
  )

/** The optional filters a documents fetch carries, as query string pairs. */
const optionalQs = (qs: URLSearchParams, opts: EsDocumentsOpts | EsHistogramOpts) => {
  if (opts.q) qs.set("q", opts.q)
  if (opts.start) qs.set("start", opts.start)
  if (opts.end) qs.set("end", opts.end)
  if (opts.field) qs.set("field", opts.field)
  if (opts.filters) qs.set("filters", opts.filters)
  if (opts.logic) qs.set("logic", opts.logic)
}

/**
 * One page of documents of an index: the page, ordered by _doc ascending,
 * with the total count of the whole index.
 */
export const fetchEsDocuments = (
  connId: string,
  index: string,
  opts: EsDocumentsOpts,
): Promise<ElasticsearchDocumentPage> => {
  const qs = new URLSearchParams({ index, from: String(opts.from), size: String(opts.size) })
  optionalQs(qs, opts)
  return esFetch<ElasticsearchDocumentPage>(`${API}/${connId}/es/documents?${qs.toString()}`)
}

/** The auto-date-histogram of an index: buckets of the chosen width. */
export const fetchEsHistogram = (
  connId: string,
  index: string,
  opts: EsHistogramOpts,
): Promise<ElasticsearchHistogram> => {
  const qs = new URLSearchParams({ index })
  if (opts.buckets !== undefined) qs.set("buckets", String(opts.buckets))
  optionalQs(qs, opts)
  return esFetch<ElasticsearchHistogram>(`${API}/${connId}/es/histogram?${qs.toString()}`)
}

/** The raw DSL console: totals, hits and aggregations, verbatim. */
export const fetchEsSearch = (
  connId: string,
  index: string,
  body: EsSearchBody,
): Promise<ElasticsearchSearchResult> =>
  esFetch<ElasticsearchSearchResult>(`${API}/${connId}/es/search`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ index, ...body }),
  })

/** The count of the documents a raw query clause matches. */
export const fetchEsCount = (
  connId: string,
  index: string,
  query?: unknown,
): Promise<ElasticsearchCountResult> =>
  esFetch<ElasticsearchCountResult>(`${API}/${connId}/es/count`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ index, query }),
  })
