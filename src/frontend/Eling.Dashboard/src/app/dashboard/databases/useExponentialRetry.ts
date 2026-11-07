"use client"

import { useCallback, useEffect, useRef, useState } from "react"

/** Why a key is being read again. */
export type RetryKind = "revalidate" | "offline"

type Pending = {
  attempt: number
  deadline: number
  max: number
  kind: RetryKind
}

export type ExponentialRetryOptions = {
  /** The first delay, doubled on every offline attempt. */
  baseMs?: number
  /** The ceiling the delay stops doubling at. */
  maxMs?: number
  /** How many offline attempts before the count starts over at one. */
  maxAttempts?: number
  /** How long to wait before asking again for a cached answer. */
  revalidateMs?: number
}

export type ExponentialRetry = {
  /**
   * Ask for another read of `key` when it comes due. Calling it again for a
   * key whose timer is still running does nothing: the running timer keeps its
   * own deadline, so nothing on screen jumps backwards.
   */
  schedule: (key: string, kind: RetryKind, reload: () => void) => void
  /** Forget `key`: cancel its timer and clear its progress. */
  cancel: (key: string) => void
  /** The text to show for `key`, or null when there is nothing to show. */
  label: (key: string) => string | null
  /** The offline attempt currently scheduled for `key`, 0 when none. */
  attemptOf: (key: string) => number
}

/**
 * A read that asks again by itself, with a visible countdown when it is
 * retrying a database that is down.
 *
 * <para>
 * There are two reasons to ask again, and they behave differently. A
 * <c>revalidate</c> follows a cached answer: the tables are already on screen,
 * so the ask is quiet and comes back in a moment. <c>offline</c> means the
 * database could not be reached, so the delay doubles from the base to a
 * ceiling and the attempt count cycles from one to the maximum — a database
 * that stays dead for days must not go silent after a few minutes.
 * </para>
 *
 * <para>
 * The bookkeeping lives in refs, because the callers are <c>useCallback</c>
 * closures that would otherwise capture the first render's values and read
 * them forever. The rendered <see cref="label"/> reads state, so the countdown
 * redraws once a second.
 * </para>
 */
export function useExponentialRetry(options: ExponentialRetryOptions = {}): ExponentialRetry {
  const baseMs = options.baseMs ?? 2000
  const maxMs = options.maxMs ?? 60000
  const maxAttempts = options.maxAttempts ?? 10
  const revalidateMs = options.revalidateMs ?? 400

  // The source of truth. The state below mirrors it, only so renders see it.
  const pendingRef = useRef<Record<string, Pending>>({})
  const attemptRef = useRef<Record<string, number>>({})
  const reloadRef = useRef<Record<string, () => void>>({})
  const timerRef = useRef<Record<string, ReturnType<typeof setTimeout>>>({})

  const [pending, setPending] = useState<Record<string, Pending>>({})

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

  // A timer must not outlive the component that started it.
  useEffect(() => () => {
    for (const timer of Object.values(timerRef.current)) clearTimeout(timer)
    timerRef.current = {}
  }, [])

  const cancel = useCallback((key: string) => {
    clearTimer(key)
    delete attemptRef.current[key]
    delete reloadRef.current[key]
    if (!(key in pendingRef.current)) return
    const next = { ...pendingRef.current }
    delete next[key]
    pendingRef.current = next
    setPending(next)
  }, [])

  const schedule = useCallback((key: string, kind: RetryKind, reload: () => void) => {
    // Keep the newest reload, so a timer that fires later calls the current
    // fetch instead of the one captured whenever it was started.
    reloadRef.current[key] = reload

    // A timer already running keeps its deadline. Re-reading in between - a
    // person expanding another connection, a manual refresh - must not restart
    // it while they watch.
    const running = pendingRef.current[key]
    if (running && running.deadline > Date.now()) return

    const offline = kind === "offline"
    const attempt = offline ? (attemptRef.current[key] ?? 0) % maxAttempts : 0
    attemptRef.current[key] = attempt + 1

    const delay = offline ? Math.min(baseMs * 2 ** attempt, maxMs) : revalidateMs
    const next = {
      ...pendingRef.current,
      [key]: { attempt: attempt + 1, deadline: Date.now() + delay, max: maxAttempts, kind }
    }
    pendingRef.current = next
    setPending(next)

    clearTimer(key)
    timerRef.current[key] = setTimeout(() => {
      delete timerRef.current[key]
      reloadRef.current[key]?.()
    }, delay)
  }, [baseMs, maxMs, maxAttempts, revalidateMs])

  const label = (key: string): string | null => {
    const entry = pending[key]
    if (!entry) return null
    // A revalidate follows data that is already on screen: there is nothing to
    // report, and a countdown would read as if the database were in trouble.
    if (entry.kind !== "offline") return null
    const left = Math.max(0, Math.ceil((entry.deadline - now) / 1000))
    return `retrying ${entry.attempt}/${entry.max} · next in ${left}s`
  }

  const attemptOf = useCallback((key: string): number => attemptRef.current[key] ?? 0, [])

  // Callers destructure this and put the stable members in their own dependency
  // lists, so `schedule`, `cancel` and `attemptOf` never change identity.
  // `label` changes on every tick, which is what redraws the countdown.
  return { schedule, cancel, label, attemptOf }
}
