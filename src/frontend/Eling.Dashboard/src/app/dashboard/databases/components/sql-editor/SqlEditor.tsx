import { Card, CardHeader, CardTitle, CardContent } from "@/components/ui/card"
import { Button } from "@/components/ui/button"
import { Badge } from "@/components/ui/badge"
import { Play } from "lucide-react"
import { QueryResult } from "@/app/dashboard/databases/types"
import { formatCacheAge, isStaleCache } from "@/app/dashboard/databases/utils/format-helpers"

export interface SqlEditorProps {
  sql: string
  qResult: QueryResult | null
  qLoading: boolean
  connName: string
  onSqlChange: (sql: string) => void
  onRunQuery: () => void
}

export const SqlEditor = ({ sql, qResult, qLoading, connName, onSqlChange, onRunQuery }: SqlEditorProps) => {
  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          Query - {connName}
          {qResult?.fromCache ? (
            <Badge
              variant="outline"
              className={
                isStaleCache(qResult.cachedAt)
                  ? "border-orange-500/40 bg-orange-500/10 text-orange-700 dark:text-orange-400"
                  : "border-amber-500/30 bg-amber-500/10 text-amber-700 dark:text-amber-400"
              }
            >
              {isStaleCache(qResult.cachedAt) ? "stale" : "cached"} · {formatCacheAge(qResult.cachedAt)}
            </Badge>
          ) : null}
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-3">
        <textarea
          className="w-full rounded border p-2 font-mono text-sm"
          value={sql}
          onChange={(e) => onSqlChange(e.target.value)}
          rows={4}
        />
        <Button onClick={onRunQuery} disabled={qLoading}>
          <Play className="mr-2 size-4" /> {qLoading ? "Running…" : "Run"}
        </Button>
        {qResult ? (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr>
                  {qResult.columns.map((col) => (
                    <th key={col} className="whitespace-nowrap border px-2 py-1 text-left">
                      {col}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {qResult.rows.map((row, i) => (
                  <tr key={i}>
                    {row.map((v, j) => (
                      <td key={j} className="whitespace-nowrap border px-2 py-1">
                        {String(v ?? "")}
                      </td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
            {qResult.truncated ? <p className="mt-1 text-xs text-muted-foreground">Truncated.</p> : null}
          </div>
        ) : null}
      </CardContent>
    </Card>
  )
}
