import { computed, onMounted, type ComputedRef } from 'vue'
import type { IFilter, IPagedResult } from '@/types'
import useDebounce from '@/cells/kernel/composables/useDebounce'

export interface UsePagedListOptions<T> {
    /** Paged list contents, read from the store getter. */
    items: () => IPagedResult<T>
    /** Current filter, read from the store getter/state. */
    filter: () => IFilter
    /** Store action that persists the filter and refetches. */
    setFilter: (filter: IFilter) => Promise<void> | void
    /** Store action that refetches the current page. */
    fetch: () => Promise<void> | void
}

const SEARCH_DEBOUNCE_MS = 300

/**
 * Shared list-page wiring: exposes the paged items and a writable `filter`
 * that acts as the v-model for the search input, sort controls and pagination
 * (every write is debounced before hitting `store.setFilter`, and search
 * queries reset to page 1 inside the store). Fetches the initial page on
 * mount.
 */
export function usePagedList<T>(options: UsePagedListOptions<T>) {
    const items: ComputedRef<IPagedResult<T>> = computed(options.items)

    const filter: ComputedRef<IFilter> = computed({
        get: options.filter,
        set: useDebounce((value: IFilter) => {
            options.setFilter(value)
        }, SEARCH_DEBOUNCE_MS)
    })

    onMounted(async () => {
        await options.fetch()
    })

    return { items, filter }
}

export default usePagedList
