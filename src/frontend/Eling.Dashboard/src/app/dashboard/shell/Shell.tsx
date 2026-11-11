"use client"

import { SquareTerminal } from "lucide-react"

import { DashboardHeader } from "@/components/dashboard-header"
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"

import { ShellConfigCard } from "./ShellConfigCard"

export function Shell() {
  return (
    <>
      <DashboardHeader label="Shell" />

      <div className="flex flex-col gap-4 p-4">
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <SquareTerminal className="size-4" />
              Shell Execution
            </CardTitle>
            <CardDescription>
              <code>shell_execute</code> runs arbitrary shell commands with the same privileges as the
              backend process. It is RCE-equivalent: treat enabling it as handing a terminal to the agent.
            </CardDescription>
          </CardHeader>
          <CardContent className="flex flex-col gap-2 text-xs leading-relaxed text-muted-foreground">
            <p>
              It stays <strong>disabled by default</strong>. Enabling it only in a trusted, single-user workspace;
              leave it off on shared or remote backends.
            </p>
            <p>
              The shell configuration lives in <code>shell-config.json</code> under the user scope and is shared
              with running MCP hosts on the next read. The switches and lists below edit it directly; changes apply
              immediately.
            </p>
            <ul className="list-inside list-disc space-y-0.5">
              <li><strong>Enable</strong> — master switch. Off means every call returns <code>shell_disabled</code>.</li>
              <li><strong>Boundary</strong> — restrict to the working directory plus the whitelist below, or allow any directory.</li>
              <li><strong>Root guardrail</strong> — refuses destructive commands against a filesystem root (e.g. <code>rm -rf /</code>).</li>
              <li><strong>Allowed directories / Blocked commands</strong> — per-rule enable switches; both can be paused without deleting.</li>
              <li><strong>Timeout / output cap</strong> — per-call limits enforced by the tool; process tree killed on timeout.</li>
            </ul>
          </CardContent>
        </Card>

        <ShellConfigCard />
      </div>
    </>
  )
}
