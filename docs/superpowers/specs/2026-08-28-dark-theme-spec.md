# Design Specification: Dark Theme with System Default and Sidebar Toggle

**Date:** 2026-08-28  
**Status:** Approved  
**Scope:** `src/frontend/Eling.Dashboard`

---

## 1. Overview

Add full dark mode support to the Eling Next.js Dashboard. The theme follows the system / browser preference by default (`prefers-color-scheme`), with user overrides (System, Light, Dark) persisted in `localStorage` and toggleable directly from the Sidebar Footer.

---

## 2. Requirements & Behavior

1. **Default State:**
   - On first visit, the theme defaults to `system` (matches OS / browser dark or light preference).
   - If the OS theme changes while the app is open (and user mode is `system`), the dashboard updates immediately via `matchMedia` listener.

2. **User Override & Persistence:**
   - User can switch between `system`, `light`, and `dark`.
   - Stored in `localStorage.getItem("eling-theme")`.

3. **No Flash of Unstyled Content (No FOUC):**
   - An inline script in `<head>` (via `src/app/layout.tsx`) reads `localStorage` / system preference and applies the `.dark` class to `document.documentElement` before the initial render.

4. **UI Placement:**
   - Toggle button is placed in `AppSidebar` (`SidebarFooter`) with clear visual state showing current mode (💻 System, ☀️ Light, 🌙 Dark).

---

## 3. Architecture & Components

### A. Theme Provider / Hook (`src/components/theme-provider.tsx` & `use-theme.ts`)
- Context managing `theme` (`"system" | "light" | "dark"`) and `resolvedTheme` (`"light" | "dark"`).
- Synchronizes class `.dark` on `document.documentElement`.
- Listens to `window.matchMedia("(prefers-color-scheme: dark)")` for system changes.

### B. Inline Script (`src/app/layout.tsx`)
- Executes before hydration:
  ```js
  (function() {
    try {
      var theme = localStorage.getItem('eling-theme') || 'system';
      var isDark = theme === 'dark' || (theme === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches);
      if (isDark) document.documentElement.classList.add('dark');
      else document.documentElement.classList.remove('dark');
    } catch (_) {}
  })();
  ```

### C. Sidebar Footer Theme Toggle (`src/components/theme-toggle.tsx`)
- Integrated into `SidebarFooter` in `src/components/app-sidebar.tsx`.
- Compact 3-state toggle / dropdown or cycling button with accessible tooltips and clear icon states.

---

## 4. Testing & Verification

1. **Unit & Build Validation:**
   - `pnpm --prefix src/frontend/Eling.Dashboard build` exits 0.
   - `pnpm --prefix src/frontend/Eling.Dashboard lint` exits 0.
2. **Behavioral Checks:**
   - Test toggle cycle: System -> Light -> Dark -> System.
   - Verify persistence across page refresh.
   - Verify proper OKLCH color rendering in dark mode for sidebar, cards, tables, badges, and inputs.
