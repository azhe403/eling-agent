import type { Metadata } from "next"
import { CreateMemory } from "./CreateMemory"
import { pageTitle } from "@/lib/page-title"

// The client page lives in ./CreateMemory so this file can stay a Server
// Component - `metadata` can only be exported from one.
export const metadata: Metadata = {
  title: pageTitle("Create Memory"),
}

export default function CreateMemoryPage() {
  return <CreateMemory />
}
