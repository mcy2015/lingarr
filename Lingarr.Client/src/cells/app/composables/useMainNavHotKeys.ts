import { onMounted, onUnmounted } from 'vue'
import { type Router } from 'vue-router'

/**
 * Main-nav hotkeys 1-6 (prototype global-interactions keyboard table):
 * overview, library, queue, import, proofreading, settings. Inactive while
 * the user is typing in an input/textarea/select or a contenteditable host.
 * The ⌘K palette and the proofread J/K/Enter/E keys live elsewhere.
 */
const HOTKEY_ROUTES = ['dashboard', 'library', 'queue', 'import', 'proofread', 'settings']

export const isTypingTarget = (target: EventTarget | null): boolean => {
    const element = target as HTMLElement | null
    return (
        !!element &&
        (element.tagName === 'INPUT' ||
            element.tagName === 'TEXTAREA' ||
            element.tagName === 'SELECT' ||
            element.isContentEditable)
    )

}

export function useMainNavHotKeys(router: Router): void {
    const onKeydown = (event: KeyboardEvent): void => {
        if (event.metaKey || event.ctrlKey || event.altKey) {
            return
        }
        const index = Number(event.key) - 1
        if (index >= 0 && index < HOTKEY_ROUTES.length && !isTypingTarget(event.target)) {
            event.preventDefault()
            void router.push({ name: HOTKEY_ROUTES[index] })
        }
    }

    onMounted(() => {
        window.addEventListener('keydown', onKeydown)
    })
    onUnmounted(() => {
        window.removeEventListener('keydown', onKeydown)
    })
}
