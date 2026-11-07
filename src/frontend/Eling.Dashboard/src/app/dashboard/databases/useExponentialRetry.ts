"use client"

import { useCallback, useEffect, useRef, useState } from "react"

/** What a scheduled read is waiting on. */
export type RetrySource = "offline" | "refreshing"

type RetryEntry = {
  attempt: number
  deadline: number
  max: number
  source: RetrySource
}

export type ExponentialRetryOptions = {
  /** The first delay, doubled on every offline attempt. */
  baseMs?: number
  /** The ceiling the delay stops doubling at. */
  maxMs?: number
  /** How many offline attempts before the count starts over at one. */
  maxAttempts?: number
  /** The delay for a snapshot the backend is already replacing. */
  refreshMs?: number
}

export type ExponentialRetry = {
  /**
   * Ask for another read of `key` after a delay, running `reload` when it
   * comes due. Calling it again for a key whose countdown is still running
   * does nothing: the running countdown keeps its own deadline, so the number
   * on screen never jumps backwards.
   */
  schedule: (key: string, source: RetrySource, reload: () => void) => void
  /** Forget `key`: cancel its countdown and clear its progress. */
  cancel: (key: string) => void
  /** Forget every key. */
  cancelAll: () => void
  /** The text to show for `key`, or null when nothing is scheduled. */
  label: (key: string) => string | null
  /** The attempt currently scheduled for `key`, 0 when none. */
  attemptOf: (key: string) => number
}

/**
 * A retrying read with a visible countdown.
 *
 * <para>
 * Two situations share one ladder. When the database is <c>offline</c> it is
 * unreachable, so the delay doubles from the base to a ceiling and the attempt
 * counter cycles from one to the maximum: a database that stays dead for days
 * must not go silent, and the delay already sits at its ceiling, so restarting
 * the count costs nothing. When the backend says <c>refreshing</c> the database
 * answered - it was simply a snapshot older than the freshness window - so
 * there is nothing to back off from and the read comes back quickly.
 * </para>
 *
 * <para>
 * All of the bookkeeping lives in refs, because the callers are
 * <c>useCallback</c> closures that would otherwise capture the first render's
 * state and read it forever. The rendered <see cref="label"/> reads state, so
 * the countdown redraws once a second.
 * </para>
 */
export function useExponentialRetry(options: ExponentialRetryOptions = {}): ExponentialRetry {
  const baseMs = options.baseMs ?? 2000
  const maxMs = options.maxMs ?? 60000
  const maxAttempts = options.maxAttempts ?? 10
  const refreshMs = options.refreshMs ?? 3000

  // The source of truth. The state below is a mirror, only so renders see it.
  const progressRef = useRef<Record<string, RetryEntry>>({})
  const attemptRef = useRef<Record<string, number>>({})
  const reloadRef = useRef<Record<string, () => void>>({})
  const timerRef = useRef<Record<string, ReturnType<typeof setTimeout>>>({})

  const [progress, setProgress] = useState<Record<string, RetryEntry>>({})

  // A heartbeat, so the countdown moves. Without it the text would only change
  // when a new attempt was scheduled, and a person waiting on a dead database
  // would see a frozen number. The clock lives in state because reading it
  // during render is impure: two renders of one state must agree.
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(timer)
  }, [])

  const clearTimer = (key: string) => {
    const timer = timerRef.current[key]
    if (timer === undefined) return
    clearTimeout(timer)
    delete timerRef.current[key]
  }

  // A countdown must not outlive the component that started it.
  useEffect(() => () => {
    for (const timer of Object.values(timerRef.current)) clearTimeout(timer)
    timerRef.current = {}
  }, [])

  const cancel = useCallback((key: string) => {
    clearTimer(key)
    delete attemptRef.current[key]
    delete reloadRef.current[key]
    if (!(key in progressRef.current)) return
    const next = { ...progressRef.current }
    delete next[key]
    progressRef.current = next
    setProgress(next)
  }, [])

  const cancelAll = useCallback(() => {
    for (const key of Object.keys(timerRef.current)) clearTimer(key)
    attemptRef.current = {}
    reloadRef.current = {}
    progressRef.current = {}
    setProgress({})
  }, [])

  const schedule = useCallback((key: string, source: RetrySource, reload: () => void) => {
    // Keep the newest reload, so a timer that fires later calls the current
    // fetch instead of the one captured whenever the countdown was started.
    reloadRef.current[key] = reload

    // A countdown already running keeps its deadline. Re-reading in between -
    // a person expanding another connection, a manual refresh - must not
    // restart the number while they watch it.
    const running = progressRef.current[key]
    if (running && running.deadline > Date.now()) return

    const offline = source === "offline"
    const attempt = offline ? (attemptRef.current[key] ?? 0) % maxAttempts : 0
    attemptRef.current[key] = attempt + 1

    const delay = offline ? Math.min(baseMs * 2 ** attempt, maxMs) : refreshMs
    const next = {
      ...progressRef.current,
      [key]: { attempt: attempt + 1, deadline: Date.now() + delay, max: maxAttempts, source }
    }
    progressRef.current = next
    setProgress(next)

    clearTimer(key)
    timerRef.current[key] = setTimeout(() => {
      delete timerRef.current[key]
      reloadRef.current[key]?.()
    }, delay)
  }, [baseMs, maxMs, maxAttempts, refreshMs])

  const label = (key: string): string | null => {
    const entry = progress[key]
    if (!entry) return null
    // A snapshot the backend is already replacing is not a failure, so it says
    // what it is instead of counting down a retry.
    if (entry.source !== "offline") return "refreshing snapshot…"
    const left = Math.max(0, Math.ceil((entry.deadline - now) / 1000))
    return `retrying ${entry.attempt}/${entry.max} · next in ${left}s`
  }

  const attemptOf = useCallback((key: string): number => attemptRef.current[key] ?? 0, [])

  // Callers destructure this and put the stable members in their own dependency
  // lists, so `schedule`, `cancel`, `cancelAll` and `attemptOf` never change
  // identity. `label` changes on every tick, which is what redraws the
  // countdown.
  return { schedule, cancel, cancelAll, label, attemptOf }
}
