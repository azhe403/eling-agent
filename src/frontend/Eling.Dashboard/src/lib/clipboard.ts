/**
 * Copy text to the clipboard, resolving to whether it actually arrived.
 *
 * The async Clipboard API is the right tool, but it has two sharp edges:
 *
 * 1. It only exists in a secure context. The dashboard is often opened over
 *    plain http on a LAN address (http://192.168.x.x:4417), where
 *    `navigator.clipboard` is undefined - and there `navigator.clipboard
 *    .writeText` throws a TypeError rather than rejecting, so a bare
 *    `.catch()` would never see it.
 *
 * 2. Where it does exist, it can hang: a document that is not focused can
 *    leave `writeText` pending forever, waiting on a permission no one
 *    answers. An awaited clipboard call with no deadline is a copy that
 *    never completes and never reports anything.
 *
 * So: the API gets a deadline, and the pre-API textarea path is the
 * fallback. The fallback runs while the click that triggered the copy is
 * still a user gesture, which is what lets it work at all.
 */

/** How long the Clipboard API gets to answer before the fallback takes over. */
const CLIPBOARD_DEADLINE_MS = 1200

/** Distinguishes "the API never answered" from "the API said no". */
const NO_ANSWER = Symbol("no-answer")

export async function copyText(text: string): Promise<boolean> {
  const api = writeViaClipboardApi(text)

  if (api === NO_ANSWER) {
    // No Clipboard API here at all: fall through to the textarea path.
    return copyViaTextarea(text)
  }

  const outcome = await api
  if (outcome) return true

  // The API refused, or it never answered. Either way the textarea path is
  // worth trying; it may still succeed even though the API did not.
  return copyViaTextarea(text)
}

/**
 * Start the Clipboard API write without blocking on it.
 *
 * Returns NO_ANSWER when there is no API to use, otherwise a promise for
 * whether the write was accepted. A write that is still pending when the
 * deadline passes is left to land on its own - the text is the same either
 * way, so a late copy is harmless.
 */
function writeViaClipboardApi(
  text: string
): Promise<boolean> | typeof NO_ANSWER {
  if (typeof navigator === "undefined" || !navigator.clipboard?.writeText) {
    return NO_ANSWER
  }

  const write = navigator.clipboard
    .writeText(text)
    .then(
      () => true,
      () => false
    )

  const deadline = new Promise<boolean>((resolve) =>
    setTimeout(() => resolve(false), CLIPBOARD_DEADLINE_MS)
  )

  return Promise.race([write, deadline])
}

/**
 * The pre-Clipboard-API path: a hidden textarea, selected, then copied.
 *
 * The selection is restored afterwards. A right-click copy that leaves the
 * user with their text selection wiped is a worse experience than one that
 * never worked, because the damage is not obvious.
 */
function copyViaTextarea(text: string): boolean {
  const selection = document.getSelection()
  const ranges: Range[] = []
  if (selection) {
    for (let i = 0; i < selection.rangeCount; i++) {
      ranges.push(selection.getRangeAt(i))
    }
  }

  const area = document.createElement("textarea")
  area.value = text
  area.setAttribute("readonly", "")
  area.style.position = "fixed"
  area.style.top = "0"
  area.style.left = "-9999px"
  area.style.opacity = "0"
  document.body.appendChild(area)

  let copied = false
  try {
    area.select()
    copied = document.execCommand("copy")
  } catch {
    copied = false
  } finally {
    document.body.removeChild(area)
  }

  if (selection && ranges.length > 0) {
    selection.removeAllRanges()
    for (const range of ranges) {
      selection.addRange(range)
    }
  }

  return copied
}
