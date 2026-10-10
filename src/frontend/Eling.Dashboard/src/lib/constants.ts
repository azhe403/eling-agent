// Shared UI breakpoints and app constants.
// Kept in one module so hooks and components derive from the same source instead
// of hard-coding magic numbers in parallel.
// Matches Tailwind `sm` (640px): below this the sidebar becomes a hidden Sheet;
// at/above it the narrow icon strip stays visible.
export const MOBILE_BREAKPOINT = 640
