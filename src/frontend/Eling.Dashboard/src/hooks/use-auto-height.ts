import * as React from "react"

/**
 * Auto-resizes a textarea to fit its content as the user types.
 * The browser's default resize handle is preserved via `resize-y` class on the textarea.
 *
 * @param ref - Ref to the textarea element.
 * @param deps - Values that should trigger a re-measure (typically the value, or a derived length).
 * @param minHeight - Minimum height in pixels (default 80).
 * @param buffer - Extra pixels added below the last line for breathing room (default 24).
 */
export function useAutoHeight(
  ref: React.RefObject<HTMLTextAreaElement | null>,
  deps: React.DependencyList,
  minHeight = 80,
  buffer = 24,
): void {
  React.useLayoutEffect(() => {
    const el = ref.current
    if (!el) return
    el.style.height = "auto"
    const nextHeight = Math.max(minHeight, el.scrollHeight + buffer)
    el.style.height = `${nextHeight}px`
    // The caller passes the deps that should drive re-measurement; we
    // intentionally do not enumerate them here (they include `ref` and the
    // value of the textarea, neither of which belongs in the effect body).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps)
}
