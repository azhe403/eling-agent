"use client"

import { useSyncExternalStore } from "react"
import { formatDistanceToNowStrict } from "date-fns"

// One timer serves every label, so a long list does not create one interval
// per card.
const TICK_MS = 30_000
const listeners = new Set<() => void>()
let revision = 0
let timer: ReturnType<typeof setInterval> | null = null

function subscribe(listener: () => void) {
  listeners.add(listener)
  if (!timer) {
    timer = setInterval(() => {
      revision += 1
      for (const current of listeners) current()
    }, TICK_MS)
  }
  return () => {
    listeners.delete(listener)
    if (listeners.size === 0 && timer) {
      clearInterval(timer)
      timer = null
    }
  }
}

function getSnapshot() { return revision }

function subscribeNoop() { return () => {} }
const clientMounted = () => true
const serverMounted = () => false

export function RelativeTime({ value }: { value: string }) {
  useSyncExternalStore(subscribe, getSnapshot, getSnapshot)
  // The server snapshot is false and the client snapshot is true, so the label
  // appears only after hydration without an effect that sets state.
  const mounted = useSyncExternalStore(subscribeNoop, clientMounted, serverMounted)

  // A relative string calculated during SSR would drift by hydration time and
  // break the markup.
  const date = new Date(value)
  if (!mounted || Number.isNaN(date.getTime())) return null
  return <>{formatDistanceToNowStrict(date, { addSuffix: true })}</>
}
