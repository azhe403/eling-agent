import { Input } from "@/components/ui/input"
import { Button } from "@/components/ui/button"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { ConnMode, Provider } from "@/app/dashboard/databases/types"
import { ConnectionFormFields } from "./ConnectionFormFields"

/** The result of a connection test. */
export type TestResult = { ok: boolean; message: string; latencyMs: number }

export interface ConnectionFormProps {
  provider: Provider
  mode: ConnMode
  host: string
  port: string
  database: string
  username: string
  password: string
  filePath: string
  connStr: string
  /** The name shown in the title bar; the edit dialog names the connection. */
  title: string
  /** The environment the connection is registered in. */
  environment: string
  /** True when the provider cannot be changed (an existing connection). */
  providerLocked?: boolean
  /** True when the password field keeps the stored password when left blank. */
  passwordKeepsStored?: boolean
  portInvalid: boolean
  /** True when the form holds enough to be submitted. */
  ready: boolean
  testing: boolean
  testResult: TestResult | null
  /** True when the form shows the agent-mutation checkboxes. */
  showMutationOptions?: boolean
  allowAgentMutations?: boolean
  requireApprovalForMutations?: boolean
  /** The name the form shows; the create dialog fills it from the fields. */
  name: string
  onNameChange: (value: string) => void
  onEnvironmentChange: (value: string) => void
  onProviderChange: (value: string | null) => void
  onModeChange: (mode: ConnMode) => void
  onHostChange: (value: string) => void
  onPortChange: (value: string) => void
  onDatabaseChange: (value: string) => void
  onUsernameChange: (value: string) => void
  onPasswordChange: (value: string) => void
  onFilePathChange: (value: string) => void
  onConnStrChange: (value: string) => void
  onAllowAgentMutationsChange: (value: boolean) => void
  onRequireApprovalChange: (value: boolean) => void
  onBrowseFile: () => void
  onTest: () => void
  onSubmit: () => void
  onCancel: () => void
  loadDatabases: () => Promise<string[]>
  serverKey: string
}

export const ConnectionForm = ({
  provider,
  mode,
  host,
  port,
  database,
  username,
  password,
  filePath,
  connStr,
  title,
  environment,
  providerLocked,
  passwordKeepsStored,
  portInvalid,
  ready,
  testing,
  testResult,
  showMutationOptions,
  allowAgentMutations,
  requireApprovalForMutations,
  name,
  onNameChange,
  onEnvironmentChange,
  onProviderChange,
  onModeChange,
  onHostChange,
  onPortChange,
  onDatabaseChange,
  onUsernameChange,
  onPasswordChange,
  onFilePathChange,
  onConnStrChange,
  onAllowAgentMutationsChange,
  onRequireApprovalChange,
  onBrowseFile,
  onTest,
  onSubmit,
  onCancel,
  loadDatabases,
  serverKey,
}: ConnectionFormProps) => {
  return (
    <>
      <div className="mb-3 flex shrink-0 items-center justify-between">
        <p className="font-medium">{title}</p>
        <Button variant="ghost" size="sm" onClick={onCancel}>
          Close
        </Button>
      </div>

      <div className="min-h-0 flex-1 space-y-3 overflow-y-auto pr-1 pt-0.5">
        <Input placeholder="name" value={name} onChange={(e) => onNameChange(e.target.value)} />

        <ConnectionFormFields
          provider={provider}
          providerLocked={providerLocked}
          mode={mode}
          host={host}
          port={port}
          database={database}
          username={username}
          password={password}
          filePath={filePath}
          connStr={connStr}
          passwordKeepsStored={passwordKeepsStored}
          portInvalid={portInvalid}
          loadDatabases={loadDatabases}
          serverKey={serverKey}
          onProviderChange={onProviderChange}
          onModeChange={onModeChange}
          onHostChange={onHostChange}
          onPortChange={onPortChange}
          onDatabaseChange={onDatabaseChange}
          onUsernameChange={onUsernameChange}
          onPasswordChange={onPasswordChange}
          onFilePathChange={onFilePathChange}
          onConnStrChange={onConnStrChange}
          onBrowseFile={onBrowseFile}
        />

        <Select value={environment} onValueChange={(val) => { if (val) onEnvironmentChange(val) }}>
          <SelectTrigger className="w-full">
            <SelectValue placeholder="Environment" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="Development">Development</SelectItem>
            <SelectItem value="Staging">Staging</SelectItem>
            <SelectItem value="Production">Production</SelectItem>
            <SelectItem value="ReadOnly">ReadOnly</SelectItem>
          </SelectContent>
        </Select>

        {showMutationOptions ? (
          <div className="flex flex-wrap gap-x-6 gap-y-1">
            <label className="flex items-center gap-2 text-sm">
              <input
                type="checkbox"
                checked={allowAgentMutations}
                onChange={(e) => onAllowAgentMutationsChange(e.target.checked)}
              />
              Allow agent mutations
            </label>
            <label className="flex items-center gap-2 text-sm">
              <input
                type="checkbox"
                checked={requireApprovalForMutations}
                onChange={(e) => onRequireApprovalChange(e.target.checked)}
              />
              Require approval for mutations
            </label>
          </div>
        ) : null}

        {testResult ? (
          <p className={`text-xs ${testResult.ok ? "text-green-600" : "text-destructive"}`}>
            {testResult.ok ? "OK" : "FAIL"} {testResult.message} ({testResult.latencyMs}ms)
          </p>
        ) : null}
      </div>

      <div className="mt-3 flex shrink-0 gap-2">
        <Button onClick={onSubmit} disabled={!ready}>
          Save
        </Button>
        <Button variant="outline" onClick={onTest} disabled={testing || !ready}>
          {testing ? "Testing…" : "Test"}
        </Button>
        <Button variant="outline" onClick={onCancel}>
          Cancel
        </Button>
      </div>
    </>
  )
}
