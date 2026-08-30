"use client"

import * as React from "react"

export type Theme = "system" | "light" | "dark"
export type ResolvedTheme = "light" | "dark"

interface ThemeContextType {
  theme: Theme
  resolvedTheme: ResolvedTheme
  setTheme: (theme: Theme) => void
}

const ThemeContext = React.createContext<ThemeContextType | undefined>(undefined)

const STORAGE_KEY = "eling-theme"

export function ThemeProvider({ children }: { children: React.ReactNode }) {
  // Lazily read from localStorage on the client to avoid an extra render cycle.
  // The initial value runs only on the client mount; SSR sees "system".
  const [theme, setThemeState] = React.useState<Theme>(() => {
    if (typeof window === "undefined") return "system"
    return (localStorage.getItem(STORAGE_KEY) as Theme) || "system"
  })
  const [resolvedTheme, setResolvedTheme] = React.useState<ResolvedTheme>("light")

  // Effect only handles DOM class toggling + media-query listener.
  // We never call setState directly here; resolvedTheme is updated by
  // applyTheme() below which is invoked from setTheme (a user action)
  // and from the media-query listener.
  React.useEffect(() => {
    const mediaQuery = window.matchMedia("(prefers-color-scheme: dark)")
    const applyTheme = (t: Theme) => {
      const isDark =
        t === "system"
          ? mediaQuery.matches
          : t === "dark"
      document.documentElement.classList.toggle("dark", isDark)
      setResolvedTheme(isDark ? "dark" : "light")
    }

    applyTheme(theme)

    const handleChange = () => {
      if (theme === "system") applyTheme("system")
    }

    mediaQuery.addEventListener("change", handleChange)
    return () => mediaQuery.removeEventListener("change", handleChange)
  }, [theme])

  const setTheme = React.useCallback(
    (newTheme: Theme) => {
      setThemeState(newTheme)
      try {
        localStorage.setItem(STORAGE_KEY, newTheme)
      } catch {}
      // Re-apply theme directly using the same logic as the effect.
      const isDark =
        newTheme === "system"
          ? window.matchMedia("(prefers-color-scheme: dark)").matches
          : newTheme === "dark"
      document.documentElement.classList.toggle("dark", isDark)
      setResolvedTheme(isDark ? "dark" : "light")
    },
    []
  )

  return (
    <ThemeContext.Provider value={{ theme, resolvedTheme, setTheme }}>
      {children}
    </ThemeContext.Provider>
  )
}

export function useTheme() {
  const context = React.useContext(ThemeContext)
  if (!context) {
    throw new Error("useTheme must be used within a ThemeProvider")
  }
  return context
}
