// Pure viewport-clamping geometry for the「＋添加服务」menu (UI batch 1,
// item 4): open toward the roomier side of the anchor (ties keep the original
// upward direction), cap the height so long candidate lists scroll inside the
// menu instead of being cut off by the viewport edge, and clamp left/width so
// the box never leaves the viewport horizontally.
import { describe, expect, it } from 'vitest'
import { computeClampedMenuStyle, AnchorBox } from '@/cells/settings/composables/useClampedMenu'

const box = (over: Partial<AnchorBox> = {}): AnchorBox => ({
    top: 0,
    bottom: 0,
    left: 0,
    right: 0,
    width: 0,
    ...over
})

const VIEWPORT = { width: 1280, height: 800 }

describe('computeClampedMenuStyle · 展开方向', () => {
    it('opens downward when the anchor sits in the upper half (more room below)', () => {
        const result = computeClampedMenuStyle(
            box({ top: 100, bottom: 200, right: 300, width: 300 }),
            VIEWPORT
        )

        expect(result.upward).toBe(false)
        expect(result.style['top']).toBe('206px') // bottom + 6px gap
        expect(result.style).not.toHaveProperty('bottom')
    })

    it('opens upward when the anchor sits in the lower half (more room above)', () => {
        const result = computeClampedMenuStyle(
            box({ top: 600, bottom: 700, right: 300, width: 300 }),
            VIEWPORT
        )

        expect(result.upward).toBe(true)
        expect(result.style['bottom']).toBe('206px') // viewport - top + 6px gap
        expect(result.style).not.toHaveProperty('top')
    })

    it('breaks ties upward — the original bottom-full design direction', () => {
        // 800 - 403 - 6 == 403 - 6 → equal space on both sides
        const result = computeClampedMenuStyle(
            box({ top: 397, bottom: 403, right: 300, width: 300 }),
            VIEWPORT
        )

        expect(result.upward).toBe(true)
        expect(result.style['bottom']).toBe('409px')
    })
})

describe('computeClampedMenuStyle · 高度钳制', () => {
    it('caps the height at maxHeight (default 320px) however roomy the side is', () => {
        const result = computeClampedMenuStyle(
            box({ top: 100, bottom: 200, right: 300, width: 300 }),
            VIEWPORT
        )

        expect(result.style['maxHeight']).toBe('320px')
    })

    it('shrinks the height to the available space when the viewport is short', () => {
        const result = computeClampedMenuStyle(
            box({ top: 100, bottom: 200, right: 300, width: 300 }),
            { width: 1280, height: 400 }
        )

        // downward space = 400 - 200 - 6 = 194 beats the 94px above → open down
        expect(result.upward).toBe(false)
        expect(result.style['maxHeight']).toBe('194px')
    })

    it('honours custom maxHeight/gap options', () => {
        const result = computeClampedMenuStyle(
            box({ top: 100, bottom: 200, right: 300, width: 300 }),
            VIEWPORT,
            { maxHeight: 200, gap: 10 }
        )

        expect(result.style['maxHeight']).toBe('200px')
        expect(result.style['top']).toBe('210px')
    })
})

describe('computeClampedMenuStyle · 水平钳制', () => {
    it('widens a narrow anchor to minWidth and right-aligns it with the anchor', () => {
        const result = computeClampedMenuStyle(
            box({ top: 100, bottom: 200, right: 1200, width: 100 }),
            VIEWPORT
        )

        expect(result.style['width']).toBe('220px')
        expect(result.style['left']).toBe('980px') // right - width
    })

    it('never lets left cross the viewport edges', () => {
        // anchor hugging the left edge: left is floored at the 6px gap
        const left = computeClampedMenuStyle(
            box({ top: 100, bottom: 200, right: 100, width: 100 }),
            VIEWPORT
        )
        expect(left.style['left']).toBe('6px')

        // anchor hugging the right edge: left is capped so the box stays inside
        const right = computeClampedMenuStyle(
            box({ top: 100, bottom: 200, right: 1280, width: 1280 }),
            VIEWPORT
        )
        expect(Number.parseInt(right.style['left'], 10)).toBeLessThanOrEqual(
            VIEWPORT.width - Number.parseInt(right.style['width'], 10) - 6
        )
    })
})
