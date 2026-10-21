// Shared UI breakpoints and app constants.
// Kept in one module so hooks and components derive from the same source instead
// of hard-coding magic numbers in parallel.
// Matches Tailwind `sm` (640px): below this the sidebar becomes a hidden Sheet;
// at/above it the narrow icon strip stays visible.
export const MOBILE_BREAKPOINT = 640

// Below this the dashboard sidebar auto-collapses to its icon strip (still
// visible, not a mobile Sheet). Above it the persisted expand/collapse state
// is respected. Mirrors Tailwind `lg` (1024px).
// Single source of the threshold: the sidebar provider, the useIsCompact hook
// and the pre-paint inline script all read it, so the layout the browser
// paints before React boots can never disagree with the hydrated layout.
export const COMPACT_BREAKPOINT = 1024

// Matches Tailwind `xl` (1280px): at/above it the codebase page docks the file
// viewer as a right-hand column instead of opening the centered dialog. The JS
// gate (useMediaQuery) and the `xl:` Tailwind classes are two encodings of
// this one number — keep them in sync through here.
export const XL_BREAKPOINT = 1280
