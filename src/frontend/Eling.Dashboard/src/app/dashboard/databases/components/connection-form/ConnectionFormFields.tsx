import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Input } from "@/components/ui/input"
import { Button } from "@/components/ui/button"
import { DatabaseCombobox } from "@/app/dashboard/databases/DatabaseCombobox"
import { ConnMode, Provider } from "@/app/dashboard/databases/types"

/**
 * The fields of a connection form, shared by the create and the edit dialog.
 *
 * The provider decides which fields apply: a SQLite connection names a file,
 * every other provider names a host and a port. The raw mode takes a
 * connection string instead of the individual fields.
 */
export interface ConnectionFormFieldsProps {
  /** The provider the form points at. The edit dialog cannot change it. */
  provider: Provider
  /** True when the provider cannot be changed (an existing connection). */
  providerLocked?: boolean
  mode: ConnMode
  host: string
  port: string
  database: string
  username: string
  password: string
  filePath: string
  connStr: string
  /** True when the password field keeps the stored password when left blank. */
  passwordKeepsStored?: boolean
  portInvalid: boolean
  /** The databases the server offers, loaded from the credentials typed. */
  loadDatabases: () => Promise<string[]>
  /** The key that names the server the credentials reach; a change reloads the list. */
  serverKey: string
  onProviderChange: (value: string | null) => void
  onModeChange: (mode: ConnMode) => void
  onHostChange: (value: string) => void
  onPortChange: (value: string) => void
  onDatabaseChange: (value: string) => void
  onUsernameChange: (value: string) => void
  onPasswordChange: (value: string) => void
  onFilePathChange: (value: string) => void
  onConnStrChange: (value: string) => void
  onBrowseFile: () => void
}

export const ConnectionFormFields = ({
  provider,
  providerLocked,
  mode,
  host,
  port,
  database,
  username,
  password,
  filePath,
  connStr,
  passwordKeepsStored,
  portInvalid,
  loadDatabases,
  serverKey,
  onProviderChange,
  onModeChange,
  onHostChange,
  onPortChange,
  onDatabaseChange,
  onUsernameChange,
  onPasswordChange,
  onFilePathChange,
  onConnStrChange,
  onBrowseFile,
}: ConnectionFormFieldsProps) => {
  const providerSelect = (
    <Select value={provider} onValueChange={onProviderChange} disabled={providerLocked}>
      <SelectTrigger className="w-40">
        <SelectValue placeholder="Provider" />
      </SelectTrigger>
      <SelectContent>
        <SelectItem value="Sqlite">Sqlite</SelectItem>
        <SelectItem value="MySql">MySql</SelectItem>
        <SelectItem value="Redis">Redis</SelectItem>
      </SelectContent>
    </Select>
  )

  const modeToggle = (
    <div className="flex rounded-lg border p-0.5 w-fit">
      <Button size="sm" variant={mode === "manual" ? "secondary" : "ghost"} onClick={() => onModeChange("manual")}>
        Manual
      </Button>
      <Button size="sm" variant={mode === "raw" ? "secondary" : "ghost"} onClick={() => onModeChange("raw")}>
        Raw string
      </Button>
    </div>
  )

  if (mode === "raw") {
    return (
      <div className="space-y-3">
        {providerLocked ? null : (
          <div className="flex flex-wrap items-center gap-2">
            {providerSelect}
            {modeToggle}
          </div>
        )}
        <div className="flex gap-2">
          <Input
            placeholder={provider === "Sqlite" ? "Data Source path or connection string" : "connection string"}
            value={connStr}
            onChange={(e) => onConnStrChange(e.target.value)}
            className="flex-1"
          />
          <Button type="button" variant="outline" onClick={onBrowseFile}>
            Browse
          </Button>
        </div>
      </div>
    )
  }

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-2">
        {providerLocked ? <Input value={provider} disabled className="w-32" /> : providerSelect}
        {modeToggle}
      </div>

      {provider === "Sqlite" ? (
        <div className="flex gap-2">
          <Input
            placeholder="database file"
            value={filePath}
            onChange={(e) => onFilePathChange(e.target.value)}
            className="flex-1"
          />
          <Button type="button" variant="outline" onClick={onBrowseFile}>
            Browse
          </Button>
        </div>
      ) : (
        <div className="grid gap-2 sm:grid-cols-2">
          <Input placeholder="host" value={host} onChange={(e) => onHostChange(e.target.value)} />
          <Input
            placeholder="port (3306)"
            value={port}
            onChange={(e) => onPortChange(e.target.value)}
            aria-invalid={portInvalid}
          />
          <Input placeholder="username" value={username} onChange={(e) => onUsernameChange(e.target.value)} />
          <Input
            type="password"
            placeholder={passwordKeepsStored ? "password (leave empty to keep the stored one)" : "password"}
            value={password}
            onChange={(e) => onPasswordChange(e.target.value)}
          />
          {/* Last, because the list it offers comes from the server these
              credentials reach. */}
          <DatabaseCombobox
            value={database}
            onChange={onDatabaseChange}
            loadDatabases={loadDatabases}
            serverKey={serverKey}
            placeholder="database (optional - pick or type)"
            className="sm:col-span-2"
          />
        </div>
      )}

      {portInvalid ? <p className="text-xs text-destructive">Port must be a number between 1 and 65535.</p> : null}
    </div>
  )
}
