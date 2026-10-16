// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { h, nextTick } from 'vue'
import { mount } from '@vue/test-utils'
import VirtualEpisodeList from '@/cells/library/components/VirtualEpisodeList.vue'

interface Row {
    id: number
    title: string
}

const makeRows = (count: number): Row[] =>
    Array.from({ length: count }, (_, i) => ({ id: 100 + i, title: `Episode ${i + 1}` }))

/** Mounts the virtual table with a row slot mirroring the page's episode row:
 * an index cell plus a working action button wired to a page-scope handler. */
const mountList = async (
    rows: Row[],
    options: { itemHeight?: number; onAction?: (row: Row) => void } = {}
) => {
    const onAction = options.onAction ?? vi.fn()
    // vue-tsc resolves the component's generic T as unknown through the
    // test-utils mount types — the slot/prop bodies cast back to the Row
    // shape the runtime actually receives
    const wrapper = mount(VirtualEpisodeList, {
        props: {
            items: rows,
            itemHeight: options.itemHeight ?? 20,
            overscan: 5,
            colspan: 3,
            label: 'episodes',
            keyOf: (row: unknown) => (row as Row).id
        },
        slots: {
            header: () => h('thead', [h('tr', [h('th', '#'), h('th', 'title'), h('th', '')])]),
            row: ({ item, index }: { item: unknown; index: number }) =>
                h('tr', { 'data-test': 'virtual-row' }, [
                    h('td', String(index)),
                    h('td', (item as Row).title),
                    h('td', [
                        h(
                            'button',
                            {
                                'data-test': 'virtual-row-action',
                                onClick: () => onAction(item as Row)
                            },
                            'go'
                        )
                    ])
                ]),
            empty: () => h('span', { 'data-test': 'virtual-empty' }, 'nothing here')
        }
    })
    await nextTick()
    return { wrapper, onAction }
}

describe('VirtualEpisodeList', () => {
    it('renders only the viewport window plus overscan for a thousand rows', async () => {
        const { wrapper } = await mountList(makeRows(1000))

        const rows = wrapper.findAll('[data-test="virtual-row"]')
        // fallback viewport (happy-dom reports clientHeight 0) 480px / 20px
        // rows = 24 visible + 2×5 overscan → 29 rows, never 1000
        expect(rows.length).toBe(29)
        // the window starts at the top: no top spacer, the bottom spacer
        // keeps the scroll height honest for the unrendered tail
        expect(wrapper.find('[data-test="virtual-spacer-top"]').exists()).toBe(false)
        const bottom = wrapper.find('[data-test="virtual-spacer-bottom"]')
        expect(bottom.exists()).toBe(true)
        expect(bottom.attributes('style')).toContain(`height: ${(1000 - 29) * 20}px`)
    })

    it('moves the window when the container scrolls', async () => {
        const { wrapper } = await mountList(makeRows(1000), { itemHeight: 40 })

        const scroller = wrapper.find('[data-test="virtual-scroll-container"]')
        scroller.element.scrollTop = 40 * 200
        await scroller.trigger('scroll')

        const rows = wrapper.findAll('[data-test="virtual-row"]')
        // first = 200 → start = 195; 480/40 = 12 visible + 5 → end = 217
        expect(rows.length).toBe(22)
        expect(rows[0].text()).toContain('Episode 196')
        expect(rows[rows.length - 1].text()).toContain('Episode 217')
        const top = wrapper.find('[data-test="virtual-spacer-top"]')
        expect(top.attributes('style')).toContain(`height: ${195 * 40}px`)
    })

    it('uses the measured container height when the layout engine reports one', async () => {
        const { wrapper } = await mountList(makeRows(500), { itemHeight: 20 })

        const scroller = wrapper.find('[data-test="virtual-scroll-container"]')
        Object.defineProperty(scroller.element, 'clientHeight', {
            configurable: true,
            get: () => 200
        })
        window.dispatchEvent(new Event('resize'))
        await nextTick()

        // 200px / 20px = 10 visible; at the top the upper overscan clamps
        // away → 10 + 5 below = 15 rows
        expect(wrapper.findAll('[data-test="virtual-row"]').length).toBe(15)
    })

    it('keeps row interactions working through the slot (button fires with its row)', async () => {
        const onAction = vi.fn()
        const { wrapper } = await mountList(makeRows(3), { onAction })

        const buttons = wrapper.findAll('[data-test="virtual-row-action"]')
        expect(buttons.length).toBe(3)
        await buttons[1].trigger('click')
        expect(onAction).toHaveBeenCalledWith(expect.objectContaining({ id: 101 }))
    })

    it('resets the window to the top when the item list reference changes (season switch)', async () => {
        const { wrapper } = await mountList(makeRows(1000))

        const scroller = wrapper.find('[data-test="virtual-scroll-container"]')
        scroller.element.scrollTop = 20 * 600
        await scroller.trigger('scroll')
        expect(wrapper.findAll('[data-test="virtual-row"]')[0].text()).toContain('Episode 596')

        await wrapper.setProps({ items: makeRows(3) })
        await nextTick()

        // the new season starts at the top — all 3 rows rendered, no stale
        // window past the end, no spacers
        const rows = wrapper.findAll('[data-test="virtual-row"]')
        expect(rows.length).toBe(3)
        expect(rows[0].text()).toContain('Episode 1')
        expect(wrapper.find('[data-test="virtual-spacer-top"]').exists()).toBe(false)
        expect(wrapper.find('[data-test="virtual-spacer-bottom"]').exists()).toBe(false)
    })

    it('renders the empty slot when the list has no rows', async () => {
        const { wrapper } = await mountList([])

        expect(wrapper.find('[data-test="virtual-empty"]').exists()).toBe(true)
        expect(wrapper.find('[data-test="virtual-row"]').exists()).toBe(false)
        expect(wrapper.find('[data-test="virtual-spacer-bottom"]').exists()).toBe(false)
    })
})
