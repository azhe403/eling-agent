/** Where the tab context menu was opened. */
export type TabContextMenuState = {
  x: number
  y: number
  idx: number
}

export interface TabContextMenuProps {
  menu: TabContextMenuState
  onCloseTab: () => void
  onCloseOtherTabs: () => void
  onCloseAllTabs: () => void
  onClose: () => void
}

/** The menu that opens when a tab in the tab strip is right-clicked. */
export const TabContextMenu = ({
  menu,
  onCloseTab,
  onCloseOtherTabs,
  onCloseAllTabs,
  onClose,
}: TabContextMenuProps) => {
  return (
    <div className="fixed inset-0 z-50" onClick={onClose}>
      <div
        className="absolute min-w-[160px] rounded-md border border-border bg-popover p-1 shadow-md"
        style={{ left: menu.x, top: menu.y }}
        onClick={(e) => e.stopPropagation()}
      >
        <button
          className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-accent"
          onClick={() => {
            onCloseTab()
            onClose()
          }}
        >
          Close
        </button>
        <button
          className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-accent"
          onClick={() => {
            onCloseOtherTabs()
            onClose()
          }}
        >
          Close others
        </button>
        <button
          className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-accent"
          onClick={() => {
            onCloseAllTabs()
            onClose()
          }}
        >
          Close all
        </button>
      </div>
    </div>
  )
}
