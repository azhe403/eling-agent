// Inline script that runs during HTML parsing (before first paint) on hard
// loads, without triggering React's client-side <script> warning.
//
// Per the Next.js "Preventing flash before hydration" guide:
// - Server renders type="text/javascript" so the browser executes it
//   synchronously while parsing the HTML head.
// - Client renders type="text/plain" so React treats it as an inert script
//   data block instead of warning that created scripts never execute.
// suppressHydrationWarning accepts the type change during hydration.
export function InlineScript({ html }: { html: string }) {
  return (
    <script
      type={typeof window === "undefined" ? "text/javascript" : "text/plain"}
      suppressHydrationWarning
      dangerouslySetInnerHTML={{ __html: html }}
    />
  )
}
