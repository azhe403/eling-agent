import { describe, expect, it } from "vitest"

import {
  CONN_SORT_KEYS,
  DEFAULT_CONN_ORDER,
  DEFAULT_CONN_SORT,
  connSortQuery,
  parseConnOrder,
  parseConnSort,
  resolveConnSort,
} from "./conn-sorting"

/**
 * The order the connections panel is drawn in, and where the choice comes
 * from. Each chain link is checked in both directions, and against the two
 * rules that are easy to get wrong: a link the reader has typed in the URL
 * always wins over a remembered pair, and a remembered pair whose key is no
 * longer offered keeps the remembered direction.
 */

describe("parseConnSort", () => {
  it("falls back to the default for a missing or unknown value", () => {
    expect(parseConnSort(null)).toBe(DEFAULT_CONN_SORT)
    expect(parseConnSort("")).toBe(DEFAULT_CONN_SORT)
    expect(parseConnSort("bogus")).toBe(DEFAULT_CONN_SORT)
  })

  it("accepts every key it offers", () => {
    for (const key of CONN_SORT_KEYS) {
      expect(parseConnSort(key)).toBe(key)
    }
  })
})

describe("parseConnOrder", () => {
  it("falls back to the natural order for a missing or unknown value", () => {
    expect(parseConnOrder(null)).toBe(DEFAULT_CONN_ORDER)
    expect(parseConnOrder("")).toBe(DEFAULT_CONN_ORDER)
    expect(parseConnOrder("sideways")).toBe(DEFAULT_CONN_ORDER)
  })

  it("accepts the mirror order", () => {
    expect(parseConnOrder("desc")).toBe("desc")
    expect(parseConnOrder("DESC")).toBe("desc")
  })
})

describe("resolveConnSort", () => {
  it("lets the URL win over the saved pair", () => {
    expect(resolveConnSort({ urlSort: "created", savedSort: "lastUsed" })).toEqual({
      sort: "created",
      order: "asc",
    })
  })

  it("lets the URL win, even when it names the default key by hand", () => {
    expect(resolveConnSort({ urlSort: "name", urlOrder: "desc", savedSort: "provider" })).toEqual({
      sort: "name",
      order: "desc",
    })
  })

  it("reads the order from the URL when the URL names a key", () => {
    expect(resolveConnSort({ urlSort: "lastUsed", urlOrder: "desc" })).toEqual({
      sort: "lastUsed",
      order: "desc",
    })
  })

  it("uses the natural order when the URL names a key but no direction", () => {
    expect(resolveConnSort({ urlSort: "created" })).toEqual({
      sort: "created",
      order: "asc",
    })
  })

  it("falls back to the saved pair when the URL names nothing", () => {
    expect(resolveConnSort({ savedSort: "provider", savedOrder: "desc" })).toEqual({
      sort: "provider",
      order: "desc",
    })
  })

  it("reads a saved direction on a remembered key", () => {
    expect(resolveConnSort({ savedSort: "provider", savedOrder: "desc" })).toEqual({
      sort: "provider",
      order: "desc",
    })
  })

  it("falls back to the default key when the saved key is no longer offered, and keeps the saved direction", () => {
    expect(resolveConnSort({ savedSort: "bogus", savedOrder: "desc" })).toEqual({
      sort: DEFAULT_CONN_SORT,
      order: "desc",
    })
  })

  it("falls back to the default pair when nothing is remembered", () => {
    expect(resolveConnSort({})).toEqual({
      sort: DEFAULT_CONN_SORT,
      order: DEFAULT_CONN_ORDER,
    })
  })

  it("lets the URL win even when the URL's key is unknown and the saved key is not", () => {
    expect(resolveConnSort({ urlSort: "bogus", urlOrder: "desc", savedSort: "created" })).toEqual({
      sort: "created",
      order: "asc",
    })
  })
})

describe("connSortQuery", () => {
  it("is empty for the default pair", () => {
    expect(connSortQuery(DEFAULT_CONN_SORT, DEFAULT_CONN_ORDER)).toBe("")
  })

  it("names the key when the pair differs from the default", () => {
    expect(connSortQuery("created", "asc")).toBe("?sort=created&order=asc")
  })

  it("names the default key when only the direction differs", () => {
    expect(connSortQuery(DEFAULT_CONN_SORT, "desc")).toBe("?sort=name&order=desc")
  })
})
