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

export interface DeleteRowConfirmDialogProps {
  open: boolean
  /** The table the row belongs to. */
  tableName: string
  mutating: boolean
  onConfirm: () => void
  onCancel: () => void
}

/** The confirmation a row is deleted after. */
export const DeleteRowConfirmDialog = ({
  open,
  tableName,
  mutating,
  onConfirm,
  onCancel,
}: DeleteRowConfirmDialogProps) => {
  return (
    <AlertDialog open={open} onOpenChange={(o) => { if (!o) onCancel() }}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle className="text-destructive">Delete row?</AlertDialogTitle>
          <AlertDialogDescription>
            This will permanently delete the selected row from{" "}
            <span className="font-medium">{tableName}</span>. This cannot be undone.
          </AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel disabled={mutating}>Cancel</AlertDialogCancel>
          <AlertDialogAction
            onClick={(e) => {
              e.preventDefault()
              onConfirm()
            }}
            disabled={mutating}
            className="bg-destructive text-destructive-foreground hover:bg-destructive/90"
          >
            {mutating ? "Deleting..." : "Delete"}
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  )
}
