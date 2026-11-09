import { render, screen, fireEvent } from "@testing-library/react"
import { describe, it, expect, vi } from "vitest"

import { RedisDeleteConfirmDialog } from "./RedisDeleteConfirmDialog"
import { RedisPrefixDeleteConfirmDialog } from "./RedisPrefixDeleteConfirmDialog"

describe("RedisDeleteConfirmDialog", () => {
  it("renders the key name and calls onConfirm without closing automatically", () => {
    const onConfirm = vi.fn()
    const onCancel = vi.fn()
    render(
      <RedisDeleteConfirmDialog
        open={true}
        keyName="user:1:name"
        mutating={false}
        onConfirm={onConfirm}
        onCancel={onCancel}
      />,
    )

    expect(screen.getByText("Delete key?")).toBeTruthy()
    const keySpan = screen.getByText("user:1:name")
    expect(keySpan).toBeTruthy()
    expect(keySpan.className).toContain("font-mono")
    expect(screen.getByText(/This cannot be undone\./)).toBeTruthy()

    fireEvent.click(screen.getByRole("button", { name: "Delete" }))
    expect(onConfirm).toHaveBeenCalledTimes(1)
    expect(onCancel).not.toHaveBeenCalled()
    expect(screen.getByText("Delete key?")).toBeTruthy()
  })

  it("shows Deleting... and disables both buttons when mutating", () => {
    render(
      <RedisDeleteConfirmDialog
        open={true}
        keyName="user:1:name"
        mutating={true}
        onConfirm={() => {}}
        onCancel={() => {}}
      />,
    )

    const del = screen.getByRole("button", { name: "Deleting..." }) as HTMLButtonElement
    expect(del.disabled).toBe(true)
    const cancel = screen.getByRole("button", { name: "Cancel" }) as HTMLButtonElement
    expect(cancel.disabled).toBe(true)
  })

  it("calls onCancel when Cancel is clicked", () => {
    const onCancel = vi.fn()
    render(
      <RedisDeleteConfirmDialog
        open={true}
        keyName="k"
        mutating={false}
        onConfirm={() => {}}
        onCancel={onCancel}
      />,
    )

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }))
    expect(onCancel).toHaveBeenCalledTimes(1)
  })
})

describe("RedisPrefixDeleteConfirmDialog", () => {
  it("renders total, prefix, and the capped key list", () => {
    const onConfirm = vi.fn()
    const onCancel = vi.fn()
    render(
      <RedisPrefixDeleteConfirmDialog
        open={true}
        prefix="user:"
        total={3}
        keys={["user:1", "user:2", "user:3"]}
        truncated={false}
        loading={false}
        mutating={false}
        onConfirm={onConfirm}
        onCancel={onCancel}
      />,
    )

    expect(screen.getByText("Delete prefix?")).toBeTruthy()
    expect(screen.getByText(/3 keys under/)).toBeTruthy()
    const prefixSpan = screen.getByText("user:")
    expect(prefixSpan).toBeTruthy()
    expect(prefixSpan.className).toContain("font-mono")
    for (const k of ["user:1", "user:2", "user:3"]) {
      expect(screen.getByText(k)).toBeTruthy()
    }
    expect(screen.queryByText(/more keys will also be deleted/)).toBeNull()

    fireEvent.click(screen.getByRole("button", { name: "Delete" }))
    expect(onConfirm).toHaveBeenCalledTimes(1)
    expect(onCancel).not.toHaveBeenCalled()
  })

  it("shows the truncated note when truncated is true", () => {
    render(
      <RedisPrefixDeleteConfirmDialog
        open={true}
        prefix="user:"
        total={150}
        keys={["user:1"]}
        truncated={true}
        loading={false}
        mutating={false}
        onConfirm={() => {}}
        onCancel={() => {}}
      />,
    )

    expect(
      screen.getByText("Showing 100 of 150 — more keys will also be deleted."),
    ).toBeTruthy()
  })

  it("shows the loading text when loading is true", () => {
    render(
      <RedisPrefixDeleteConfirmDialog
        open={true}
        prefix="user:"
        total={0}
        keys={[]}
        truncated={false}
        loading={true}
        mutating={false}
        onConfirm={() => {}}
        onCancel={() => {}}
      />,
    )

    expect(screen.getByText("Loading affected keys…")).toBeTruthy()
  })

  it("shows Deleting... when mutating", () => {
    render(
      <RedisPrefixDeleteConfirmDialog
        open={true}
        prefix="user:"
        total={1}
        keys={["user:1"]}
        truncated={false}
        loading={false}
        mutating={true}
        onConfirm={() => {}}
        onCancel={() => {}}
      />,
    )

    const del = screen.getByRole("button", { name: "Deleting..." }) as HTMLButtonElement
    expect(del.disabled).toBe(true)
    const cancel = screen.getByRole("button", { name: "Cancel" }) as HTMLButtonElement
    expect(cancel.disabled).toBe(true)
  })
})
