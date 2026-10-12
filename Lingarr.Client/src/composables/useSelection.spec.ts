import { describe, expect, it } from 'vitest'
import { nextTick, ref } from 'vue'
import { useSelection } from '@/composables/useSelection'

interface Item {
    id: number
    name: string
}

const page = (ids: number[]): Item[] => ids.map((id) => ({ id, name: `item-${id}` }))

describe('useSelection', () => {
    it('starts empty', () => {
        const items = ref<Item[]>(page([1, 2]))
        const selection = useSelection(() => items.value)

        expect(selection.selectedCount.value).toBe(0)
        expect(selection.selectAll.value).toBe(false)
        expect(selection.isSelected(items.value[0])).toBe(false)
    })

    it('toggles individual items and recomputes select-all', async () => {
        const items = ref<Item[]>(page([1, 2, 3]))
        const selection = useSelection(() => items.value)

        selection.toggleSelect(items.value[0])
        selection.toggleSelect(items.value[1])
        await nextTick()

        expect(selection.selectedCount.value).toBe(2)
        expect(selection.selectAll.value).toBe(false)

        selection.toggleSelect(items.value[2])
        await nextTick()

        expect(selection.selectAll.value).toBe(true)
    })

    it('deselecting one item clears select-all', async () => {
        const items = ref<Item[]>(page([1, 2]))
        const selection = useSelection(() => items.value)

        selection.toggleSelectAll()
        await nextTick()
        expect(selection.selectAll.value).toBe(true)

        selection.toggleSelect(items.value[0])
        await nextTick()
        expect(selection.selectAll.value).toBe(false)
        expect(selection.selectedCount.value).toBe(1)
    })

    it('toggleSelectAll snapshots the current page and clears in one click', async () => {
        const items = ref<Item[]>(page([1, 2]))
        const selection = useSelection(() => items.value)

        selection.toggleSelectAll()
        await nextTick()
        expect(selection.selectedCount.value).toBe(2)

        // navigating to another page keeps the selection sticky
        items.value = page([3, 4])
        await nextTick()
        expect(selection.selectedCount.value).toBe(2)
        expect(selection.selectAll.value).toBe(true)

        // one click clears everything, including stale ids
        selection.toggleSelectAll()
        await nextTick()
        expect(selection.selectedCount.value).toBe(0)
    })

    it('clearSelection empties everything', async () => {
        const items = ref<Item[]>(page([1, 2]))
        const selection = useSelection(() => items.value)

        selection.toggleSelectAll()
        selection.clearSelection()

        expect(selection.selectedCount.value).toBe(0)
        expect(selection.selectAll.value).toBe(false)
    })
})
