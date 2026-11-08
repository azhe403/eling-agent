"use client"

import { useCallback, useState } from "react"
import { DbNotification } from "../types"

/** How many toasts are on screen at once; the oldest one goes. */
const MAX_DB_NOTIFICATIONS = 4

let nextId = 0

export type NotifyDb = (type: DbNotification["type"], title: string, text?: string) => void

/**
 * The toasts the explorer shows.
 *
 * Errors go through here rather than into the page body, because an error in
 * one panel must not push the rest of the page around.
 */
export function useNotifications() {
  const [notifications, setNotifications] = useState<DbNotification[]>([])

  const dismiss = useCallback((id: number) => {
    setNotifications((current) => current.filter((item) => item.id !== id))
  }, [])

  const notify: NotifyDb = useCallback((type, title, text) => {
    nextId += 1
    const id = nextId
    setNotifications((current) => [...current.slice(-(MAX_DB_NOTIFICATIONS - 1)), { id, type, title, text }])
  }, [])

  return { notifications, notify, dismiss }
}
