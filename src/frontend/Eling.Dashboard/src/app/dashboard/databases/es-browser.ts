/**
 * The Elasticsearch index browser: the labels and the keys of the tree rows
 * and the visibility rule for the system indices.
 *
 * Everything here is pure - the decisions about which index a row names and
 * which indices the tree draws can be tested without a server and without a
 * cluster.
 */

import type { ElasticsearchIndexInfo } from "./types"

/**
 * The label a tree row shows for an index: the index name itself. The
 * subtitle carries the stats, so the name is all the row needs.
 */
export const esIndexLabel = (index: string): string => index

/**
 * How many documents an index holds, spelled out like Redis's "N keys": a
 * bare number next to an index name reads as a name, not a count.
 */
export const esIndexDocs = (count: number): string => `${count} docs`

/**
 * The key that names one index tab: per connection and per index, so two
 * tabs of one connection are two different views.
 */
export const esTabKey = (connId: string, index: string): string => `${connId}:${index}`

/**
 * The indices to draw: the server's list minus the system indices, unless
 * the reader asked for them.
 *
 * System indices are the dot-prefixed ones (`.security-7`, `.kibana_...`);
 * the tree hides them by default so an everyday cluster reads like an
 * everyday one. A name that is dotted without leading to a dot is an
 * ordinary index and stays.
 */
export const visibleIndices = (
  indices: ElasticsearchIndexInfo[],
  includeSystem: boolean,
): ElasticsearchIndexInfo[] =>
  includeSystem ? indices : indices.filter((index) => !index.name.startsWith("."))
