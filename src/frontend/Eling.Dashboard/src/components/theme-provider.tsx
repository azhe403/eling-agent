"use client"

import { ThemeProvider as NextThemesProvider } from "next-themes"
import * as React from "react"

// Thin wrapper that keeps the project's import path stable
// (@/components/theme-provider) while delegating to next-themes.
// next-themes handles the anti-flash inline script, system theme
// resolution, media-query listening, and dev-remount re-application.
export function ThemeProvider({
  children,
  ...props
}: React.ComponentProps<typeof NextThemesProvider>) {
  return <NextThemesProvider {...props}>{children}</NextThemesProvider>
}
