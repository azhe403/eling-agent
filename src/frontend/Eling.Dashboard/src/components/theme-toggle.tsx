"use client"

import * as React from "react"
import { Monitor, Moon, Sun } from "lucide-react"
import { useTheme } from "next-themes"

import { cn } from "@/lib/utils"

// Order matters: this drives button order and the cycle direction (intentionally unused now).
const themeOptions = [
  { value: "system", label: "System", icon: Monitor },
  { value: "light", label: "Light", icon: Sun },
  { value: "dark", label: "Dark", icon: Moon },
] as const

type ThemeValue = (typeof themeOptions)[number]["value"]

export function ThemeToggle() {
  const { theme, setTheme } = useTheme()
  // Detect client mount in an effect: the server render and the client's first
  // (hydration) render both see mounted=false, so they agree. The control only
  // appears after mount, avoiding layout shift without a hydration mismatch.
  // useSyncExternalStore: server snapshot = false (hidden), client snapshot = true (visible).
  // This replaces useState+setTimeout/useEffect and avoids the React 19.2
  // setState-in-effect rule while still preventing hydration mismatch.
  const mounted = React.useSyncExternalStore(
    () => () => {},
    () => true,
    () => false,
  )

  if (!mounted) {
    return (
      <div className="flex items-center justify-between px-2 py-1.5 rounded-md text-xs text-muted-foreground">
        <span>Theme</span>
        <div className="flex items-center gap-1">
          <div className="size-6 animate-pulse bg-muted rounded" />
          <div className="size-6 animate-pulse bg-muted rounded" />
          <div className="size-6 animate-pulse bg-muted rounded" />
        </div>
      </div>
    )
  }

  const current = (theme ?? "system") as ThemeValue

  const renderIcon = (value: ThemeValue) => {
    if (value === "system") return <Monitor className="size-3.5" />
    if (value === "light") return <Sun className="size-3.5 text-amber-500" />
    return <Moon className="size-3.5 text-blue-400" />
  }

  return (
    <div className="flex items-center justify-between px-2 py-1.5 rounded-md text-xs text-muted-foreground">
      <span>Theme</span>
      <div
        role="group"
        aria-label="Theme"
        className="inline-flex items-center rounded-md border border-sidebar-border bg-sidebar-accent/40 p-0.5"
      >
        {themeOptions.map((opt) => {
          const isActive = current === opt.value
          return (
            <button
              key={opt.value}
              type="button"
              aria-pressed={isActive}
              aria-label={`${opt.label} theme`}
              title={opt.label}
              onClick={() => setTheme(opt.value)}
              className={cn(
                "inline-flex size-6 items-center justify-center rounded-sm transition-colors",
                "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/50",
                isActive
                  ? "bg-sidebar-primary text-sidebar-primary-foreground shadow-sm"
                  : "text-muted-foreground hover:text-foreground hover:bg-sidebar-accent"
              )}
            >
              {renderIcon(opt.value)}
            </button>
          )
        })}
      </div>
    </div>
  )
}
