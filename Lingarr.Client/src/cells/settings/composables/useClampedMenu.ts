import { nextTick, onBeforeUnmount, onMounted, Ref, ref, watch } from 'vue'

/**
 * Viewport-clamped dropdown positioning for the「＋添加服务」menu (UI batch 1,
 * item 4). The old `absolute bottom-full` menu opened upward inside the
 * settings scroll container: with 8+ candidates near the top of the scrollport
 * it was clipped above the container edge and no amount of scrolling could
 * reach it. `position: fixed` escapes that clipping; the math below then keeps
 * the box inside the viewport on its own.
 */

/** The subset of DOMRect the clamping math needs (easy to fake in specs). */
export interface AnchorBox {
    top: number
    bottom: number
    left: number
    right: number
    width: number
}

export interface ClampedMenuOptions {
    /** viewport gap kept around the menu in px */
    gap?: number
    /** upper bound for the menu height in px */
    maxHeight?: number
    /** lower bound for the menu height in px — keeps a tiny viewport still usable */
    minHeight?: number
    /** lower bound for the menu width in px — keeps long candidate labels readable */
    minWidth?: number
}

export interface ClampedMenuPosition {
    /** inline CSS properties for a `position: fixed` element */
    style: Record<string, string>
    /** whether the menu opens above the anchor */
    upward: boolean
}

/// Pure clamping geometry (unit-tested without a DOM): open toward the roomier
/// side of the anchor (ties open upward — the original design direction), then
/// clamp width/height so every candidate stays reachable through the menu's
/// own scroll instead of being cut off by the viewport edge.
export function computeClampedMenuStyle(
    anchor: AnchorBox,
    viewport: { width: number; height: number },
    options: ClampedMenuOptions = {}
): ClampedMenuPosition {
    const gap = options.gap ?? 6
    const maxHeight = options.maxHeight ?? 320
    const minHeight = options.minHeight ?? 120
    const minWidth = options.minWidth ?? 220

    const spaceAbove = anchor.top - gap
    const spaceBelow = viewport.height - anchor.bottom - gap
    const upward = spaceAbove >= spaceBelow
    const available = Math.max(spaceAbove, spaceBelow)

    const width = Math.max(anchor.width, minWidth)
    const left = Math.min(Math.max(gap, anchor.right - width), viewport.width - width - gap)

    const height = Math.max(minHeight, Math.min(maxHeight, available))
    const style: Record<string, string> = {
        left: `${Math.round(left)}px`,
        width: `${Math.round(width)}px`,
        maxHeight: `${Math.round(height)}px`
    }
    style[upward ? 'bottom' : 'top'] = `${Math.round(
        upward ? viewport.height - anchor.top + gap : anchor.bottom + gap
    )}px`
    return { style, upward }
}

/// Positions a fixed menu against its anchor and keeps it inside the viewport
/// while open: recomputed on open, on window resize and on any scroll (capture
/// phase — the settings scroll container does not bubble its scroll events).
export default function useClampedMenu(anchor: Ref<HTMLElement | undefined>, isOpen: Ref<boolean>) {
    const style = ref<Record<string, string>>({})
    const upward = ref(false)

    const recompute = (): void => {
        const el = anchor.value
        if (!el) return
        const position = computeClampedMenuStyle(el.getBoundingClientRect(), {
            width: window.innerWidth,
            height: window.innerHeight
        })
        style.value = position.style
        upward.value = position.upward
    }

    const onViewportChange = (): void => {
        if (isOpen.value) recompute()
    }

    watch(isOpen, async (open) => {
        if (!open) return
        await nextTick()
        recompute()
    })

    onMounted(() => {
        window.addEventListener('resize', onViewportChange)
        window.addEventListener('scroll', onViewportChange, true)
    })
    onBeforeUnmount(() => {
        window.removeEventListener('resize', onViewportChange)
        window.removeEventListener('scroll', onViewportChange, true)
    })

    return { style, upward, recompute }
}
