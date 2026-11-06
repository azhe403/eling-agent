import type { Metadata } from "next"
import { Settings } from "./Settings"
import { pageTitle } from "@/lib/page-title"

// The client page lives in ./Settings so this file can stay a Server Component -
// `metadata` can only be exported from one.
export const metadata: Metadata = {
  title: pageTitle("Settings"),
}

export default function SettingsPage() {
  return <Settings />
}
