import { describe, it, expect } from "vitest"
import { render, screen, fireEvent } from "@testing-library/react"
import { LeasePaths } from "./lease-paths"

const three = ["src/a.ts", "src/b.ts", "src/c.ts"]
const five = [...three, "src/d.ts", "src/e.ts"]

describe("LeasePaths", () => {
  it("renders all entries when there are exactly 3, with no toggle", () => {
    render(<LeasePaths paths={three} />)
    expect(screen.getByText("src/a.ts")).toBeTruthy()
    expect(screen.getByText("src/b.ts")).toBeTruthy()
    expect(screen.getByText("src/c.ts")).toBeTruthy()
    expect(screen.queryByRole("button")).toBeNull()
    expect(screen.getAllByRole("listitem")).toHaveLength(3)
  })

  it("shows 3 entries and a +2 more toggle when there are 5 paths", () => {
    render(<LeasePaths paths={five} />)
    expect(screen.getAllByRole("listitem")).toHaveLength(3)
    expect(screen.getByRole("button", { name: /\+2 more/i })).toBeTruthy()
  })

  it("reveals the remaining paths on toggle and hides them again on second click", () => {
    render(<LeasePaths paths={five} />)
    fireEvent.click(screen.getByRole("button", { name: /\+2 more/i }))
    expect(screen.getAllByRole("listitem")).toHaveLength(5)
    expect(screen.getByText("src/d.ts")).toBeTruthy()
    expect(screen.getByText("src/e.ts")).toBeTruthy()
    fireEvent.click(screen.getByRole("button", { name: /show less/i }))
    expect(screen.getAllByRole("listitem")).toHaveLength(3)
  })

  it("renders a dash for an empty list", () => {
    render(<LeasePaths paths={[]} />)
    expect(screen.getByText("—")).toBeTruthy()
  })
})
