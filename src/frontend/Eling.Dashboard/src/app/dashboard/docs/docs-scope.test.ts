import { describe, it, expect } from "vitest"

import { docKindLabel, docOwnershipLabel, docOriginLabel } from "./docs-scope"

describe("docs filter labels", () => {
  it("capitalizes the All sentinel and every option consistently", () => {
    expect(docKindLabel("all")).toBe("All")
    expect(docKindLabel("spec")).toBe("Spec")
    expect(docOwnershipLabel("all")).toBe("All")
    expect(docOwnershipLabel("managed")).toBe("Managed")
    expect(docOriginLabel("all")).toBe("All")
    expect(docOriginLabel("current")).toBe("Current")
  })
})
