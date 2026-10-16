"use client"

import * as React from "react"
import { useState } from "react"
import { CalendarIcon } from "lucide-react"
import { cn } from "cn"
import { type DateRange } from "react-day-picker"
import { Calendar } from "@/components/ui/calendar"
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover"
import { Button } from "@/components/ui/button"

interface DateRangePickerProps {
  from: string | null
  to: string | null
  onChange: (from: string | null, to: string | null) => void
  disabled?: boolean
}

function formatDay(date: Date): string {
  return date.toLocaleDateString(undefined, {
    year: "numeric",
    month: "short",
    day: "numeric",
  })
}

/**
 * Shadcn range picker (Popover + Calendar, mode="range") bound to ISO UTC
 * strings — the same contract the API and the URL params use. The calendar
 * edits a draft opened from the current props; Apply commits it as ISO UTC
 * (start of from-day, end of to-day) and Clear empties both ends. No
 * prop-to-state sync effect: the draft is snapshotted on open, so an external
 * URL change while closed is picked up on the next open.
 */
export function DateRangePicker({
  from,
  to,
  onChange,
  disabled = false,
}: DateRangePickerProps) {
  const [open, setOpen] = useState(false)
  const [draft, setDraft] = useState<DateRange | undefined>(undefined)

  const committed: DateRange | undefined =
    from || to
      ? { from: from ? new Date(from) : undefined, to: to ? new Date(to) : undefined }
      : undefined

  const shown = open ? (draft ?? committed) : committed

  const openPicker = (next: boolean) => {
    if (next) setDraft(committed)
    setOpen(next)
  }

  const applyRange = (next: DateRange | undefined) => {
    setDraft(next)
    onChange(
      next?.from ? startOfDayUtc(next.from).toISOString() : null,
      next?.to ? endOfDayUtc(next.to).toISOString() : null,
    )
  }

  return (
    <div className="flex items-center gap-1.5">
      <Popover open={open} onOpenChange={openPicker}>
        <PopoverTrigger
          render={
            <Button
              variant="outline"
              size="sm"
              className={cn(
                "w-[250px] justify-start text-left font-normal",
                !shown?.from && !shown?.to && "text-muted-foreground",
              )}
              disabled={disabled}
            >
              <CalendarIcon className="mr-2 h-4 w-4" />
              {shown?.from || shown?.to ? (
                <>
                  {shown.from ? formatDay(shown.from) : "From"}
                  <span className="mx-1 text-muted-foreground">–</span>
                  {shown.to ? formatDay(shown.to) : "To"}
                </>
              ) : (
                <span>Pick a range</span>
              )}
            </Button>
          }
        />
        <PopoverContent className="w-auto p-0" side="bottom" align="start">
          <Calendar
            mode="range"
            selected={shown}
            onSelect={applyRange}
            numberOfMonths={2}
          />
          <div className="flex items-center justify-end gap-2 border-t p-2">
            <Button
              variant="ghost"
              size="sm"
              onClick={() => {
                applyRange(undefined)
              }}
            >
              Clear
            </Button>
            <Button size="sm" onClick={() => setOpen(false)}>
              Apply
            </Button>
          </div>
        </PopoverContent>
      </Popover>
    </div>
  )
}

function startOfDayUtc(date: Date): Date {
  return new Date(Date.UTC(date.getFullYear(), date.getMonth(), date.getDate(), 0, 0, 0))
}

function endOfDayUtc(date: Date): Date {
  return new Date(
    Date.UTC(date.getFullYear(), date.getMonth(), date.getDate(), 23, 59, 59, 999),
  )
}
