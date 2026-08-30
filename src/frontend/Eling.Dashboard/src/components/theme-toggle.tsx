"use client"

import * as React from "react"
import { Monitor, Moon, Sun } from "lucide-react"
import { useTheme } from "@/components/theme-provider"
import { Button } from "@/components/ui/button"

export function ThemeToggle() {
  const { theme, setTheme } = useTheme()
  // Lazily detect client mount to prevent layout shift without needing an effect
  const [mounted] = React.useState(
    typeof window !== "undefined" && typeof document !== "undefined"
  )

  if (!mounted) {
    return (
      <div className="flex items-center justify-between px-2 py-1.5 rounded-md text-xs text-muted-foreground">
        <span>Theme</span>
        <div className="size-4 animate-pulse bg-muted rounded" />
      </div>
    )
  }

  const cycleTheme = () => {
    if (theme === "system") setTheme("light")
    else if (theme === "light") setTheme("dark")
    else setTheme("system")
  }

  const getLabel = () => {
    if (theme === "system") return "System"
    if (theme === "light") return "Light"
    return "Dark"
  }

  return (
    <Button
      variant="ghost"
      size="sm"
      onClick={cycleTheme}
      className="w-full justify-between px-2 py-1.5 h-auto text-xs text-muted-foreground hover:text-foreground hover:bg-sidebar-accent"
      title={`Theme: ${getLabel()} (Click to change)`}
    >
      <span className="flex items-center gap-2">
        {theme === "system" && <Monitor className="size-4" />}
        {theme === "light" && <Sun className="size-4 text-amber-500" />}
        {theme === "dark" && <Moon className="size-4 text-blue-400" />}
        <span>Theme</span>
      </span>
      <span className="font-medium capitalize text-[11px] bg-sidebar-accent px-1.5 py-0.5 rounded text-sidebar-accent-foreground border border-sidebar-border">
        {getLabel()}
      </span>
    </Button>
  )
}
