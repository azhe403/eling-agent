"use client"

import { useEffect } from "react"

import { Button } from "@/components/ui/button"

export default function MemoriesError({
  error,
  reset,
}: {
  error: Error & { digest?: string }
  reset: () => void
}) {
  useEffect(() => {
    console.error("[memories page error]", error.digest, error)
  }, [error])

  return (
    <div className="flex flex-1 flex-col items-center justify-center gap-4 p-8 text-center">
      <h2 className="text-lg font-semibold">Could not load memories</h2>
      <p className="max-w-md text-sm text-muted-foreground">
        {error.message ||
          "An unexpected error occurred while loading the memories list."}
      </p>
      <Button onClick={reset}>Try again</Button>
    </div>
  )
}