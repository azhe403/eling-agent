import { Button } from "@/components/ui/button"
import { DbTab, DbConn, QueryResult, SchemaInfo, RedisInspect } from "@/app/dashboard/databases/types"
import { isRedisTab } from "@/app/dashboard/databases/db-tabs"
import type { RedisPage } from "@/app/dashboard/databases/redis-browser"
import { ConnectionTree } from "./connection-tree/ConnectionTree"
import { SchemaView } from "./schema-view/SchemaView"
import { DataGrid } from "./data-grid/DataGrid"
import { SqlEditor } from "./sql-editor/SqlEditor"
import { RedisBrowser } from "./redis-browser/RedisBrowser"
import { ExplorerTabBar } from "./ExplorerTabBar"

export interface ExplorerPanelsProps {
  conns: DbConn[]
  expandedConns: Set<string>
  expandedDbs: Set<string>
  connSchemas: Record<string, SchemaInfo>
  dbSchemas: Record<string, SchemaInfo>
  redisDbs: Record<string, import("@/app/dashboard/databases/redis-browser").RedisDbInfo[]>
  activeTable: DbTab | null
  openTabs: DbTab[]
  onActivateTab: (idx: number) => void
  onCloseTab: (idx: number) => void
  onTabContextMenu: (e: React.MouseEvent, idx: number) => void
  rightTab: "data" | "schema" | "sql"
  connSort: import("@/app/dashboard/databases/conn-sorting").ConnSortKey
  connOrder: import("@/app/dashboard/databases/conn-sorting").ConnSortOrder
  loading: boolean
  isFading: boolean
  schemaConnId: string | null
  schemaInfo: SchemaInfo | null
  schemaLoading: boolean
  selectedId: string | null
  selectedIsReadOnly: boolean
  selectedIsRedis: boolean
  browseResult: QueryResult | null
  browseTotal: number | null
  browseLoading: boolean
  mutating: boolean
  editingCell: { row: number; col: number } | null
  editValue: string
  savingCell: boolean
  newRow: Record<string, string> | null
  pkNames: string[]
  sql: string
  qResult: QueryResult | null
  qLoading: boolean
  activeRedisDb: number
  redisPrefix: string
  redisRootPrefix: string
  redisPages: Record<string, RedisPage>
  redisExpanded: Set<string>
  redisInspectKey: string
  redisLoading: boolean
  redisInspect: RedisInspect | null
  redisInspectOpen: boolean
  isWide: boolean
  /** The countdown a schema read shows while it retries, or null. */
  retryLabel: (key: string) => string | null
  onSortChange: (sort: import("@/app/dashboard/databases/conn-sorting").ConnSortKey) => void
  onOrderChange: (order: import("@/app/dashboard/databases/conn-sorting").ConnSortOrder) => void
  onToggleConn: (id: string) => void
  onToggleRedisConn: (id: string) => void
  onToggleDb: (connId: string, database: string) => void
  onSelectTreeTable: (connId: string, table: string, database?: string | null) => void
  onOpenRedisDb: (connId: string, dbIndex: number) => void
  onTreeContextMenu: (e: React.MouseEvent, connId: string, table?: string, database?: string) => void
  onRegisterTreeItem: (key: string, el: HTMLButtonElement | null) => void
  onRightTabChange: (tab: "data" | "schema" | "sql") => void
  onSchemaClose: () => void
  onSchemaTableSelect: (table: string) => void
  onRefreshData: () => void
  onAddRow: () => void
  onStartEdit: (row: number, col: number, value: unknown) => void
  onEditValueChange: (value: string) => void
  onCommitEdit: (overrideValue?: string) => void
  onCancelEdit: () => void
  onNewRowChange: (row: Record<string, string> | null) => void
  onSubmitNewRow: () => void
  onPromptDeleteRow: (row: number) => void
  onCellContextMenu: (e: React.MouseEvent, row: number, col: number) => void
  onSqlChange: (sql: string) => void
  onRunQuery: () => void
  onPrefixChange: (prefix: string) => void
  onBrowsePrefix: (connId: string, db: number, prefix: string) => void
  onRootPrefix: (connId: string, db: number) => void
  onTogglePrefix: (connId: string, prefix: string) => void
  onSelectKey: (key: string) => void
  onLoadMore: (connId: string, db: number, prefix: string) => void
  onCollapseAll: (connId: string, db: number) => void
  onCloseInspect: () => void
  onInspectOpenChange: (open: boolean) => void
}

/**
 * The explorer: the tree of connections on the left, and the table, schema,
 * SQL or Redis view on the right.
 */
export const ExplorerPanels = (props: ExplorerPanelsProps) => {
  const {
    conns, expandedConns, expandedDbs, connSchemas, dbSchemas, redisDbs, activeTable, openTabs,
    rightTab, connSort, connOrder, loading, isFading, schemaConnId, schemaInfo, schemaLoading,
    selectedId, selectedIsReadOnly, selectedIsRedis, browseResult, browseTotal, browseLoading,
    mutating, editingCell, editValue, savingCell, newRow, pkNames, sql, qResult, qLoading,
    activeRedisDb, redisPrefix, redisRootPrefix, redisPages, redisExpanded, redisInspectKey,
    redisLoading, redisInspect, redisInspectOpen, isWide, retryLabel,
  } = props

  const activeIsRedis = isRedisTab(activeTable)

  return (
    <div className="flex min-h-0 flex-1 gap-4 overflow-hidden">
      <aside className="w-72 shrink-0 min-h-0 overflow-y-auto">
        <ConnectionTree
          conns={conns}
          expandedConns={expandedConns}
          expandedDbs={expandedDbs}
          connSchemas={connSchemas}
          dbSchemas={dbSchemas}
          redisDbs={redisDbs}
          activeTable={activeTable}
          connSort={connSort}
          connOrder={connOrder}
          loading={loading}
          isFading={isFading}
          retryLabel={retryLabel}
          onSortChange={props.onSortChange}
          onOrderChange={props.onOrderChange}
          onToggleConn={props.onToggleConn}
          onToggleRedisConn={props.onToggleRedisConn}
          onToggleDb={props.onToggleDb}
          onSelectTreeTable={props.onSelectTreeTable}
          onOpenRedisDb={props.onOpenRedisDb}
          onContextMenu={props.onTreeContextMenu}
          onRegisterTreeItem={props.onRegisterTreeItem}
        />
      </aside>

      <main className={`min-h-0 flex-1 overflow-hidden ${activeIsRedis ? "flex flex-col gap-4" : "space-y-4"}`}>
      <ExplorerTabBar
        conns={conns}
        openTabs={openTabs}
        activeTable={activeTable}
        onActivateTab={props.onActivateTab}
        onCloseTab={props.onCloseTab}
        onTabContextMenu={props.onTabContextMenu}
      />
        <div className="flex gap-2 border-b pb-2">
          <Button size="sm" variant={rightTab === "data" ? "default" : "outline"} onClick={() => props.onRightTabChange("data")}>
            Data
          </Button>
          {!activeIsRedis ? (
            <Button size="sm" variant={rightTab === "schema" ? "default" : "outline"} onClick={() => props.onRightTabChange("schema")}>
              Schema
            </Button>
          ) : null}
          {!activeIsRedis ? (
            <Button size="sm" variant={rightTab === "sql" ? "default" : "outline"} onClick={() => props.onRightTabChange("sql")}>
              SQL
            </Button>
          ) : null}
        </div>

        {rightTab === "schema" && schemaConnId && !selectedIsRedis ? (
          <SchemaView
            schemaConnId={schemaConnId}
            schemaInfo={schemaInfo}
            schemaLoading={schemaLoading}
            activeTable={activeTable}
            conns={conns}
            retryLabel={retryLabel}
            onClose={props.onSchemaClose}
            onSelectTable={props.onSchemaTableSelect}
          />
        ) : null}

        {activeIsRedis && rightTab === "data" ? (
          <RedisBrowser
            activeTable={activeTable}
            activeRedisDb={activeRedisDb}
            redisPrefix={redisPrefix}
            redisRootPrefix={redisRootPrefix}
            redisPages={redisPages}
            redisExpanded={redisExpanded}
            redisInspectKey={redisInspectKey}
            redisLoading={redisLoading}
            redisInspect={redisInspect}
            redisInspectOpen={redisInspectOpen}
            conns={conns}
            isWide={isWide}
            onPrefixChange={props.onPrefixChange}
            onBrowsePrefix={props.onBrowsePrefix}
            onRootPrefix={props.onRootPrefix}
            onTogglePrefix={props.onTogglePrefix}
            onSelectKey={props.onSelectKey}
            onLoadMore={props.onLoadMore}
            onCollapseAll={props.onCollapseAll}
            onCloseInspect={props.onCloseInspect}
            onInspectOpenChange={props.onInspectOpenChange}
          />
        ) : null}

        {!activeIsRedis && rightTab === "data" && !activeTable ? (
          <p className="text-sm text-muted-foreground">Select a table from the tree to view its data.</p>
        ) : null}

        {!activeIsRedis && rightTab === "data" && activeTable ? (
          <DataGrid
            activeTable={activeTable}
            schemaInfo={schemaInfo}
            browseResult={browseResult}
            browseLoading={browseLoading}
            browseTotal={browseTotal}
            selectedIsReadOnly={selectedIsReadOnly}
            mutating={mutating}
            editingCell={editingCell}
            editValue={editValue}
            savingCell={savingCell}
            newRow={newRow}
            pkColumnNames={pkNames}
            onRefresh={props.onRefreshData}
            onAddRow={props.onAddRow}
            onStartEdit={props.onStartEdit}
            onEditValueChange={props.onEditValueChange}
            onCommitEdit={props.onCommitEdit}
            onCancelEdit={props.onCancelEdit}
            onNewRowChange={props.onNewRowChange}
            onSubmitNewRow={props.onSubmitNewRow}
            onPromptDeleteRow={props.onPromptDeleteRow}
            onCellContextMenu={props.onCellContextMenu}
          />
        ) : null}

        {rightTab === "sql" && selectedId && !selectedIsRedis ? (
          <SqlEditor
            sql={sql}
            qResult={qResult}
            qLoading={qLoading}
            connName={conns.find((c) => c.id === selectedId)?.name ?? ""}
            onSqlChange={props.onSqlChange}
            onRunQuery={props.onRunQuery}
          />
        ) : null}
      </main>
    </div>
  )
}
