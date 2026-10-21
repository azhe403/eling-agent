import type { Metadata } from "next"
import { Databases } from "./Databases"
import { pageTitle } from "@/lib/page-title"

// The client page lives in ./Databases so this file can stay a Server
// Component - `metadata` can only be exported from one.
export const metadata: Metadata = {
  title: pageTitle("Databases"),
}

export default function DatabasesPage() {
  return <Databases />
}
