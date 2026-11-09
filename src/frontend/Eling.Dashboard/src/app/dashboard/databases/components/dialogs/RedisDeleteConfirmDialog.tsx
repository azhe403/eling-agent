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

export interface RedisDeleteConfirmDialogProps {
  open: boolean
  keyName: string
  mutating: boolean
  onConfirm: () => void
  onCancel: () => void
}

/** The confirmation a single Redis key is deleted after. */
export const RedisDeleteConfirmDialog = ({
  open,
  keyName,
  mutating,
  onConfirm,
  onCancel,
}: RedisDeleteConfirmDialogProps) => {
  return (
    <AlertDialog open={open} onOpenChange={(o) => { if (!o) onCancel() }}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle className="text-destructive">Delete key?</AlertDialogTitle>
          <AlertDialogDescription>
            This will permanently delete the key <span className="font-mono">{keyName}</span>.
            This cannot be undone.
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
