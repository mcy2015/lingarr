import { computed, ref, type Ref } from 'vue'

export type SelectableItem = {
    id: number
}

/**
 * Shared selection state machine for list pages (movies, shows, translation
 * requests). Selection is tracked by item id and `selectAll` mirrors the
 * behavior of the previous per-store implementations:
 * - `toggleSelectAll` flips a sticky `selectAll` flag; selecting snapshots the
 *   ids of the currently listed items (so the flag stays true after paging or
 *   refetching until it is toggled off or cleared).
 * - `toggleSelect` recomputes `selectAll` as "every currently listed item is
 *   selected".
 *
 * @param items accessor for the currently listed items, used to snapshot ids
 *   on select-all and to recompute `selectAll` on individual toggles.
 */
export function useSelection<T extends SelectableItem>(items: () => T[]) {
    const selectedIds: Ref<Set<number>> = ref(new Set<number>())
    const selectAll = ref(false)

    const selectedCount = computed(() => selectedIds.value.size)

    function isSelected(item: T): boolean {
        return selectedIds.value.has(item.id)
    }

    function toggleSelect(item: T): void {
        const nextIds = new Set(selectedIds.value)
        if (!nextIds.delete(item.id)) {
            nextIds.add(item.id)
        }
        selectedIds.value = nextIds
        selectAll.value = nextIds.size === items().length
    }

    function toggleSelectAll(): void {
        if (selectAll.value) {
            clearSelection()
            return
        }
        selectedIds.value = new Set(items().map((item) => item.id))
        selectAll.value = true
    }

    function clearSelection(): void {
        selectedIds.value = new Set<number>()
        selectAll.value = false
    }

    return {
        selectedIds,
        selectedCount,
        selectAll,
        isSelected,
        toggleSelect,
        toggleSelectAll,
        clearSelection
    }
}
