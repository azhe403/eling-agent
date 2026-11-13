import Link from "next/link"
import { ArrowUpRight } from "lucide-react"

import {
  Card,
  CardAction,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"

/**
 * The way in to the Commit Lease panel.
 *
 * Static on purpose: the panel itself is the live surface (SSE-driven), so
 * the home page does not fetch lease state just to render a door.
 */
export function CommitLeaseCard() {
  return (
    <Card>
      <CardHeader>
        <CardTitle>Commit Lease</CardTitle>
        <CardAction>
          <Link
            href="/dashboard/commit-lease"
            className="inline-flex items-center gap-1 text-xs font-medium text-muted-foreground transition-colors hover:text-foreground"
          >
            Open panel
            <ArrowUpRight className="size-3" />
          </Link>
        </CardAction>
      </CardHeader>
      <CardContent>
        <p className="text-xs text-muted-foreground">
          Who holds the commit slot right now, and what was released recently.
          Force-release a wedged lease from the panel.
        </p>
      </CardContent>
    </Card>
  )
}
