// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest'
import { defineComponent, h, type WritableComputedRef } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { usePagedList } from '@/cells/queue/composables/usePagedList'
import type { IFilter, IPagedResult } from '@/types'

const SEARCH_DEBOUNCE_MS = 300

describe('usePagedList', () => {
    let page: IPagedResult<string>
    let currentFilter: IFilter
    let setFilter: Mock<(filter: IFilter) => void>
    let fetch: Mock<() => void>

    beforeEach(() => {
        vi.useFakeTimers()
        page = { items: ['a', 'b'], totalCount: 2, pageNumber: 1, pageSize: 20 }
        currentFilter = {
            pageNumber: 1,
            searchQuery: '',
            sortBy: 'title',
            isAscending: true,
            status: null
        }
        setFilter = vi.fn()
        fetch = vi.fn()
    })

    afterEach(() => {
        vi.clearAllTimers()
        vi.useRealTimers()
    })

    const mountFan = () => {
        let list!: ReturnType<typeof usePagedList<string>>
        const Fan = defineComponent({
            setup() {
                list = usePagedList<string>({
                    items: () => page,
                    filter: () => currentFilter,
                    setFilter,
                    fetch
                })
                return () => h('div')
            }
        })
        const wrapper = mount(Fan)
        // the composable types `filter` as ComputedRef, but it is built with a
        // writable computed — widen it back so tests can drive the v-model
        const filter = list.filter as WritableComputedRef<IFilter>
        return { wrapper, list: { items: list.items, filter } }
    }

    it('fetches the initial page exactly once on mount', async () => {
        const { wrapper } = mountFan()
        await flushPromises()

        expect(fetch).toHaveBeenCalledTimes(1)
        wrapper.unmount()
    })

    it('debounces a filter write by 300ms before calling setFilter', async () => {
        const { wrapper, list } = mountFan()
        await flushPromises()

        const newFilter: IFilter = { ...currentFilter, searchQuery: 'alien' }
        list.filter.value = newFilter
        expect(setFilter).not.toHaveBeenCalled()

        await vi.advanceTimersByTimeAsync(SEARCH_DEBOUNCE_MS)
        expect(setFilter).toHaveBeenCalledTimes(1)
        expect(setFilter).toHaveBeenCalledWith(newFilter)
        wrapper.unmount()
    })

    it('collapses two filter writes within the debounce window into the last one', async () => {
        const { wrapper, list } = mountFan()
        await flushPromises()

        list.filter.value = { ...currentFilter, searchQuery: 'a' }
        await vi.advanceTimersByTimeAsync(SEARCH_DEBOUNCE_MS / 2)
        expect(setFilter).not.toHaveBeenCalled()

        list.filter.value = { ...currentFilter, searchQuery: 'ab' }
        await vi.advanceTimersByTimeAsync(SEARCH_DEBOUNCE_MS)
        expect(setFilter).toHaveBeenCalledTimes(1)
        expect(setFilter).toHaveBeenCalledWith({ ...currentFilter, searchQuery: 'ab' })
        wrapper.unmount()
    })
})
