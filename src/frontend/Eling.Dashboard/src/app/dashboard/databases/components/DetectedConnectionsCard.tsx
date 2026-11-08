import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Detected } from "@/app/dashboard/databases/types"

export interface DetectedConnectionsCardProps {
  detected: Detected[]
  selectedKeys: string[]
  onToggleKey: (settingsKey: string, selected: boolean) => void
  onImport: () => void
}

/**
 * The connections found in the workspace's appsettings files, offered for
 * import.
 *
 * The passwords are masked: the card shows which key was found and where, so
 * the reader can pick the right one without seeing a secret.
 */
export const DetectedConnectionsCard = ({
  detected,
  selectedKeys,
  onToggleKey,
  onImport,
}: DetectedConnectionsCardProps) => {
  if (detected.length === 0) return null
  return (
    <Card>
      <CardHeader>
        <CardTitle>Detected connections</CardTitle>
        <CardDescription>
          Passwords masked. Select keys to import (read-only). Every appsettings*.json file in the
          workspace contributes its own keys.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-2">
        {detected.map((d) => (
          <label key={d.settingsKey} className="flex items-center gap-2 text-sm">
            <input
              type="checkbox"
              checked={selectedKeys.includes(d.settingsKey)}
              onChange={(e) => onToggleKey(d.settingsKey, e.target.checked)}
              disabled={d.provider === null}
            />
            <span className="font-medium">{d.settingsKey}</span>
            {d.sourceFile ? <span className="text-xs text-muted-foreground">{d.sourceFile}</span> : null}
            <span className="text-muted-foreground">→ {d.name}</span>
            <span className="text-xs text-muted-foreground">{d.maskedConnectionString}</span>
            {d.rejectionReason ? <Badge variant="destructive">{d.rejectionReason}</Badge> : null}
          </label>
        ))}
        <Button onClick={onImport} disabled={selectedKeys.length === 0}>
          Import selected
        </Button>
      </CardContent>
    </Card>
  )
}
