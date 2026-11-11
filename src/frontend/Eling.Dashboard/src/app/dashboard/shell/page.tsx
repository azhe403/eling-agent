import type { Metadata } from "next"
import { Shell } from "./Shell"
import { pageTitle } from "@/lib/page-title"

// The client page lives in ./Shell so this file can stay a Server Component -
// `metadata` can only be exported from one.
export const metadata: Metadata = {
  title: pageTitle("Shell"),
}

export default function ShellPage() {
  return <Shell />
}
