import { copyText } from "@/lib/clipboard"
import { DbNotification } from "@/app/dashboard/databases/types"
import { shortValue } from "@/app/dashboard/databases/utils/connection-helpers"

/** Where the cell context menu was opened. */
export type CellContextMenuState = {
  x: number
  y: number
  row: number
  col: number
}

export interface CellContextMenuProps {
  menu: CellContextMenuState
  /** The name of the column the cell sits in. */
  column: string
  /** The value the cell shows. */
  value: unknown
  /** True when the column holds a date or a time. */
  isDateTime: boolean
  /** True when the column is a primary key and cannot be changed. */
  isPrimaryKey: boolean
  mutating: boolean
  selectedIsReadOnly: boolean
  /** True when the data on screen came from the cache, so edits are off. */
  fromCache: boolean
  notifyDb: (type: DbNotification["type"], title: string, text?: string) => void
  onCopyValue: () => void
  onSetNow: () => void
  onSetNull: () => void
  onSetEmpty: () => void
  onClose: () => void
}

/** The menu that opens when a cell in the data grid is right-clicked. */
export const CellContextMenu = ({
  menu,
  column,
  value,
  isDateTime,
  isPrimaryKey,
  mutating,
  selectedIsReadOnly,
  fromCache,
  notifyDb,
  onCopyValue,
  onSetNow,
  onSetNull,
  onSetEmpty,
  onClose,
}: CellContextMenuProps) => {
  const disabled = mutating || selectedIsReadOnly || fromCache || isPrimaryKey
  const disabledTitle = selectedIsReadOnly ? "read-only connection — changes are disabled" : undefined

  return (
    <div
      className="fixed inset-0 z-50"
      onClick={onClose}
      onContextMenu={(e) => {
        e.preventDefault()
        onClose()
      }}
    >
      <div
        className="absolute min-w-[160px] rounded-md border border-border bg-popover p-1 shadow-md"
        style={{ left: menu.x, top: menu.y }}
        onClick={(e) => e.stopPropagation()}
      >
        <p className="px-2 py-1 text-xs text-muted-foreground">{column}</p>
        <button
          className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-accent"
          title="Copy this cell's value to the clipboard"
          onClick={() => {
            // Copy what the cell shows, so what lands on the clipboard is
            // what the reader saw - a null cell copies as "null".
            const text = value === null || value === undefined ? "null" : String(value)
            void copyText(text).then((ok) => {
              if (ok) {
                notifyDb("success", "Copied", `${column}: ${shortValue(text)}`)
              } else {
                notifyDb("error", "Copy failed", "The browser blocked clipboard access.")
              }
            })
            onCopyValue()
            onClose()
          }}
        >
          Copy value
        </button>
        <div className="my-1 h-px bg-border" />
        {isDateTime ? (
          <button
            className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-accent disabled:opacity-50 disabled:hover:bg-transparent"
            disabled={disabled}
            title={disabledTitle}
            onClick={() => {
              onSetNow()
              onClose()
            }}
          >
            Set now
          </button>
        ) : null}
        <button
          className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-accent disabled:opacity-50 disabled:hover:bg-transparent"
          disabled={disabled}
          title={disabledTitle}
          onClick={() => {
            onSetNull()
            onClose()
          }}
        >
          Set null
        </button>
        <button
          className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-accent disabled:opacity-50 disabled:hover:bg-transparent"
          disabled={disabled}
          title={disabledTitle}
          onClick={() => {
            onSetEmpty()
            onClose()
          }}
        >
          Set empty string
        </button>
      </div>
    </div>
  )
}
