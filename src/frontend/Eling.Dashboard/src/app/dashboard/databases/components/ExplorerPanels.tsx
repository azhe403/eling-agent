import { Button } from "@/components/ui/button"
import { DbTab, DbConn, QueryResult, SchemaInfo, RedisInspect, EsInspect, ElasticsearchIndexInfo } from "@/app/dashboard/databases/types"
import { useEffect, useState } from "react"
import { List, X } from "lucide-react"
import { Badge } from "@/components/ui/badge"
import { isRedisTab, isElasticsearchIndexTab, tabKey } from "@/app/dashboard/databases/db-tabs"
import type { RedisPage, RedisPrefixPreview } from "@/app/dashboard/databases/redis-browser"
import { visibleIndices } from "@/app/dashboard/databases/es-browser"
import { ConnectionTree } from "./connection-tree/ConnectionTree"
import { SchemaView } from "./schema-view/SchemaView"
import { DataGrid } from "./data-grid/DataGrid"
import { SqlEditor } from "./sql-editor/SqlEditor"
import { RedisBrowser } from "./redis-browser/RedisBrowser"
import { ElasticsearchIndexPanel } from "./elasticsearch/ElasticsearchIndexPanel"
import { ExplorerTabBar } from "./ExplorerTabBar"
import type { BrowseFilter, BrowseSort } from "@/app/dashboard/databases/filter-model"

export interface ExplorerPanelsProps {
  conns: DbConn[]
  expandedConns: Set<string>
  expandedDbs: Set<string>
  connSchemas: Record<string, SchemaInfo>
  dbSchemas: Record<string, SchemaInfo>
  redisDbs: Record<string, import("@/app/dashboard/databases/redis-browser").RedisDbInfo[]>
  esIndices: Record<string, import("@/app/dashboard/databases/types").ElasticsearchIndexInfo[]>
  /** The connections with the ".system indices" toggle on. */
  esSystemVisible: Set<string>
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
  redisAutoRefresh: boolean
  redisAutoRefreshMs: number
  redisDeleteTarget: string | null
  redisDeleteOpen: boolean
  redisPreview: RedisPrefixPreview | null
  redisPreviewLoading: boolean
  redisMutating: boolean
  redisPrefixPreviewOpen: boolean
  redisPrefixPreviewTarget: string | null
  /** Elasticsearch index being inspected in split-view (like Redis). */
  esInspect: import("@/app/dashboard/databases/types").EsInspect | null
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
  onOpenEsIndex: (connId: string, index: string) => void
  onToggleEsConn: (id: string) => void
  onToggleEsSystem: (connId: string) => void
  onTreeContextMenu: (e: React.MouseEvent, connId: string, table?: string, database?: string) => void
  onRegisterTreeItem: (key: string, el: HTMLButtonElement | null) => void
  onRightTabChange: (tab: "data" | "schema" | "sql") => void
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
  onDeleteTargetChange: (key: string | null) => void
  onDeleteOpenChange: (open: boolean) => void
  onPrefixPreviewOpenChange: (open: boolean) => void
  onPreviewPrefix: (connId: string, db: number, prefix: string) => void
  onConfirmDeleteKey: (connId: string, db: number, key: string) => void
  onConfirmDeletePrefix: (connId: string, db: number, prefix: string) => void
  onAutoRefreshChange: (enabled: boolean) => void
  onAutoRefreshMsChange: (ms: number) => void
  /** Called when the ES inspect target changes (split-view like Redis). */
  onEsInspectChange: (inspect: import("@/app/dashboard/databases/types").EsInspect | null) => void
  /** Effective per-tab view state, computed by the page. */
  browseLimit: number
  browsePage: number
  browseFilters?: BrowseFilter[]
  browseSort?: BrowseSort[]
  manualCondition?: string
  /** Whether the committed manual condition participates. Absent = enabled. */
  manualEnabled?: boolean
  filterOpen?: boolean
  onFilterOpenChange?: (open: boolean) => void
  onApplyBrowse: (next: { filters: BrowseFilter[]; sort: BrowseSort[]; manualCondition: string; manualEnabled: boolean }) => void
  onCycleSortColumn: (column: string) => void
  onBrowseLimitChange: (limit: number) => void
  onBrowsePageChange: (page: number) => void
}

/**
 * The explorer: the tree of connections on the left, and the table, schema,
 * SQL or Redis view on the right.
 */
export const ExplorerPanels = (props: ExplorerPanelsProps) => {
  const {
    conns, expandedConns, expandedDbs, connSchemas, dbSchemas, redisDbs, esIndices, esSystemVisible, activeTable, openTabs,
    rightTab, connSort, connOrder, loading, isFading, schemaConnId, schemaInfo, schemaLoading,
    selectedId, selectedIsReadOnly, selectedIsRedis, browseResult, browseTotal, browseLoading, browseLimit, browsePage,
    browseFilters, browseSort, manualCondition, manualEnabled, filterOpen, onFilterOpenChange,
    mutating, editingCell, editValue, savingCell, newRow, pkNames, sql, qResult, qLoading,
    activeRedisDb, redisPrefix, redisRootPrefix, redisPages, redisExpanded, redisInspectKey,
    redisLoading, redisInspect, redisInspectOpen,
    redisDeleteTarget, redisDeleteOpen, redisPreview, redisPreviewLoading, redisMutating,
    redisPrefixPreviewOpen, redisPrefixPreviewTarget, redisAutoRefresh, redisAutoRefreshMs,
    esInspect, onEsInspectChange,
    isWide, retryLabel,
  } = props

  const [connectionsOpen, setConnectionsOpen] = useState(false)
  const activeIsRedis = isRedisTab(activeTable)
  const activeIsEsIndex = isElasticsearchIndexTab(activeTable)

  // Escape closes the overlay; no-op when it is closed.
  useEffect(() => {
    if (!connectionsOpen) return
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape") setConnectionsOpen(false)
    }
    window.addEventListener("keydown", onKeyDown)
    return () => window.removeEventListener("keydown", onKeyDown)
  }, [connectionsOpen])

  const closeConnections = () => setConnectionsOpen(false)

  const renderConnectionsTree = () => (
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
      onSelectTreeTable={(connId, table, database) => {
        props.onSelectTreeTable(connId, table, database)
        setConnectionsOpen(false)
      }}
      onOpenRedisDb={(connId, dbIndex) => {
        props.onOpenRedisDb(connId, dbIndex)
        setConnectionsOpen(false)
      }}
      esIndices={esIndices}
      esSystemVisible={esSystemVisible}
      onToggleEsSystem={props.onToggleEsSystem}
      onToggleEsConn={props.onToggleEsConn}
      onOpenEsIndex={(connId, index) => {
        props.onOpenEsIndex(connId, index)
        setConnectionsOpen(false)
      }}
      onEsInspectChange={(inspect) => {
        props.onEsInspectChange(inspect)
        setConnectionsOpen(false)
      }}
      onContextMenu={props.onTreeContextMenu}
      onRegisterTreeItem={props.onRegisterTreeItem}
    />
  )

  return (
    <div className="flex min-h-0 flex-1 gap-4 overflow-hidden">
      {isWide ? (
        <aside className="w-72 shrink-0 min-h-0 overflow-y-auto">{renderConnectionsTree()}</aside>
      ) : null}

      {!isWide && connectionsOpen ? (
        <div className="fixed inset-0 z-50" role="dialog" aria-modal="true" aria-label="Connections">
          <button type="button" className="absolute inset-0 cursor-pointer bg-black/50" onClick={closeConnections} aria-label="Close connections" />
          <div className="relative z-10 h-full w-80 shrink-0 overflow-y-auto border-r bg-background p-3 pr-1 shadow-xl">
            {renderConnectionsTree()}
          </div>
        </div>
      ) : null}

      <main className={`min-h-0 flex-1 overflow-hidden flex flex-col gap-4`}>
        {!isWide ? (
          <div className="flex shrink-0 items-center">
            <Button
              variant="outline"
              size="sm"
              className="cursor-pointer gap-1.5"
              aria-expanded={connectionsOpen}
              aria-controls="connections-overlay"
              onClick={() => setConnectionsOpen((v) => !v)}
              title={connectionsOpen ? "Hide connections" : "Show connections"}
            >
              <List className="size-4" />
              <span className="ml-1">Connections</span>
              <Badge variant="outline" className="h-4 min-w-4 justify-center px-1 text-[10px] tabular-nums">
                {conns.length}
              </Badge>
            </Button>
          </div>
        ) : null}
      <ExplorerTabBar
        conns={conns}
        openTabs={openTabs}
        activeTable={activeTable}
        onActivateTab={props.onActivateTab}
        onCloseTab={props.onCloseTab}
        onTabContextMenu={props.onTabContextMenu}
      />
        <div className="flex shrink-0 gap-2 border-b pb-2">
          <Button size="sm" variant={rightTab === "data" ? "default" : "outline"} onClick={() => props.onRightTabChange("data")}>
            Data
          </Button>
          {!activeIsRedis && !activeIsEsIndex ? (
            <Button size="sm" variant={rightTab === "schema" ? "default" : "outline"} onClick={() => props.onRightTabChange("schema")}>
              Schema
            </Button>
          ) : null}
          {!activeIsRedis && !activeIsEsIndex ? (
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
            redisDeleteTarget={redisDeleteTarget}
            redisDeleteOpen={redisDeleteOpen}
            redisPreview={redisPreview}
            redisPreviewLoading={redisPreviewLoading}
            redisMutating={redisMutating}
            redisPrefixPreviewOpen={redisPrefixPreviewOpen}
            redisPrefixPreviewTarget={redisPrefixPreviewTarget}
            redisAutoRefresh={redisAutoRefresh}
            redisAutoRefreshMs={redisAutoRefreshMs}
            onAutoRefreshChange={props.onAutoRefreshChange}
            onAutoRefreshMsChange={props.onAutoRefreshMsChange}
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
            onDeleteTargetChange={props.onDeleteTargetChange}
            onDeleteOpenChange={props.onDeleteOpenChange}
            onPrefixPreviewOpenChange={props.onPrefixPreviewOpenChange}
            onPreviewPrefix={props.onPreviewPrefix}
            onConfirmDeleteKey={props.onConfirmDeleteKey}
            onConfirmDeletePrefix={props.onConfirmDeletePrefix}
          />
        ) : null}

        {!activeIsRedis && rightTab === "data" && !activeTable ? (
          <p className="text-sm text-muted-foreground">Select a table from the tree to view its data.</p>
        ) : null}

        {/* The Elasticsearch index panel: one Discover view per index tab. */}
        {activeIsEsIndex && rightTab === "data" && activeTable?.kind === "elasticsearch-index" ? (
          ((() => {
            const connId = activeTable.connId
            const index = activeTable.index ?? ""
            const inspectMatches = esInspect?.connId === connId && esInspect?.index === index

            // Split view: index tree on left, panel on right (like Redis)
            if (isWide && esInspect && inspectMatches) {
              return (
                <div className="grid min-h-0 flex-1 grid-rows-[1fr] gap-2 xl:grid-cols-[360px_minmax(0,1fr)]">
                  {/* Left: Index tree for this connection */}
                  <div className="flex min-h-0 min-w-0 flex-col overflow-hidden rounded-md border p-3">
                    <div className="flex shrink-0 items-center justify-between gap-2">
                      <p className="text-xs text-muted-foreground">{connId} indices</p>
                      <Button
                        variant="ghost"
                        size="sm"
                        className="size-6 shrink-0 p-0"
                        aria-label="Close index inspector"
                        onClick={() => onEsInspectChange(null)}
                      >
                        <X className="size-3.5" />
                      </Button>
                    </div>
                    <div className="min-h-0 flex-1 overflow-y-auto pr-1" style={{ scrollbarGutter: "stable" }}>
                      {esIndices[connId] ? (
                        visibleIndices(esIndices[connId] ?? [], esSystemVisible.has(connId)).map((idx) => (
                          <button
                            key={idx.name}
                            type="button"
                            className={`w-full text-left px-2 py-1 text-xs rounded hover:bg-muted ${
                              idx.name === index ? "bg-muted font-medium" : ""
                            }`}
                            onClick={() => onEsInspectChange({ connId, index: idx.name })}
                          >
                            {idx.name}
                          </button>
                        ))
                      ) : (
                        <p className="text-sm text-muted-foreground">No indices loaded.</p>
                      )}
                    </div>
                  </div>
                  {/* Right: Elasticsearch panel */}
                  <div className="flex min-h-0 flex-col rounded-md border p-3">
                    <ElasticsearchIndexPanel
                      key={tabKey(activeTable)}
                      conns={conns}
                      connId={connId}
                      index={index}
                      isWide={false}
                      showFieldSidebar={true}
                    />
                  </div>
                </div>
              )
            }

            // Full-width panel (default)
            return (
              <ElasticsearchIndexPanel
                key={tabKey(activeTable)}
                conns={conns}
                connId={connId}
                index={index}
                isWide={isWide}
              />
            )
          }))()
        ) : null}
        {!activeIsRedis && !activeIsEsIndex && rightTab === "data" && activeTable ? (
          <DataGrid
            key={tabKey(activeTable)}
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
            browseLimit={browseLimit}
            browsePage={browsePage}
            browseFilters={browseFilters}
            browseSort={browseSort}
            manualCondition={manualCondition}
            manualEnabled={manualEnabled}
            filterOpen={filterOpen}
            onFilterOpenChange={onFilterOpenChange}
            onApplyBrowse={props.onApplyBrowse}
            onCycleSortColumn={props.onCycleSortColumn}
            onBrowseLimitChange={props.onBrowseLimitChange}
            onBrowsePageChange={props.onBrowsePageChange}
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
