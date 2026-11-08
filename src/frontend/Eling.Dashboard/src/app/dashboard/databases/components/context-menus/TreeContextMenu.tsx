/** Where the tree context menu was opened. */
export type TreeContextMenuState = {
  x: number
  y: number
  connId: string
  table?: string
  database?: string
}
export interface TreeContextMenuProps {
  menu: TreeContextMenuState
  onOpenData: () => void
  onQueryTable: () => void
  onEditConnection: () => void
  onRefreshSchema: () => void
  onDeleteConnection: () => void
  onClose: () => void
}

/**
 * The menu that opens when a connection or a table in the tree is
 * right-clicked.
 */
export const TreeContextMenu = ({
  menu,
  onOpenData,
  onQueryTable,
  onEditConnection,
  onRefreshSchema,
  onDeleteConnection,
  onClose,
}: TreeContextMenuProps) => {
  return (
    <div className="fixed inset-0 z-50" onClick={onClose}>
      <div
        className="absolute min-w-[160px] rounded-md border border-border bg-popover p-1 shadow-md"
        style={{ left: menu.x, top: menu.y }}
        onClick={(e) => e.stopPropagation()}
      >
        {menu.table ? (
          <>
            <button
              className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-accent"
              onClick={() => {
                onOpenData()
                onClose()
              }}
            >
              Open data
            </button>
            <button
              className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-accent"
              onClick={() => {
                onQueryTable()
                onClose()
              }}
            >
              Query table
            </button>
          </>
        ) : null}
        <button
          className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-accent"
          onClick={() => {
            onEditConnection()
            onClose()
          }}
        >
          Edit connection
        </button>
        <button
          className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-accent"
          onClick={() => {
            onRefreshSchema()
            onClose()
          }}
        >
          Refresh schema
        </button>
        <button
          className="block w-full rounded px-2 py-1 text-left text-sm text-destructive hover:bg-accent"
          onClick={() => {
            onDeleteConnection()
            onClose()
          }}
        >
          Delete connection
        </button>
      </div>
    </div>
  )
}
