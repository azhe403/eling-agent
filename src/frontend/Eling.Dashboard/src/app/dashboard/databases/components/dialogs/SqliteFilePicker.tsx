import { Button } from "@/components/ui/button"

export interface SqliteFilePickerProps {
  open: boolean
  /** The directory the picker is showing; null means the drives list. */
  path: string | null
  /** The directory above the one shown; null at the top. */
  parent: string | null
  entries: { path: string; isDirectory: boolean }[]
  loading: boolean
  /** Opens a directory, or the drives list when the path is null. */
  onBrowse: (path: string | null) => void
  /** A file was picked. */
  onSelect: (path: string) => void
  onClose: () => void
}

/**
 * The file browser a SQLite connection points at. It walks the host's
 * directories so a person can pick the database file without typing a path.
 */
export const SqliteFilePicker = ({
  open,
  path,
  parent,
  entries,
  loading,
  onBrowse,
  onSelect,
  onClose,
}: SqliteFilePickerProps) => {
  if (!open) return null
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 px-4">
      <div className="w-full max-w-2xl rounded-lg border border-border bg-background p-4 shadow-lg">
        <div className="mb-3 flex items-center justify-between">
          <p className="font-medium">Select SQLite database file</p>
          <Button variant="ghost" size="sm" onClick={onClose}>
            Close
          </Button>
        </div>
        <p className="mb-2 truncate text-xs text-muted-foreground">{path ?? "Drives"}</p>
        <div className="mb-2 flex gap-2">
          <Button variant="outline" size="sm" onClick={() => onBrowse(null)}>
            Drives
          </Button>
          {parent ? (
            <Button variant="outline" size="sm" onClick={() => onBrowse(parent)}>
              .. Up
            </Button>
          ) : null}
        </div>
        {loading ? (
          <p className="text-sm text-muted-foreground">Loading…</p>
        ) : (
          <div className="h-[480px] overflow-y-auto rounded border">
            {entries.map((e) => (
              <button
                key={e.path}
                className="flex w-full items-center gap-2 px-3 py-2 text-left text-sm hover:bg-accent"
                onClick={() => {
                  if (e.isDirectory) {
                    onBrowse(e.path)
                  } else {
                    onSelect(e.path)
                  }
                }}
              >
                <span>{e.isDirectory ? "📁" : "📄"}</span>
                <span className="truncate">{e.path.split(/[\\/]/).pop()}</span>
              </button>
            ))}
            {entries.length === 0 ? <p className="px-3 py-2 text-sm text-muted-foreground">Empty.</p> : null}
          </div>
        )}
      </div>
    </div>
  )
}
