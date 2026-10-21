"use client"

import { Button } from "@/components/ui/button"
import { SEARCH_SCOPE_COPY, type SearchScope } from "./scope"

const SCOPES: readonly SearchScope[] = ["content", "path", "file"]

/**
 * The three-way search scope: what the query is matched against — the file
 * contents, the file's path, or the file's name.
 *
 * It sits before the query box, so the row reads "search in [Content] …".
 * On narrow screens it takes a row of its own: three labels plus the
 * workspace picker and the submit button leave the input no room to type in.
 */
export function SearchScopeToggle({
  scope,
  onChange,
}: {
  scope: SearchScope
  onChange: (scope: SearchScope) => void
}) {
  return (
    <div
      role="group"
      aria-label="Search scope"
      className="flex w-full shrink-0 gap-0.5 rounded-lg border p-0.5 lg:w-auto"
    >
      {SCOPES.map((value) => (
        <Button
          key={value}
          type="button"
          size="sm"
          variant={scope === value ? "secondary" : "ghost"}
          aria-pressed={scope === value}
          onClick={() => onChange(value)}
          className="flex-1 px-2 text-xs lg:flex-none"
        >
          {SEARCH_SCOPE_COPY[value].label}
        </Button>
      ))}
    </div>
  )
}
