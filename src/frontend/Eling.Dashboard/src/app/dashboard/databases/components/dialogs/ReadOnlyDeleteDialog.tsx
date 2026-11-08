import {
  AlertDialog,
  AlertDialogContent,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogAction,
} from "@/components/ui/alert-dialog"

export interface ReadOnlyDeleteDialogProps {
  open: boolean
  onClose: () => void
}

/**
 * The notice a reader gets when they try to delete a row on a read-only
 * connection: the row cannot go anywhere, so the click explains why instead
 * of silently doing nothing.
 */
export const ReadOnlyDeleteDialog = ({ open, onClose }: ReadOnlyDeleteDialogProps) => {
  return (
    <AlertDialog open={open} onOpenChange={(o) => { if (!o) onClose() }}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>Read-only connection</AlertDialogTitle>
          <AlertDialogDescription>This connection is read-only, so rows cannot be deleted.</AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogAction onClick={onClose}>OK</AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  )
}
