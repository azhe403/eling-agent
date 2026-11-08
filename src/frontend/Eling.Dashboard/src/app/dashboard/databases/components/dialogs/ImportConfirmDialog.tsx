import {
  AlertDialog,
  AlertDialogContent,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogAction,
  AlertDialogCancel,
} from "@/components/ui/alert-dialog"
import { Detected } from "@/app/dashboard/databases/types"

export interface ImportConfirmDialogProps {
  open: boolean
  /** The detected connections that were picked for import. */
  connections: Detected[]
  onConfirm: () => void
  onCancel: () => void
}

/** The confirmation a set of detected connections is imported after. */
export const ImportConfirmDialog = ({ open, connections, onConfirm, onCancel }: ImportConfirmDialogProps) => {
  return (
    <AlertDialog open={open} onOpenChange={(o) => { if (!o) onCancel() }}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>
            Import {connections.length} connection{connections.length === 1 ? "" : "s"}?
          </AlertDialogTitle>
          <AlertDialogDescription>The following keys will be registered as read-only connections:</AlertDialogDescription>
        </AlertDialogHeader>
        <div className="max-h-60 space-y-1 overflow-y-auto">
          {connections.map((d) => (
            <div key={d.settingsKey} className="rounded border bg-muted/40 px-2 py-1 text-sm">
              <div className="font-medium">{d.settingsKey}</div>
              {d.sourceFile ? (
                <div className="text-xs text-muted-foreground">
                  {d.sourceFile} → {d.name}
                </div>
              ) : (
                <div className="text-xs text-muted-foreground">→ {d.name}</div>
              )}
            </div>
          ))}
        </div>
        <AlertDialogFooter>
          <AlertDialogCancel onClick={onCancel}>Cancel</AlertDialogCancel>
          <AlertDialogAction onClick={onConfirm}>Import</AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  )
}
