/// Writes text to the clipboard across deployment contexts.
///
/// The async Clipboard API only exists in secure contexts (HTTPS or
/// localhost). Lingarr's usual LAN deployment — plain HTTP on a NAS IP —
/// has no navigator.clipboard at all, so a bare writeText call fails
/// before it can even reject. The execCommand fallback covers both the
/// missing API and permission-denied rejections while the click's
/// user-gesture context is still active.
export const writeClipboard = async (text: string): Promise<void> => {
    try {
        if (navigator.clipboard?.writeText) {
            await navigator.clipboard.writeText(text)
            return
        }
    } catch {
        // fall through to the execCommand fallback
    }
    const textarea = document.createElement('textarea')
    textarea.value = text
    textarea.style.position = 'fixed'
    textarea.style.opacity = '0'
    document.body.appendChild(textarea)
    textarea.select()
    try {
        if (!document.execCommand('copy')) throw new Error('execCommand copy returned false')
    } finally {
        document.body.removeChild(textarea)
    }
}
