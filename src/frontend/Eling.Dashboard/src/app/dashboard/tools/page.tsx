import type { Metadata } from "next"
import { Tools } from "./Tools"
import { pageTitle } from "@/lib/page-title"

// The client page lives in ./Tools so this file can stay a Server Component -
// `metadata` can only be exported from one.
export const metadata: Metadata = {
  title: pageTitle("Tools"),
}

export default function ToolsPage() {
  return <Tools />
}
