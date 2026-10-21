import type { Metadata } from "next"
import { Judge } from "./Judge"
import { pageTitle } from "@/lib/page-title"

// The client page lives in ./Judge so this file can stay a Server Component -
// `metadata` can only be exported from one.
export const metadata: Metadata = {
  title: pageTitle("Judge"),
}

export default function JudgePage() {
  return <Judge />
}
