import { Card, CardHeader, CardTitle, CardContent } from "@/components/ui/card"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Loader2 } from "lucide-react"
import { DbConn, DbTab, SchemaInfo } from "@/app/dashboard/databases/types"
import { formatCacheAge, isStaleCache } from "@/app/dashboard/databases/utils/format-helpers"

export interface SchemaViewProps {
  schemaConnId: string | null
  schemaInfo: SchemaInfo | null
  schemaLoading: boolean
  activeTable: DbTab | null
  conns: DbConn[]
  /** The countdown a schema read shows while it retries, or null. */
  retryLabel: (key: string) => string | null
  onClose: () => void
  onSelectTable: (table: string) => void
}

export const SchemaView = ({
  schemaConnId,
  schemaInfo,
  schemaLoading,
  activeTable,
  conns,
  retryLabel,
  onClose,
  onSelectTable,
}: SchemaViewProps) => {
  const t = activeTable ? schemaInfo?.tables?.find((x) => x.name === activeTable.table) : null
  const retry = schemaConnId ? retryLabel(schemaConnId) : null

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          Schema · {conns.find((c) => c.id === schemaConnId)?.name}
          {schemaInfo?.fromCache ? (
            <Badge
              variant="outline"
              className={
                isStaleCache(schemaInfo.cachedAt)
                  ? "border-orange-500/40 bg-orange-500/10 text-orange-700 dark:text-orange-400"
                  : "border-amber-500/30 bg-amber-500/10 text-amber-700 dark:text-amber-400"
              }
            >
              {isStaleCache(schemaInfo.cachedAt) ? "stale" : "cached"} · {formatCacheAge(schemaInfo.cachedAt)}
            </Badge>
          ) : null}
        </CardTitle>
      </CardHeader>
      <CardContent>
        {retry ? (
          <div className="flex items-center gap-2">
            <Loader2 className="size-4 animate-spin text-muted-foreground" />
            <p className="text-sm text-muted-foreground">{retry}</p>
          </div>
        ) : schemaLoading ? (
          <p className="text-sm text-muted-foreground">Loading…</p>
        ) : null}

        {t ? (
          <div className="space-y-3">
            <p className="font-medium">
              {t.name} <span className="text-xs text-muted-foreground">({t.kind})</span>
            </p>
            <div className="overflow-x-auto">
              <table className="w-full text-xs">
                <thead>
                  <tr>
                    <th className="border px-2 py-1 text-left">Column</th>
                    <th className="border px-2 py-1 text-left">Type</th>
                    <th className="border px-2 py-1 text-left">Nullable</th>
                    <th className="border px-2 py-1 text-left">PK</th>
                  </tr>
                </thead>
                <tbody>
                  {t.columns?.map((col) => (
                    <tr key={col.name}>
                      <td className="border px-2 py-1">{col.name}</td>
                      <td className="border px-2 py-1">{col.dataType}</td>
                      <td className="border px-2 py-1">{col.isNullable ? "yes" : "no"}</td>
                      <td className="border px-2 py-1">{col.isPrimaryKey ? "✓" : ""}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            {t.foreignKeys?.length ? (
              <p className="text-xs text-muted-foreground">
                FKs: {t.foreignKeys.map((f) => `${f.column}→${f.referencesTable}.${f.referencesColumn}`).join(", ")}
              </p>
            ) : null}
          </div>
        ) : schemaInfo?.tables?.length ? (
          <div className="space-y-1">
            <p className="text-xs text-muted-foreground">Click a table to view its schema.</p>
            {schemaInfo.tables.map((tbl) => (
              <button
                key={tbl.name}
                className="flex w-full items-center gap-2 rounded border px-3 py-2 text-left text-sm hover:bg-accent"
                onClick={() => onSelectTable(tbl.name)}
              >
                <span>📄</span>
                <span className="font-medium">{tbl.name}</span>
                <span className="text-xs text-muted-foreground">({tbl.kind})</span>
              </button>
            ))}
          </div>
        ) : null}

        <div className="mt-3">
          <Button variant="outline" size="sm" onClick={onClose}>
            Close
          </Button>
        </div>
      </CardContent>
    </Card>
  )
}
