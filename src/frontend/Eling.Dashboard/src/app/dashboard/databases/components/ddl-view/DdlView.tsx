"use client"

import { useState } from "react"
import { Check, Copy, Loader2 } from "lucide-react"

import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { copyText } from "@/lib/clipboard"

export interface DdlViewProps {
  table: string
  /** The table's own type from the schema: table or view. */
  kind: string | null
  ddl: string
  loading: boolean
  error: string | null
  onRetry: () => void
}

/** The DDL tab: the table's definition, with a copy button for the whole statement. */
export const DdlView = ({ table, kind, ddl, loading, error, onRetry }: DdlViewProps) => {
  const [copied, setCopied] = useState(false)

  const copy = async () => {
    const ok = await copyText(ddl)
    if (!ok) return
    setCopied(true)
    setTimeout(() => setCopied(false), 1500)
  }

  return (
    <Card className="flex min-h-0 flex-1 flex-col">
      <CardHeader className="shrink-0">
        <CardTitle className="flex items-center gap-2">
          DDL · {table}
          {kind ? (
            <Badge variant="outline" className="uppercase text-[10px]">
              {kind}
            </Badge>
          ) : null}
          <Button
            size="sm"
            variant="outline"
            className="ml-auto cursor-pointer"
            onClick={() => void copy()}
            disabled={!ddl || loading}
            aria-label="Copy DDL"
            title="Copy the whole statement to the clipboard"
          >
            {copied ? <Check className="size-4 text-green-600" /> : <Copy className="size-4" />}
            Copy
          </Button>
        </CardTitle>
      </CardHeader>
      <CardContent className="flex min-h-0 flex-1 flex-col">
        {loading ? (
          <p className="flex items-center gap-2 text-sm text-muted-foreground">
            <Loader2 className="size-4 animate-spin" /> Reading the table definition...
          </p>
        ) : error ? (
          <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-4 text-sm text-destructive">
            {error}
            <Button size="sm" variant="outline" className="ml-3 cursor-pointer" onClick={onRetry}>
              Retry
            </Button>
          </div>
        ) : ddl ? (
          <pre className="min-h-0 flex-1 overflow-auto whitespace-pre rounded-md border bg-muted/30 p-3 font-mono text-xs leading-relaxed">
            {ddl}
          </pre>
        ) : (
          <p className="text-sm text-muted-foreground">No DDL is available for this table.</p>
        )}
      </CardContent>
    </Card>
  )
}
