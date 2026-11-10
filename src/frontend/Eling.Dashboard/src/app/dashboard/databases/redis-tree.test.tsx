import { render, screen, fireEvent } from "@testing-library/react"
import { describe, it, expect, vi } from "vitest"

import { RedisKeyRow, RedisNodeRow, RedisPrefixTree } from "./redis-tree"
import { redisPageKey, type RedisPage } from "./redis-browser"

describe("RedisKeyRow delete", () => {
  it("calls onDelete with the key and never selects", () => {
    const onSelect = vi.fn()
    const onDelete = vi.fn()
    render(
      <RedisKeyRow
        item={{ key: "my:key", type: "string", ttlSeconds: 60 }}
        active={false}
        onSelect={onSelect}
        onDelete={onDelete}
        now={0}
      />,
    )
    fireEvent.click(screen.getByRole("button", { name: "Delete key my:key" }))
    expect(onDelete).toHaveBeenCalledTimes(1)
    expect(onDelete).toHaveBeenCalledWith("my:key")
    expect(onSelect).not.toHaveBeenCalled()
  })

  it("still selects on the row button", () => {
    const onSelect = vi.fn()
    render(
      <RedisKeyRow
        item={{ key: "my:key", type: "string", ttlSeconds: 60 }}
        active={false}
        onSelect={onSelect}
        onDelete={() => {}}
        now={0}
      />,
    )
    const selectButton = screen
      .getAllByRole("button")
      .find((b) => !b.hasAttribute("aria-label"))
    if (!selectButton) throw new Error("select button not found")
    fireEvent.click(selectButton)
    expect(onSelect).toHaveBeenCalledTimes(1)
    expect(onSelect).toHaveBeenCalledWith("my:key")
  })
})

describe("RedisKeyRow gone", () => {
  it("marks a key the recheck found gone, instead of dropping the row", () => {
    render(
      <RedisKeyRow
        item={{ key: "my:key", type: "string", ttlSeconds: -1, gone: true }}
        active={false}
        onSelect={() => {}}
        onDelete={() => {}}
        now={0}
      />,
    )
    expect(screen.getByText(/gone/)).toBeTruthy()
    expect(screen.getByText("my:key")).toBeTruthy()
  })

  it("shows the live countdown when the key still has a ttl", () => {
    const now = Date.parse("2026-11-10T12:00:00Z")
    render(
      <RedisKeyRow
        item={{ key: "my:key", type: "string", ttlSeconds: 90, expiresAt: new Date(now + 90_000).toISOString() }}
        active={false}
        onSelect={() => {}}
        onDelete={() => {}}
        now={now}
      />,
    )
    expect(screen.getByText(/0h 1m 30s/)).toBeTruthy()
  })
})

describe("RedisNodeRow delete", () => {  it("calls onDelete with the prefix and never toggles", () => {
    const onToggle = vi.fn()
    const onDelete = vi.fn()
    render(
      <RedisNodeRow
        item={{ segment: "users", prefix: "users:" }}
        expanded={false}
        onToggle={onToggle}
        onDelete={onDelete}
      />,
    )
    fireEvent.click(screen.getByRole("button", { name: "Delete prefix users:" }))
    expect(onDelete).toHaveBeenCalledTimes(1)
    expect(onDelete).toHaveBeenCalledWith("users:")
    expect(onToggle).not.toHaveBeenCalled()
  })

  it("renders no delete button for the root (empty prefix)", () => {
    render(
      <RedisNodeRow
        item={{ segment: "root", prefix: "" }}
        expanded={false}
        onToggle={() => {}}
        onDelete={() => {}}
      />,
    )
    expect(screen.queryByRole("button", { name: /delete prefix/i })).toBeNull()
  })
})

describe("RedisPrefixTree threading", () => {
  const page: RedisPage = {
    prefix: "",
    nodes: [{ segment: "users", prefix: "users:" }],
    keys: [{ key: "my:key", type: "string", ttlSeconds: 60 }],
    cursor: 0,
    pageOffset: 0,
    complete: true,
    scanned: 1,
  }

  it("threads onDeleteKey and onDeletePrefix to the rows", () => {
    const onDeleteKey = vi.fn()
    const onDeletePrefix = vi.fn()
    render(
      <RedisPrefixTree
        connId="conn"
        db={0}
        prefix=""
        pages={{ [redisPageKey("conn", 0, "")]: page }}
        expanded={new Set()}
        inspectKey=""
        onToggle={() => {}}
        onSelectKey={() => {}}
        onDeleteKey={onDeleteKey}
        onDeletePrefix={onDeletePrefix}
        now={0}
      />,
    )
    fireEvent.click(screen.getByRole("button", { name: "Delete key my:key" }))
    fireEvent.click(screen.getByRole("button", { name: "Delete prefix users:" }))
    expect(onDeleteKey).toHaveBeenCalledWith("my:key")
    expect(onDeletePrefix).toHaveBeenCalledWith("users:")
  })
})
