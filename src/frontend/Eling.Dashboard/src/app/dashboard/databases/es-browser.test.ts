import { describe, it, expect } from "vitest"

import { esIndexLabel, esIndexDocs, esTabKey, visibleIndices } from "./es-browser"
import type { ElasticsearchIndexInfo } from "./types"

const index = (name: string): ElasticsearchIndexInfo => ({
  name,
  health: "green",
  status: "open",
  docsCount: 0,
  storeSizeBytes: 0,
  primaryShards: 1,
  replicas: 0,
})

describe("esIndexLabel", () => {
  it("reads the index name", () => {
    expect(esIndexLabel("orders")).toBe("orders")
  })
})

describe("esIndexDocs", () => {
  it("spells the count out", () => {
    expect(esIndexDocs(3)).toBe("3 docs")
    expect(esIndexDocs(0)).toBe("0 docs")
  })
})

describe("visibleIndices", () => {
  const system = index(".security-7")
  const orders = index("orders")
  const myDotted = index("my.index")

  it("hides the system indices by default", () => {
    expect(visibleIndices([system, orders], false).map((i) => i.name)).toEqual(["orders"])
  })

  it("keeps the system indices when asked", () => {
    expect(visibleIndices([system, orders], true).map((i) => i.name)).toEqual([".security-7", "orders"])
  })

  it("keeps an ordinary dotted name - only the leading dot hides", () => {
    expect(visibleIndices([myDotted], false)).toEqual([myDotted])
  })

  it("shows an empty list as empty", () => {
    expect(visibleIndices([], false)).toEqual([])
  })
})

describe("esTabKey", () => {
  it("differs for two indices of one connection", () => {
    expect(esTabKey("c1", "orders")).not.toBe(esTabKey("c1", "items"))
  })

  it("differs across connections", () => {
    expect(esTabKey("c1", "orders")).not.toBe(esTabKey("c2", "orders"))
  })
})
