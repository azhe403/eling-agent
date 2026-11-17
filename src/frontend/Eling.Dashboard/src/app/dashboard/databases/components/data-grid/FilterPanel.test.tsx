import { render, screen, fireEvent } from "@testing-library/react"
import { describe, it, expect, vi } from "vitest"

import { FilterPanel } from "./FilterPanel"
import type { BrowseSort } from "../../filter-model"

const base = {
  columns: ["id", "name", "updated_at"],
  columnTypes: {},
  initial: { filters: [], sort: [] as BrowseSort[], manualCondition: "" },
}

describe("FilterPanel keyboard", () => {
  it("Enter in the manual condition field applies", () => {
    const onApply = vi.fn()
    render(
      <FilterPanel
        {...base}
        committedSort={[]}
        onApply={onApply}
        onClose={() => {}}
      />,
    )
    const manual = screen.getByRole("textbox", { name: /manual query condition/i })
    fireEvent.change(manual, { target: { value: "is_active = 1" } })
    fireEvent.keyDown(manual, { key: "Enter" })
    expect(onApply).toHaveBeenCalledTimes(1)
    expect(onApply).toHaveBeenCalledWith({ filters: [], sort: [], manualCondition: "is_active = 1" })
  })

  it("Enter in the value field commits the draft filter", () => {
    const onApply = vi.fn()
    render(
      <FilterPanel
        {...base}
        committedSort={[]}
        onApply={onApply}
        onClose={() => {}}
      />,
    )
    fireEvent.click(screen.getByRole("button", { name: "Add condition" }))
    const value = screen.getByRole("textbox", { name: "Value" })
    fireEvent.change(value, { target: { value: "Batangan" } })
    fireEvent.keyDown(value, { key: "Enter" })
    expect(onApply).toHaveBeenCalledTimes(1)
    expect(onApply).toHaveBeenCalledWith({
      filters: [{ column: "name", op: "eq", value: "Batangan", enabled: true, logic: "first" }],
      sort: [],
      manualCondition: "",
    })
  })

  it("Enter on the operator dropdown trigger does not apply", () => {
    const onApply = vi.fn()
    render(
      <FilterPanel
        {...base}
        committedSort={[]}
        onApply={onApply}
        onClose={() => {}}
      />,
    )
    fireEvent.click(screen.getByRole("button", { name: "Add condition" }))
    fireEvent.keyDown(screen.getByRole("combobox", { name: "Operator" }), { key: "Enter" })
    expect(onApply).not.toHaveBeenCalled()
  })

  it("Enter on a panel button does not apply", () => {
    const onApply = vi.fn()
    render(
      <FilterPanel
        {...base}
        committedSort={[]}
        onApply={onApply}
        onClose={() => {}}
      />,
    )
    fireEvent.keyDown(screen.getByRole("button", { name: "Add condition" }), { key: "Enter" })
    expect(onApply).not.toHaveBeenCalled()
  })
})

describe("FilterPanel apply lock", () => {
  const initialFilters = [{ column: "name", op: "eq" as const, value: "Batangan", enabled: true, logic: "first" as const }]

  it("Apply button is disabled while a refetch is pending and fires nothing", () => {
    const onApply = vi.fn()
    render(
      <FilterPanel
        {...base}
        initial={{ ...base.initial, filters: initialFilters }}
        committedSort={[]}
        applyPending
        onApply={onApply}
        onClose={() => {}}
      />,
    )
    const applyButton = screen.getByRole("button", { name: "Applying…" })
    expect((applyButton as HTMLButtonElement).disabled).toBe(true)
    fireEvent.click(applyButton)
    expect(onApply).not.toHaveBeenCalled()
  })

  it("Enter in a text field does not fire while pending, Ctrl+Enter neither", () => {
    const onApply = vi.fn()
    render(
      <FilterPanel
        {...base}
        initial={{ ...base.initial, filters: initialFilters }}
        committedSort={[]}
        applyPending
        onApply={onApply}
        onClose={() => {}}
      />,
    )
    const manual = screen.getByRole("textbox", { name: /manual query condition/i })
    fireEvent.keyDown(manual, { key: "Enter" })
    fireEvent.keyDown(manual, { key: "Enter", ctrlKey: true })
    expect(onApply).not.toHaveBeenCalled()
  })

  it("Clear all does not fire while pending", () => {
    const onApply = vi.fn()
    render(
      <FilterPanel
        {...base}
        initial={{ ...base.initial, filters: initialFilters }}
        committedSort={[]}
        applyPending
        onApply={onApply}
        onClose={() => {}}
      />,
    )
    const clearAll = screen.getByRole("button", { name: "Clear all" })
    expect((clearAll as HTMLButtonElement).disabled).toBe(true)
    fireEvent.click(clearAll)
    expect(onApply).not.toHaveBeenCalled()
  })
})

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
