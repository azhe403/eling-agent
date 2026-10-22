import { render, screen, fireEvent, waitFor } from "@testing-library/react"
import { describe, it, expect, vi } from "vitest"

import { DatabaseCombobox } from "./DatabaseCombobox"

describe("DatabaseCombobox", () => {
  it("renders the input and the dropdown trigger", () => {
    render(
      <DatabaseCombobox
        value=""
        onChange={() => {}}
        loadDatabases={async () => []}
        serverKey="mysql|host-a|3306||"
      />,
    )
    expect(screen.getByRole("textbox")).toBeDefined()
  })

  it("re-reads the server when the server key changes", async () => {
    const load = vi.fn(async () => ["db1", "db2"])
    const { rerender } = render(
      <DatabaseCombobox
        value=""
        onChange={() => {}}
        loadDatabases={load}
        serverKey="mysql|host-a|3306||"
      />,
    )

    // Open the dropdown on server A.
    fireEvent.click(screen.getByRole("button", { name: /show the databases/i }))
    await waitFor(() => expect(load).toHaveBeenCalledTimes(1))

    // Close the dropdown before the server changes (a person types in the
    // host field while the list is closed).
    fireEvent.click(screen.getByRole("button", { name: /show the databases/i }))

    // Switch to server B and reopen — the list must be re-read, not reused.
    rerender(
      <DatabaseCombobox
        value=""
        onChange={() => {}}
        loadDatabases={load}
        serverKey="mysql|host-b|3306||"
      />,
    )
    fireEvent.click(screen.getByRole("button", { name: /show the databases/i }))
    await waitFor(() => expect(load).toHaveBeenCalledTimes(2))
  })
})