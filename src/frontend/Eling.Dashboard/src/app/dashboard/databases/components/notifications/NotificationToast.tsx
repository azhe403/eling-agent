import { useEffect } from "react"
import { AlertCircle, CheckCircle2, X } from "lucide-react"
import { Button } from "@/components/ui/button"
import { DbNotification } from "@/app/dashboard/databases/types"

/** How long a toast stays on screen. */
export const DB_NOTIFICATION_TTL_MS = 4000

export interface NotificationToastProps {
  notification: DbNotification
  onDismiss: (id: number) => void
}

/**
 * One toast. It dismisses itself after a while, so a reader who looks away
 * does not come back to a wall of messages.
 */
export const NotificationToast = ({ notification, onDismiss }: NotificationToastProps) => {
  useEffect(() => {
    const timer = setTimeout(() => onDismiss(notification.id), DB_NOTIFICATION_TTL_MS)
    return () => clearTimeout(timer)
  }, [notification.id, onDismiss])

  return (
    <div className="pointer-events-auto flex w-80 max-w-[calc(100vw-2rem)] items-start gap-2.5 rounded-lg border bg-background p-3 shadow-lg animate-in fade-in-0 slide-in-from-right-8 duration-300">
      <span
        className={
          notification.type === "error"
            ? "flex size-7 shrink-0 items-center justify-center rounded-full bg-destructive/15 text-destructive"
            : "flex size-7 shrink-0 items-center justify-center rounded-full bg-emerald-500/15 text-emerald-600 dark:text-emerald-400"
        }
      >
        {notification.type === "error" ? <AlertCircle className="size-4" /> : <CheckCircle2 className="size-4" />}
      </span>
      <div className="min-w-0 flex-1">
        <p className="text-sm font-medium">{notification.title}</p>
        {notification.text ? <p className="mt-0.5 text-xs break-words text-muted-foreground">{notification.text}</p> : null}
      </div>
      <Button
        variant="ghost"
        size="icon"
        className="size-6 shrink-0"
        onClick={() => onDismiss(notification.id)}
        aria-label="Dismiss notification"
      >
        <X className="size-3.5" />
      </Button>
    </div>
  )
}

export interface NotificationStackProps {
  notifications: DbNotification[]
  onDismiss: (id: number) => void
}

/** The stack of toasts, drawn over everything else. */
export const NotificationStack = ({ notifications, onDismiss }: NotificationStackProps) => {
  return (
    <div className="pointer-events-none fixed top-4 right-4 z-[100] flex flex-col gap-2">
      {notifications.map((n) => (
        <NotificationToast key={n.id} notification={n} onDismiss={onDismiss} />
      ))}
    </div>
  )
}
