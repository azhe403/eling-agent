"use client"

import { useCallback, useState } from "react"
import { ConnMode, DbConn, Provider } from "../types"
import { deriveConnectionName, isValidPort, manualPayload } from "../utils/connection-helpers"

/** The fields of the create form, before they are sent. */
export type CreateFormState = {
  name: string
  /** True when the reader typed a name of their own. */
  nameEdited: boolean
  provider: Provider
  mode: ConnMode
  connStr: string
  host: string
  port: string
  database: string
  username: string
  password: string
  filePath: string
  environment: string
}

const emptyCreate = (): CreateFormState => ({
  name: "",
  nameEdited: false,
  provider: "Sqlite",
  mode: "manual",
  connStr: "",
  host: "",
  port: "",
  database: "",
  username: "",
  password: "",
  filePath: "",
  environment: "ReadOnly",
})

/** The fields of the edit form, before they are sent. */
export type EditFormState = {
  /** The id of the connection being edited. */
  id: string
  name: string
  provider: Provider
  mode: ConnMode
  connStr: string
  host: string
  port: string
  database: string
  username: string
  password: string
  filePath: string
  environment: string
  allowAgentMutations: boolean
  requireApprovalForMutations: boolean
}

/**
 * The create and edit forms of a connection.
 *
 * They are one hook because they are one form in two states: the same fields,
 * the same rules, and the provider decides which of them apply. The create
 * form derives its name from the fields until the reader types one; the edit
 * form starts from what the connection already holds.
 */
export function useConnectionForm() {
  const [create, setCreate] = useState<CreateFormState>(emptyCreate)
  const [edit, setEdit] = useState<EditFormState | null>(null)

  /** The name the create form shows: the typed one, or one derived from the fields. */
  const createEffectiveName = create.nameEdited
    ? create.name
    : deriveConnectionName(create.provider, create.host, create.port, create.database, create.filePath)

  const createPortInvalid = !isValidPort(create.port)
  const createReady =
    create.mode === "raw"
      ? create.connStr.trim() !== "" && createEffectiveName.trim() !== ""
      : create.provider === "Sqlite"
        ? create.filePath.trim() !== "" && createEffectiveName.trim() !== ""
        : create.host.trim() !== "" && createEffectiveName.trim() !== ""

  const editPortInvalid = edit ? !isValidPort(edit.port) : false
  const editReady = edit
    ? edit.mode === "raw"
      ? true
      : edit.provider === "Sqlite"
        ? edit.filePath.trim() !== ""
        : edit.host.trim() !== ""
    : false

  /** The body the create request sends. */
  const createPayload = useCallback(() => {
    const body: Record<string, unknown> = {
      name: createEffectiveName.trim(),
      provider: create.provider,
      environment: create.environment,
      allowAgentMutations: false,
      requireApprovalForMutations: true,
    }
    if (create.mode === "raw") {
      body.connectionString = create.connStr
    } else {
      body.manual = manualPayload(
        create.provider,
        create.host,
        create.port,
        create.database,
        create.username,
        create.password,
        create.filePath
      )
    }
    return body
  }, [create, createEffectiveName])

  /** The body the edit request sends. */
  const editPayload = useCallback(() => {
    if (!edit) return null
    const body: Record<string, unknown> = {
      name: edit.name,
      environment: edit.environment,
      allowAgentMutations: edit.allowAgentMutations,
      requireApprovalForMutations: edit.requireApprovalForMutations,
    }
    if (edit.mode === "raw") {
      if (edit.connStr.trim() !== "") body.connectionString = edit.connStr
    } else {
      body.manual = manualPayload(
        edit.provider,
        edit.host,
        edit.port,
        edit.database,
        edit.username,
        edit.password,
        edit.filePath
      )
    }
    return body
  }, [edit])

  const resetCreate = useCallback(() => setCreate(emptyCreate()), [])

  /**
   * The provider changed: the fields of the provider we are leaving do not
   * apply to the one we are switching to.
   */
  const changeCreateProvider = useCallback((value: string | null) => {
    if (!value) return
    setCreate((prev) => {
      if (value === "Sqlite") {
        return { ...prev, provider: value as Provider, host: "", port: "", database: "", username: "", password: "" }
      }
      return { ...prev, provider: value as Provider, filePath: "" }
    })
  }, [])

  /** Opens the edit form on a connection, with its stored values in the fields. */
  const openEdit = useCallback((conn: DbConn) => {
    const details = conn.details ?? null
    setEdit({
      id: conn.id,
      name: conn.name,
      provider: conn.provider as Provider,
      // The stored connection is read back into fields so the form can be
      // pre-filled. The password is never part of that answer - it stays
      // blank, and a blank password keeps the stored one.
      mode: details ? "manual" : "raw",
      connStr: "",
      host: details?.host ?? "",
      port: details?.port != null ? String(details.port) : "",
      database: details?.database ?? "",
      username: details?.username ?? "",
      password: "",
      filePath: details?.filePath ?? "",
      environment: conn.environment,
      allowAgentMutations: conn.allowAgentMutations,
      requireApprovalForMutations: conn.requireApprovalForMutations,
    })
  }, [])

  const closeEdit = useCallback(() => setEdit(null), [])

  /** The key that names the server the credentials reach; a change reloads the database list. */
  const createServerKey = `${create.provider}|${create.host}|${create.port}|${create.filePath}|${create.username}`
  const editServerKey = edit
    ? `${edit.provider}|${edit.host}|${edit.port}|${edit.filePath}|${edit.username}`
    : ""

  return {
    create,
    setCreate,
    createEffectiveName,
    createPortInvalid,
    createReady,
    createPayload,
    resetCreate,
    changeCreateProvider,
    edit,
    setEdit,
    editPortInvalid,
    editReady,
    editPayload,
    openEdit,
    closeEdit,
    createServerKey,
    editServerKey,
  }
}
