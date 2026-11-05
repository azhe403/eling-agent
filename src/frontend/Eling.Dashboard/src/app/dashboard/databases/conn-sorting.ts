/**
 * The order the connections panel is drawn in, and where the choice comes from.
 *
 * The panel is a server-arranged list: the backend answers `?sort=&order=`
 * with the rows already arranged, so nothing here touches the rows. This
 * module only keeps the pair the panel asks for, and works out which pair to
 * ask for on the next fetch.
 *
 * The pair comes from three places, in order of authority: the URL (a shared
 * link or the back button is a deliberate choice), localStorage (the choice
 * the reader made last time), and the default, which is Name A to Z. The URL
 * always wins over the remembered pair; a remembered pair always wins over
 * the default.
 *
 * Nothing here touches React: same input, same output.
 */

export const CONN_SORT_KEYS = ["name", "provider", "created", "lastUsed"] as const

export type ConnSortKey = (typeof CONN_SORT_KEYS)[number]

export const DEFAULT_CONN_SORT: ConnSortKey = "name"

export type ConnSortOrder = "asc" | "desc"

export const DEFAULT_CONN_ORDER: ConnSortOrder = "asc"

/**
 * The label a reader sees on each segment, and the reason for it on hover.
 * "Ascending" is the column's natural order - a name reads A to Z, a date
 * reads newest first - and "descending" is the mirror of it.
 */
export const CONN_SORT_COPY: Record<ConnSortKey, { label: string; title: string }> = {
  name: {
    label: "Name",
    title: "The name, A to Z",
  },
  provider: {
    label: "Provider",
    title: "The provider the connection speaks",
  },
  created: {
    label: "Created",
    title: "The moment it was registered, newest first",
  },
  lastUsed: {
    label: "Last used",
    title: "The moment it was last reached, most recent first",
  },
}

/** The key a wire value names; an unknown key is the default key, not an error. */
export const parseConnSort = (value: string | null | undefined): ConnSortKey =>
  (CONN_SORT_KEYS as readonly string[]).includes(value ?? "")
    ? (value as ConnSortKey)
    : DEFAULT_CONN_SORT

/** The direction a wire value names; only "desc" is the mirror, anything else is the natural order. */
export const parseConnOrder = (value: string | null | undefined): ConnSortOrder =>
  value?.toLowerCase() === "desc" ? "desc" : "asc"

/**
 * The pair the next fetch asks for.
 *
 * <para>The URL is the top of the chain: a link that names a key asks for it
 * even when the reader has a different pair remembered. A link that names a
 * key but no direction asks for the key in its natural order, which keeps the
 * pair whole.</para>
 * <para>A URL that carries no key at all - either absent, or typed as the
 * default without a direction - defers to the saved pair, and a saved pair
 * whose key is no longer offered falls back to the default key and keeps the
 * remembered direction, so the reader's choice of how the list runs is never
 * lost to a renamed key.</para>
 */
export const resolveConnSort = (opts: {
  urlSort?: string | null
  urlOrder?: string | null
  savedSort?: string | null
  savedOrder?: string | null
}): { sort: ConnSortKey; order: ConnSortOrder } => {
  // The URL wins only when it names a key we actually offer: a link typed with
  // one of our four keys - even the default one - is a deliberate choice and
  // takes the whole pair with it. A value that names none of them is not a
  // choice, and the remembered pair gets the floor instead.
  if ((CONN_SORT_KEYS as readonly string[]).includes(opts.urlSort ?? "")) {
    return {
      sort: parseConnSort(opts.urlSort),
      order: parseConnOrder(opts.urlOrder),
    }
  }

  const saved = parseConnSort(opts.savedSort)
  return {
    sort: saved,
    order: parseConnOrder(opts.savedOrder),
  }
}

/**
 * The query string the fetch asks for - empty when the pair is the default,
 * so a shared link stays clean.
 */
export const connSortQuery = (sort: ConnSortKey, order: ConnSortOrder): string => {
  if (sort === DEFAULT_CONN_SORT && order === DEFAULT_CONN_ORDER) return ""
  return `?sort=${sort}&order=${order}`
}
