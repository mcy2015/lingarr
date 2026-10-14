import { onMounted, onBeforeUnmount, Ref } from 'vue'

export default function useClickOutside(
    component: Ref<HTMLElement | undefined>,
    callback: (() => void) | undefined,
    excludeComponent?: Ref<HTMLElement | undefined> | null
) {
    if (!component) {
        throw new Error('A target component has to be provided.')
    }

    if (!callback) {
        throw new Error('A callback has to be provided.')
    }

    const listener = (event: Event) => {
        if (
            event.target === component.value ||
            event.composedPath().includes(component.value as Node) ||
            event.target === excludeComponent?.value ||
            event.composedPath().includes(excludeComponent?.value as Node)
        ) {
            return
        }
        if (typeof callback === 'function') {
            callback()
        }
    }

    // Capture phase: overlays and card bodies often call stopPropagation on
    // click (e.g. modals shielding themselves from overlay-close handlers),
    // which would swallow a bubble-phase window listener and leave dropdowns
    // opened inside those modals. Capturing on the way down still sees the
    // click before any intermediate node can stop it.
    onMounted(() => {
        window.addEventListener('click', listener, true)
    })

    onBeforeUnmount(() => {
        window.removeEventListener('click', listener, true)
    })
}
