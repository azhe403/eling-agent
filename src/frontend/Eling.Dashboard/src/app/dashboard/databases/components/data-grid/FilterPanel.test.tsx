import { render, screen, fireEvent } from "@testing-library/react"
import { describe, it, expect, vi } from "vitest"

import { FilterPanel } from "./FilterPanel"
import type { BrowseSort } from "../../filter-model"

const base = {
  columns: ["id", "name", "updated_at"],
  columnTypes: {},
  initial: { filters: [], sort: [] as BrowseSort[], manualCondition: "" },
}

describe("FilterPanel committed sort", () => {
  it("Apply preserves a header-committed desc sort (1237524)", () => {
    const onApply = vi.fn()
    const committed: BrowseSort[] = [{ column: "updated_at", order: "desc" }]
    render(
      <FilterPanel
        {...base}
        initial={{ ...base.initial, sort: committed }}
        committedSort={committed}
        onApply={onApply}
        onClose={() => {}}
      />,
    )

    fireEvent.click(screen.getByRole("button", { name: "Apply" }))

    expect(onApply).toHaveBeenCalledTimes(1)
    expect(onApply).toHaveBeenCalledWith({
      filters: [],
      sort: [{ column: "updated_at", order: "desc" }],
      manualCondition: "",
    })
  })

  it("follows out-of-band committed changes until the reader edits the rows", () => {
    const onApply = vi.fn()
    const initialCommitted: BrowseSort[] = [{ column: "updated_at", order: "desc" }]
    const { rerender } = render(
      <FilterPanel
        {...base}
        initial={{ ...base.initial, sort: initialCommitted }}
        committedSort={initialCommitted}
        onApply={onApply}
        onClose={() => {}}
      />,
    )

    // The header clears the sort after the panel opened (out-of-band).
    rerender(
      <FilterPanel
        {...base}
        initial={{ ...base.initial, sort: initialCommitted }}
        committedSort={[]}
        onApply={onApply}
        onClose={() => {}}
      />,
    )

    fireEvent.click(screen.getByRole("button", { name: "Apply" }))
    expect(onApply).toHaveBeenCalledWith({ filters: [], sort: [], manualCondition: "" })
  })

  it("keeps the reader's own edit when committed changes afterwards", () => {
    const onApply = vi.fn()
    const { container, rerender } = render(
      <FilterPanel
        {...base}
        initial={{ ...base.initial, sort: [] }}
        committedSort={[]}
        onApply={onApply}
        onClose={() => {}}
      />,
    )

    // The reader adds a sort row themselves (first free column: "id").
    fireEvent.click(screen.getByRole("button", { name: "Add sort" }))
    expect(container.querySelectorAll("[aria-label=\"Sort column\"]")).toHaveLength(1)

    // Out-of-band the header now sorts a different column.
    rerender(
      <FilterPanel
        {...base}
        initial={{ ...base.initial, sort: [] }}
        committedSort={[{ column: "name", order: "desc" }]}
        onApply={onApply}
        onClose={() => {}}
      />,
    )

    fireEvent.click(screen.getByRole("button", { name: "Apply" }))
    const applied = onApply.mock.calls[0][0] as { sort: BrowseSort[] }
    // The reader's own row wins: their sort, untouched by the header.
    expect(applied.sort).toEqual([{ column: "id", order: "asc" }])
  })
})
