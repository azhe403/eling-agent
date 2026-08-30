# Dark Theme Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement full dark mode support with system preference as default and a 3-state toggle (System, Light, Dark) in the Sidebar Footer of the Eling Dashboard.

**Architecture:** A custom lightweight `ThemeProvider` manages theme state in React context, syncs with `localStorage`, listens for OS `matchMedia` changes, and prevents initial flash of light theme via a `<head>` inline script. An interactive `ThemeToggle` component in `AppSidebar` (`SidebarFooter`) enables switching modes.

**Tech Stack:** Next.js 16 (App Router), React 19, TypeScript, Tailwind CSS v4, Lucide Icons.

## Global Constraints

- Default theme must be `system`.
- No Flash of Unstyled Content (No FOUC) on initial page load / refresh.
- Persist user override in `localStorage["eling-theme"]`.
- `pnpm --prefix src/frontend/Eling.Dashboard build` must pass with 0 errors.
- Project hygiene: no absolute paths or personal names in source files.

---

### Task 1: Create Theme Context & Provider

**Files:**
- Create: `src/frontend/Eling.Dashboard/src/components/theme-provider.tsx`

**Interfaces:**
- Produces: `ThemeProvider` component and `useTheme()` hook returning `{ theme: "system" | "light" | "dark", resolvedTheme: "light" | "dark", setTheme: (theme: "system" | "light" | "dark") => void }`.

- [x] **Step 1: Write `theme-provider.tsx`**

```tsx
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
  const [theme, setThemeState] = React.useState<Theme>("system")
  const [resolvedTheme, setResolvedTheme] = React.useState<ResolvedTheme>("light")

  const updateResolvedTheme = React.useCallback((currentTheme: Theme) => {
    if (typeof window === "undefined") return
    let isDark = false
    if (currentTheme === "system") {
      isDark = window.matchMedia("(prefers-color-scheme: dark)").matches
    } else {
      isDark = currentTheme === "dark"
    }

    if (isDark) {
      document.documentElement.classList.add("dark")
      setResolvedTheme("dark")
    } else {
      document.documentElement.classList.remove("dark")
      setResolvedTheme("light")
    }
  }, [])

  React.useEffect(() => {
    const saved = (localStorage.getItem(STORAGE_KEY) as Theme) || "system"
    setThemeState(saved)
    updateResolvedTheme(saved)

    const mediaQuery = window.matchMedia("(prefers-color-scheme: dark)")
    const handleChange = () => {
      const current = (localStorage.getItem(STORAGE_KEY) as Theme) || "system"
      if (current === "system") {
        updateResolvedTheme("system")
      }
    }

    mediaQuery.addEventListener("change", handleChange)
    return () => mediaQuery.removeEventListener("change", handleChange)
  }, [updateResolvedTheme])

  const setTheme = React.useCallback(
    (newTheme: Theme) => {
      setThemeState(newTheme)
      try {
        localStorage.setItem(STORAGE_KEY, newTheme)
      } catch {}
      updateResolvedTheme(newTheme)
    },
    [updateResolvedTheme]
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
```

- [x] **Step 2: Add inline script and `ThemeProvider` in `src/app/layout.tsx`**

Modify `src/frontend/Eling.Dashboard/src/app/layout.tsx`:
Add inline anti-FOUC script inside `<head>` and wrap `{children}` with `ThemeProvider`.

```tsx
import type { Metadata } from "next";
import { Geist, Geist_Mono } from "next/font/google";
import { ThemeProvider } from "@/components/theme-provider";
import "./globals.css";

const geistSans = Geist({
  variable: "--font-geist-sans",
  subsets: ["latin"],
});

const geistMono = Geist_Mono({
  variable: "--font-geist-mono",
  subsets: ["latin"],
});

export const metadata: Metadata = {
  title: "Eling Dashboard",
  description: "Eling platform dashboard",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html
      lang="en"
      className={`${geistSans.variable} ${geistMono.variable} h-full antialiased`}
      suppressHydrationWarning
    >
      <head>
        <script
          dangerouslySetInnerHTML={{
            __html: `(function(){try{var t=localStorage.getItem('eling-theme')||'system';var d=t==='dark'||(t==='system'&&window.matchMedia('(prefers-color-scheme: dark)').matches);if(d)document.documentElement.classList.add('dark');else document.documentElement.classList.remove('dark');}catch(_){}})();`,
          }}
        />
      </head>
      <body className="min-h-full flex flex-col">
        <ThemeProvider>{children}</ThemeProvider>
      </body>
    </html>
  );
}
```

---

### Task 2: Create ThemeToggle Component and Integrate in SidebarFooter

**Files:**
- Create: `src/frontend/Eling.Dashboard/src/components/theme-toggle.tsx`
- Modify: `src/frontend/Eling.Dashboard/src/components/app-sidebar.tsx`

**Interfaces:**
- Consumes: `useTheme` from `src/components/theme-provider.tsx`
- Produces: `ThemeToggle` component rendered in `AppSidebar` (`SidebarFooter`).

- [x] **Step 1: Write `theme-toggle.tsx`**

```tsx
"use client"

import * as React from "react"
import { Monitor, Moon, Sun } from "lucide-react"
import { useTheme, type Theme } from "@/components/theme-provider"
import { Button } from "@/components/ui/button"

export function ThemeToggle() {
  const { theme, setTheme } = useTheme()
  const [mounted, setMounted] = React.useState(false)

  React.useEffect(() => {
    setMounted(true)
  }, [])

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
```

- [x] **Step 2: Add `SidebarFooter` with `ThemeToggle` in `src/components/app-sidebar.tsx`**

Modify `src/frontend/Eling.Dashboard/src/components/app-sidebar.tsx`:
Add `SidebarFooter` containing `<ThemeToggle />`.

---

### Task 3: Build & Verification

- [x] **Step 1: Test TypeScript and Next.js Build**

Run: `pnpm --prefix src/frontend/Eling.Dashboard build`
Expected: Build succeeded.

- [x] **Step 2: Verify in browser on `http://localhost:4427`**

Verify clicking the theme toggle cycles through System -> Light -> Dark -> System, persists across page refreshes, and renders all cards, badges, inputs, and sidebar cleanly in dark mode.
