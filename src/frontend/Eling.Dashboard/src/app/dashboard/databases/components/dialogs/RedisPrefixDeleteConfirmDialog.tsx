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

export interface RedisPrefixDeleteConfirmDialogProps {
  open: boolean
  prefix: string
  total: number
  keys: string[]
  truncated: boolean
  loading: boolean
  mutating: boolean
  onConfirm: () => void
  onCancel: () => void
}

/** The confirmation a Redis subtree (prefix) is deleted after. */
export const RedisPrefixDeleteConfirmDialog = ({
  open,
  prefix,
  total,
  keys,
  truncated,
  loading,
  mutating,
  onConfirm,
  onCancel,
}: RedisPrefixDeleteConfirmDialogProps) => {
  return (
    <AlertDialog open={open} onOpenChange={(o) => { if (!o) onCancel() }}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle className="text-destructive">Delete prefix?</AlertDialogTitle>
          <AlertDialogDescription>
            {total} keys under <span className="font-mono">{prefix}</span> will be
            permanently deleted. This cannot be undone.
          </AlertDialogDescription>
        </AlertDialogHeader>
        {loading ? (
          <p className="text-sm text-muted-foreground">Loading affected keys…</p>
        ) : (
          <div className="max-h-60 space-y-1 overflow-y-auto">
            {keys.map((k) => (
              <div
                key={k}
                className="rounded border bg-muted/40 px-2 py-1 text-sm font-mono break-all"
              >
                {k}
              </div>
            ))}
          </div>
        )}
        {truncated && !loading && (
          <p className="text-sm text-amber-600 dark:text-amber-400">
            Showing 100 of {total} — more keys will also be deleted.
          </p>
        )}
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
