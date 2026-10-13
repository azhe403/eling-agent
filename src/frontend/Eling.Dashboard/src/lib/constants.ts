// Shared UI breakpoints and app constants.
// Kept in one module so hooks and components derive from the same source instead
// of hard-coding magic numbers in parallel.
// Matches Tailwind `sm` (640px): below this the sidebar becomes a hidden Sheet;
// at/above it the narrow icon strip stays visible.
export const MOBILE_BREAKPOINT = 640

// Matches Tailwind `xl` (1280px): at/above it the codebase page docks the file
// viewer as a right-hand column instead of opening the centered dialog. The JS
// gate (useMediaQuery) and the `xl:` Tailwind classes are two encodings of
// this one number — keep them in sync through here.
export const XL_BREAKPOINT = 1280
