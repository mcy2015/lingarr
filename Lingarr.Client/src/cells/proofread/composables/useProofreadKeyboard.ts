import { onUnmounted } from 'vue'

/**
 * Keyboard flow for the proofreading workbench (prototype screen 5):
 * J/K move between lines, Enter approves the focused line, E starts an
 * edit. Typing in an input swallows every key — the editor owns them.
 */
export function useProofreadKeyboard(handlers: {
    next: () => void
    prev: () => void
    approve: () => void
    edit: () => void
}): void {
    const onKeydown = (event: KeyboardEvent): void => {
        const target = event.target as HTMLElement | null
        if (
            target &&
            (target.tagName === 'INPUT' ||
                target.tagName === 'TEXTAREA' ||
                target.isContentEditable)
        ) {
            return
        }

        switch (event.key.toLowerCase()) {
            case 'j':
                handlers.next()
                break
            case 'k':
                handlers.prev()
                break
            case 'enter':
                handlers.approve()
                break
            case 'e':
                handlers.edit()
                break
            default:
                return
        }
        event.preventDefault()
    }

    window.addEventListener('keydown', onKeydown)
    onUnmounted(() => window.removeEventListener('keydown', onKeydown))
}
