import { Button } from "@/components/ui/button"
import { DbConn } from "@/app/dashboard/databases/types"

export interface DeleteConnectionDialogProps {
  conn: DbConn | null
  onConfirm: () => void
  onCancel: () => void
}

/** The confirmation a connection is removed after. */
export const DeleteConnectionDialog = ({ conn, onConfirm, onCancel }: DeleteConnectionDialogProps) => {
  if (!conn) return null
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40">
      <div className="w-full max-w-sm rounded-lg border border-border bg-background p-4 shadow-lg">
        <p className="mb-3 font-medium">Delete connection?</p>
        <p className="mb-4 text-sm text-muted-foreground">
          {conn.name} will be removed. This cannot be undone.
        </p>
        <div className="flex justify-end gap-2">
          <Button variant="outline" onClick={onCancel}>
            Cancel
          </Button>
          <Button variant="destructive" onClick={onConfirm}>
            Delete
          </Button>
        </div>
      </div>
    </div>
  )
}
