import { Card, CardHeader, CardTitle, CardContent } from "@/components/ui/card"
import { Badge } from "@/components/ui/badge"
import { Loader2 } from "lucide-react"
import { DbConn, DbTab, SchemaInfo } from "@/app/dashboard/databases/types"
import { cacheBadgeCopy, formatColumnType, isStaleCache } from "@/app/dashboard/databases/utils/format-helpers"

export interface SchemaViewProps {
  schemaConnId: string | null
  schemaInfo: SchemaInfo | null
  schemaLoading: boolean
  activeTable: DbTab | null
  conns: DbConn[]
  /** The countdown a schema read shows while it retries, or null. */
  retryLabel: (key: string) => string | null
  onSelectTable: (table: string) => void
}

/** How each column of the schema table is sized, in one place. */
const COLUMN_WIDTHS = [
  "w-[45%]",
  "w-[30%]",
  "w-[15%]",
  "w-[10%]",
]

export const SchemaView = ({
  schemaConnId,
  schemaInfo,
  schemaLoading,
  activeTable,
  conns,
  retryLabel,
  onSelectTable,
}: SchemaViewProps) => {
  const t = activeTable ? schemaInfo?.tables?.find((x) => x.name === activeTable.table) : null
  const retry = schemaConnId ? retryLabel(schemaConnId) : null
  const cache = cacheBadgeCopy(schemaInfo?.cachedAt, schemaInfo?.cacheTtlSeconds)

  return (
    // The card fills the column and scrolls inside: the schema of a wide table
    // is taller than the viewport, and <main> clips (overflow-hidden) whatever
    // does not fit, so an unbounded card lost its bottom rows with no way to
    // reach them.
    <Card className="flex min-h-0 flex-1 flex-col">
      <CardHeader className="shrink-0">
        <CardTitle className="flex items-center gap-2">
          Schema · {activeTable?.table}
          {schemaInfo ? (
            <Badge
              variant="outline"
              className={
                isStaleCache(schemaInfo.cachedAt)
                  ? "border-orange-500/40 bg-orange-500/10 text-orange-700 dark:text-orange-400"
                  : schemaInfo.fromCache
                  ? "border-amber-500/30 bg-amber-500/10 text-amber-700 dark:text-amber-400"
                  : "border-emerald-600/30 bg-emerald-600/10 text-emerald-700 dark:text-emerald-400"
              }
              title={cache.expiry}
            >
              {schemaInfo.fromCache ? (isStaleCache(schemaInfo.cachedAt) ? "stale" : "cached") : "live · cached"} · {cache.age}
              {cache.expiry ? ` · ${cache.expiry}` : ""}
            </Badge>
          ) : null}
        </CardTitle>
      </CardHeader>
      <CardContent className="flex min-h-0 flex-1 flex-col gap-3">
        {retry ? (
          <div className="flex shrink-0 items-center gap-2">
            <Loader2 className="size-4 animate-spin text-muted-foreground" />
            <p className="text-sm text-muted-foreground">{retry}</p>
          </div>
        ) : schemaLoading ? (
          <p className="shrink-0 text-sm text-muted-foreground">Loading…</p>
        ) : null}

        {t ? (
          <div className="flex min-h-0 flex-1 flex-col gap-2">

            {/* The header row sits outside the scrolling region, so a wide
                table's header stays readable while its rows move under it.
                A sticky header alone would not do: the region scrolls in both
                directions, and sticking pins a cell horizontally as well. */}
            <div className="shrink-0 overflow-hidden">
              <table className="w-full table-fixed text-xs">
                <thead>
                  <tr>
                    {["Column", "Type", "Nullable", "PK"].map((label, i) => (
                      <th key={label} className={`border px-2 py-1 text-left ${COLUMN_WIDTHS[i]}`}>
                        {label}
                      </th>
                    ))}
                  </tr>
                </thead>
              </table>
            </div>

            <div className="min-h-0 flex-1 overflow-auto">
              <table className="w-full table-fixed text-xs">
                <tbody>
                  {t.columns?.map((col) => (
                    <tr key={col.name}>
                      <td className={`border px-2 py-1 ${COLUMN_WIDTHS[0]}`}>{col.name}</td>
                      <td className={`border px-2 py-1 ${COLUMN_WIDTHS[1]}`}>
                        {formatColumnType(col.dataType, col.maxLength)}
                      </td>
                      <td className={`border px-2 py-1 ${COLUMN_WIDTHS[2]}`}>{col.isNullable ? "yes" : "no"}</td>
                      <td className={`border px-2 py-1 ${COLUMN_WIDTHS[3]}`}>{col.isPrimaryKey ? "✓" : ""}</td>
                    </tr>
                  ))}
                </tbody>
              </table>

              {t.foreignKeys?.length ? (
                <p className="p-2 text-xs text-muted-foreground">
                  FKs: {t.foreignKeys.map((f) => `${f.column}→${f.referencesTable}.${f.referencesColumn}`).join(", ")}
                </p>
              ) : null}

              {t.indexes?.length ? (
                <div className="p-2">
                  <p className="text-xs font-medium mb-1">Indexes</p>
                  <div className="overflow-x-auto">
                    <table className="w-full text-xs table-fixed">
                      <thead>
                        <tr>
                          <th className="border px-2 py-1 text-left w-[50%]">Name</th>
                          <th className="border px-2 py-1 text-left w-[15%]">Unique</th>
                          <th className="border px-2 py-1 text-left w-[35%]">Columns</th>
                        </tr>
                      </thead>
                      <tbody>
                        {t.indexes.map((idx) => (
                          <tr key={idx.name}>
                            <td className="border px-2 py-1">{idx.name}</td>
                            <td className="border px-2 py-1">{idx.isUnique ? "✓" : ""}</td>
                            <td className="border px-2 py-1">{idx.columns.join(", ")}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </div>
              ) : null}
            </div>
          </div>
        ) : (
          <div className="min-h-0 flex-1 overflow-y-auto">
            {schemaInfo?.tables?.length ? (
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
          </div>
        )}
      </CardContent>
    </Card>
  )
}
