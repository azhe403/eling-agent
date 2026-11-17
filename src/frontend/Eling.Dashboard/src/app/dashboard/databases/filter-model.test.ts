import { describe, it, expect } from "vitest"
import {
  columnKind,
  opsFor,
  cycleSort,
  toggleSortColumn,
  activeCount,
  serializeBrowse,
  type BrowseFilter,
} from "./filter-model"

const F = (over: Partial<BrowseFilter> = {}): BrowseFilter => ({
  column: "name",
  op: "eq",
  value: "x",
  enabled: true,
  logic: "first",
  ...over,
})

describe("columnKind", () => {
  it("maps text type families to text", () => {
    for (const t of ["varchar(50)", "TEXT", "char(1)", "enum('a','b')", "mediumtext"]) {
      expect(columnKind(t)).toBe("text")
    }
  })

  it("maps numeric type families to numeric", () => {
    for (const t of ["int", "bigint", "smallint", "decimal(10,2)", "float", "double", "numeric(10,2)", "real"]) {
      expect(columnKind(t)).toBe("numeric")
    }
  })

  it("maps date/time families to datetime", () => {
    for (const t of ["date", "datetime", "timestamp"]) {
      expect(columnKind(t)).toBe("datetime")
    }
  })

  it("maps anything unknown to text", () => {
    expect(columnKind("blob")).toBe("text")
    expect(columnKind("")).toBe("text")
  })

  it("maps boolish type families to boolean", () => {
    for (const t of ["tinyint(1)", "TINYINT", "bool", "boolean", "bit"]) {
      expect(columnKind(t)).toBe("boolean")
    }
  })

  it("token-matches the first word, not substrings", () => {
    expect(columnKind("POINT")).toBe("text")
    expect(columnKind("geometry_interval")).toBe("text")
    expect(columnKind("DOUBLE PRECISION")).toBe("numeric")
  })
})

describe("opsFor", () => {
  it("gives text columns string operators and no comparison operators", () => {
    const values = opsFor("text").map((o) => o.value)
    expect(values).toContain("contains")
    expect(values).toContain("notContains")
    expect(values).toContain("startsWith")
    expect(values).toContain("endsWith")
    expect(values).toContain("eq")
    expect(values).toContain("neq")
    expect(values).not.toContain("gt")
    expect(values).not.toContain("gte")
    expect(values.slice(-2)).toEqual(["isNull", "isNotNull"])
  })

  it("labels text operators for the dropdown", () => {
    const ops = Object.fromEntries(opsFor("text").map((o) => [o.value, o.label]))
    expect(ops.eq).toBe("Equals")
    expect(ops.neq).toBe("Not equals")
    expect(ops.contains).toBe("Contains")
    expect(ops.isNull).toBe("Is null")
    expect(ops.isNotNull).toBe("Is not null")
  })

  it("gives boolean columns equality, null and not-equals only", () => {
    const values = opsFor("boolean").map((o) => o.value)
    expect(values).toEqual(["eq", "neq", "isNull", "isNotNull"])
    expect(opsFor("boolean").map((o) => o.label)).toEqual(["Equals", "Not equals", "Is null", "Is not null"])
  })

  it("gives numeric columns comparison operators and no string operators", () => {
    const values = opsFor("numeric").map((o) => o.value)
    expect(values).toContain("eq")
    expect(values).toContain("neq")
    expect(values).toContain("gt")
    expect(values).toContain("gte")
    expect(values).toContain("lt")
    expect(values).toContain("lte")
    expect(values).not.toContain("contains")
    expect(values.slice(-2)).toEqual(["isNull", "isNotNull"])
  })

  it("labels numeric operators with math symbols", () => {
    const ops = Object.fromEntries(opsFor("numeric").map((o) => [o.value, o.label]))
    expect(ops.eq).toBe("=")
    expect(ops.neq).toBe("≠")
    expect(ops.gte).toBe("≥")
    expect(ops.lte).toBe("≤")
  })

  it("gives datetime columns the comparison list with date wording", () => {
    const ops = Object.fromEntries(opsFor("datetime").map((o) => [o.value, o.label]))
    expect(Object.keys(ops)).toEqual(expect.arrayContaining(["eq", "neq", "gt", "gte", "lt", "lte", "isNull", "isNotNull"]))
    expect(ops.eq).toBe("On")
    expect(ops.gte).toBe("On or after")
    expect(ops.lt).toBe("Before")
    expect(ops.gt).toBe("After")
  })
})

describe("cycleSort", () => {
  it("cycles asc -> desc -> none -> asc", () => {
    expect(cycleSort(null)).toBe("asc")
    expect(cycleSort("asc")).toBe("desc")
    expect(cycleSort("desc")).toBeNull()
  })
})

describe("toggleSortColumn", () => {
  it("assigns asc to an unsorted column, replacing the whole list", () => {
    expect(toggleSortColumn([{ column: "other", order: "desc" }], "id")).toEqual([{ column: "id", order: "asc" }])
  })

  it("cycles asc -> desc -> removed", () => {
    const asc = toggleSortColumn([], "id")
    expect(asc).toEqual([{ column: "id", order: "asc" }])
    const desc = toggleSortColumn(asc, "id")
    expect(desc).toEqual([{ column: "id", order: "desc" }])
    const none = toggleSortColumn(desc, "id")
    expect(none).toEqual([])
  })
})

describe("activeCount", () => {
  it("counts enabled conditions only", () => {
    const filters = [F(), F(), F({ enabled: false })]
    expect(activeCount(filters, "")).toBe(2)
  })

  it("adds one for a non-blank manual condition", () => {
    expect(activeCount([F()], "is_active = 1")).toBe(2)
  })

  it("ignores a whitespace-only manual condition", () => {
    expect(activeCount([F()], "   ")).toBe(1)
    expect(activeCount([], "   ")).toBe(0)
  })
})



describe("serializeBrowse", () => {
  it("emits the wire body with the contract keys in order", () => {
    const filters = [F()]
    const sort = [{ column: "updated_at", order: "desc" as const }]
    const body = serializeBrowse("mst_rate", "dev_reference", 2, 250, filters, sort, "is_active = 1")
    expect(body).toEqual({
      table: "mst_rate",
      database: "dev_reference",
      page: 2,
      limit: 250,
      filters,
      sort,
      manualCondition: "is_active = 1",
    })
    expect(Object.keys(body)).toEqual(["table", "database", "page", "limit", "filters", "sort", "manualCondition"])
  })

  it("passes null database through for a connection's own database", () => {
    expect(serializeBrowse("t", null, 1, 100, [], [], null).database).toBeNull()
  })
})
