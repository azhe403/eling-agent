"use client"

import * as React from "react"
import { format } from "date-fns"
import { CalendarIcon } from "lucide-react"

import { cn } from "@/lib/utils"
import { Button } from "@/components/ui/button"
import { Calendar } from "@/components/ui/calendar"
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover"

export function DatePicker({
  value,
  onChange,
  className,
  withTime = false,
}: {
  value: string
  onChange: (v: string) => void
  className?: string
  withTime?: boolean
}) {
  const date = value ? new Date(value) : undefined
  const valid = date && !isNaN(date.getTime())
  const timePart = valid && withTime ? format(date!, "HH:mm:ss") : ""

  const combine = (d: Date, t: string) => {
    const [hh, mm, ss] = (t || "00:00:00").split(":")
    const out = new Date(d)
    out.setHours(Number(hh) || 0, Number(mm) || 0, Number(ss) || 0, 0)
    const pad = (n: number) => String(n).padStart(2, "0")
    return `${out.getFullYear()}-${pad(out.getMonth() + 1)}-${pad(out.getDate())}T${pad(out.getHours())}:${pad(out.getMinutes())}:${pad(out.getSeconds())}`
  }

  const setNow = () => {
    const now = new Date()
    if (withTime) {
      const pad = (n: number) => String(n).padStart(2, "0")
      onChange(`${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}T${pad(now.getHours())}:${pad(now.getMinutes())}`)
    } else {
      onChange(format(now, "yyyy-MM-dd"))
    }
  }

  return (
    <div className={cn("flex items-center gap-2", className)}>
      <Popover>
        <PopoverTrigger
          render={
            <Button
              variant="outline"
              className={cn("justify-start text-left font-normal", !valid && "text-muted-foreground")}
            />
          }
        >
          <CalendarIcon className="mr-2 size-4" />
          {valid ? (withTime ? format(date!, "yyyy-MM-dd HH:mm:ss") : format(date!, "yyyy-MM-dd")) : "Pick date"}
        </PopoverTrigger>
        <PopoverContent className="w-auto p-0" align="start">
          <Calendar
            mode="single"
            selected={valid ? date : undefined}
            onSelect={(d) => {
              if (d) {
                if (withTime) onChange(combine(d, timePart || "00:00"))
                else onChange(format(d, "yyyy-MM-dd"))
              }
            }}
          />
          {withTime ? (
            <div className="p-3 border-t border-border flex items-center gap-2">
              <span className="text-sm text-muted-foreground">Time</span>
              <input
                type="time"
                step="1"
                className="rounded border px-2 py-1 text-sm"
                value={timePart}
                onChange={(e) => {
                  if (valid && date) onChange(combine(date, e.target.value))
                }}
              />
            </div>
          ) : null}
        </PopoverContent>
      </Popover>
      <Button type="button" variant="ghost" size="sm" className="px-2" onClick={setNow} title="Set to now">
        Now
      </Button>
    </div>
  )
}