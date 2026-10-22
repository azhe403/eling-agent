"use client"

import { useRef, useState } from "react"
import { ChevronDown, Loader2 } from "lucide-react"

import { Input } from "@/components/ui/input"
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover"
import { cn } from "cn"

/**
 * A database field that offers the databases found on the server as a
 * dropdown, while still accepting any name typed by hand.
 *
 * The two are not alternatives: the list is what the account can see, and a
 * name typed by hand may be one the account cannot enumerate but can still
 * use. So the input is always editable and the list is a shortcut, never a
 * constraint - an empty field stays valid and means "server-level".
 */
export function DatabaseCombobox({
  value,
  onChange,
  loadDatabases,
  placeholder,
  disabled,
  id,
  className,
}: {
  value: string
  onChange: (value: string) => void
  /** Reads the databases on the server. */
  loadDatabases: () => Promise<string[]>
  placeholder?: string
  disabled?: boolean
  id?: string
  className?: string
}) {
  const [open, setOpen] = useState(false)
  const [databases, setDatabases] = useState<string[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  // The list belongs to the connection it was read from; a change of host
  // makes it stale.
  const loadedRef = useRef(false)

  const fetchDatabases = async () => {
    setLoading(true)
    setError(null)
    try {
      setDatabases(await loadDatabases())
      loadedRef.current = true
    } catch {
      setError("Could not read the databases on this server.")
      setDatabases([])
    } finally {
      setLoading(false)
    }
  }

  const handleOpenChange = (next: boolean) => {
    setOpen(next)
    // Opening the list for the first time reads the server; after that the
    // list is reused.
    if (next && !loadedRef.current && !loading) {
      void fetchDatabases()
    }
  }

  return (
    <div className={cn("flex gap-2", className)}>
      <Input
        id={id}
        placeholder={placeholder}
        value={value}
        disabled={disabled}
        onChange={(e) => onChange(e.target.value)}
        className="flex-1"
      />
      <Popover open={open} onOpenChange={handleOpenChange}>
        <PopoverTrigger
          className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border border-input bg-transparent text-sm font-medium transition-colors outline-none hover:bg-muted disabled:pointer-events-none disabled:opacity-50 focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50"
          disabled={disabled}
          title="Show the databases on this server"
          aria-label="Show the databases on this server"
        >
          {loading ? <Loader2 className="size-4 animate-spin" /> : <ChevronDown className="size-4" />}
        </PopoverTrigger>
        <PopoverContent align="start" className="w-72 max-h-72 overflow-y-auto p-1">
          {error ? (
            <p className="px-2 py-1.5 text-xs text-destructive">{error}</p>
          ) : null}
          {!error && databases.length === 0 && !loading ? (
            <p className="px-2 py-1.5 text-xs text-muted-foreground">
              No databases found. Type a name to use one anyway.
            </p>
          ) : null}
          {databases.map((db) => (
            <button
              key={db}
              type="button"
              className="flex w-full items-center rounded px-2 py-1.5 text-left text-sm hover:bg-accent"
              onClick={() => {
                onChange(db)
                setOpen(false)
              }}
            >
              <span className="truncate">{db}</span>
            </button>
          ))}
        </PopoverContent>
      </Popover>
    </div>
  )
}
