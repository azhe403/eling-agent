import { describe, it, expect } from "vitest"

import {
  docKindLabel,
  selectionFromParams,
  applySelection,
  sortFromParams,
} from "./docs-scope"

describe("docs scope and sort binding", () => {
  it("capitalizes the All sentinel and every kind option consistently", () => {
    expect(docKindLabel("all")).toBe("All")
    expect(docKindLabel("spec")).toBe("Spec")
  })

  it("decodes no workspace params as all, repeated params as an explicit set", () => {
    expect(selectionFromParams(new URLSearchParams())).toEqual({ kind: "all" })
    const selection = selectionFromParams(new URLSearchParams("workspace=/a&workspace=/b"))
    expect(selection.kind).toBe("workspaces")
    if (selection.kind === "workspaces") expect(selection.roots).toEqual(["/a", "/b"])
  })

  it("round-trips a workspace selection through the URL", () => {
    const next = applySelection(new URLSearchParams("kind=spec"), { kind: "workspaces", roots: ["/a"] })
    expect(next.getAll("workspace")).toEqual(["/a"])
    expect(next.get("kind")).toBe("spec")
    expect(selectionFromParams(next)).toEqual({ kind: "workspaces", roots: ["/a"] })
  })

  it("falls back to name/asc for unknown sort values", () => {
    expect(sortFromParams(new URLSearchParams())).toEqual({ sort: "name", order: "asc" })
    expect(sortFromParams(new URLSearchParams("sort=bogus&order=sideways")))
      .toEqual({ sort: "name", order: "asc" })
    expect(sortFromParams(new URLSearchParams("sort=order&order=desc")))
      .toEqual({ sort: "order", order: "desc" })
  })
})
