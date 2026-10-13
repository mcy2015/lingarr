import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useTranslationRequestStore } from '@/cells/app/stores/translationRequest'
import {
    IFilter,
    IPagedResult,
    IRequestProgress,
    ITranslationRequest,
    TRANSLATION_STATUS
} from '@/types'

const translationRequestApi = vi.hoisted(() => ({
    requests: vi.fn(),
    statusCounts: vi.fn(),
    pause: vi.fn(),
    resumePaused: vi.fn(),
    cancel: vi.fn(),
    remove: vi.fn(),
    retry: vi.fn(),
    resume: vi.fn(),
    proofread: vi.fn(),
    applyProofreadLine: vi.fn(),
    getActiveTranslations: vi.fn()
}))
const translateApi = vi.hoisted(() => ({
    proofreadStatus: vi.fn()
}))

vi.mock('@/cells/app/api/translationRequestService', () => ({
    translationRequestService: () => translationRequestApi
}))

vi.mock('@/cells/app/api/translateService', () => ({
    translateService: () => translateApi
}))

const mockedRequests = translationRequestApi.requests
const mockedStatusCounts = translationRequestApi.statusCounts
const mockedRemove = translationRequestApi.remove

const defaultFilter: IFilter = {
    searchQuery: '',
    sortBy: 'CreatedAt',
    isAscending: true,
    pageNumber: 1,
    status: null
}

const makeRequest = (overrides: Partial<ITranslationRequest> = {}): ITranslationRequest =>
    ({
        id: 1,
        title: 'request',
        status: TRANSLATION_STATUS.INPROGRESS,
        progress: 0,
        ...overrides
    }) as unknown as ITranslationRequest

const makeProgress = (overrides: Partial<IRequestProgress> = {}): IRequestProgress =>
    ({
        id: 1,
        status: TRANSLATION_STATUS.INPROGRESS,
        progress: 50,
        ...overrides
    }) as unknown as IRequestProgress

const seedItems = (
    store: ReturnType<typeof useTranslationRequestStore>,
    items: ITranslationRequest[]
): void => {
    store.translationRequests = {
        items,
        totalCount: items.length,
        pageNumber: 1,
        pageSize: 20
    }
}

describe('useTranslationRequestStore', () => {
    beforeEach(() => {
        setActivePinia(createPinia())
        mockedRequests
            .mockReset()
            .mockResolvedValue({ items: [], totalCount: 0, pageNumber: 1, pageSize: 20 })
        mockedStatusCounts.mockReset().mockResolvedValue({})
        mockedRemove.mockReset().mockResolvedValue(undefined)
    })

    it('starts with the default filter', () => {
        const store = useTranslationRequestStore()

        expect(store.filter).toEqual(defaultFilter)
        expect(store.getFilter).toEqual(defaultFilter)
    })

    it('setFilter resets pageNumber to 1 when the searchQuery changes', async () => {
        const store = useTranslationRequestStore()

        await store.setFilter({ ...defaultFilter, searchQuery: 'dune', pageNumber: 4 })

        expect(store.filter).toEqual({ ...defaultFilter, searchQuery: 'dune', pageNumber: 1 })
        expect(mockedRequests).toHaveBeenCalledTimes(1)
        expect(mockedRequests.mock.calls[0][0]).toBe(1)
    })

    it('setFilter keeps the requested pageNumber when the searchQuery is unchanged', async () => {
        const store = useTranslationRequestStore()
        await store.setFilter({ ...defaultFilter, searchQuery: 'dune', pageNumber: 2 })
        mockedRequests.mockClear()

        await store.setFilter({ ...defaultFilter, searchQuery: 'dune', pageNumber: 3 })

        expect(store.filter).toEqual({ ...defaultFilter, searchQuery: 'dune', pageNumber: 3 })
        expect(mockedRequests).toHaveBeenCalledTimes(1)
        expect(mockedRequests.mock.calls[0][0]).toBe(3)
    })

    it('setFilter resets pageNumber to 1 when the searchQuery is cleared', async () => {
        const store = useTranslationRequestStore()
        await store.setFilter({ ...defaultFilter, searchQuery: 'dune', pageNumber: 2 })
        mockedRequests.mockClear()

        await store.setFilter({ ...defaultFilter, searchQuery: '', pageNumber: 5 })

        expect(store.filter).toEqual({ ...defaultFilter, pageNumber: 1 })
        expect(mockedRequests).toHaveBeenCalledTimes(1)
        expect(mockedRequests.mock.calls[0][0]).toBe(1)
    })

    it('setStatusFilter sets the status, resets pageNumber and refetches', async () => {
        const store = useTranslationRequestStore()
        await store.setFilter({ ...defaultFilter, pageNumber: 3 })
        mockedRequests.mockClear()

        await store.setStatusFilter('Failed')

        expect(store.filter.status).toBe('Failed')
        expect(store.filter.pageNumber).toBe(1)
        expect(mockedRequests).toHaveBeenCalledTimes(1)
        expect(mockedRequests.mock.calls[0][0]).toBe(1)
        expect(mockedRequests.mock.calls[0][4]).toBe('Failed')
    })

    it('fetch stores the paged result and status counts from the service', async () => {
        const store = useTranslationRequestStore()
        const result: IPagedResult<ITranslationRequest> = {
            items: [makeRequest({ id: 7, title: 'Dune' })],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        }
        mockedRequests.mockResolvedValueOnce(result)
        mockedStatusCounts.mockResolvedValueOnce({ InProgress: 2, Failed: 1 })

        await store.fetch()

        expect(store.translationRequests).toEqual(result)
        expect(store.translationRequests.items[0].title).toBe('Dune')
        expect(store.statusCounts).toEqual({ InProgress: 2, Failed: 1 })
    })

    it('updateProgress patches the matching item and keeps the old error fields when none arrive', () => {
        const store = useTranslationRequestStore()
        seedItems(store, [
            makeRequest({
                id: 1,
                status: TRANSLATION_STATUS.PENDING,
                progress: 10,
                errorMessage: 'previous failure',
                stackTrace: 'previous stack'
            })
        ])

        store.updateProgress(
            makeProgress({
                id: 1,
                status: TRANSLATION_STATUS.INPROGRESS,
                progress: 55,
                completedAt: '2026-01-01T00:00:00Z'
            })
        )

        const updated = store.translationRequests.items[0]
        expect(updated.status).toBe(TRANSLATION_STATUS.INPROGRESS)
        expect(updated.progress).toBe(55)
        expect(updated.errorMessage).toBe('previous failure')
        expect(updated.stackTrace).toBe('previous stack')
        expect(updated.completedAt).toBe('2026-01-01T00:00:00Z')
    })

    it('updateProgress protects terminal items while other items update normally', () => {
        const store = useTranslationRequestStore()
        seedItems(store, [
            makeRequest({ id: 1, status: TRANSLATION_STATUS.COMPLETED, progress: 100 }),
            makeRequest({ id: 2, status: TRANSLATION_STATUS.INPROGRESS, progress: 20 })
        ])

        store.updateProgress(makeProgress({ id: 1, status: TRANSLATION_STATUS.INPROGRESS, progress: 40 }))
        store.updateProgress(makeProgress({ id: 2, status: TRANSLATION_STATUS.INPROGRESS, progress: 40 }))

        const [kept, updated] = store.translationRequests.items
        expect(kept.status).toBe(TRANSLATION_STATUS.COMPLETED)
        expect(kept.progress).toBe(100)
        expect(updated.status).toBe(TRANSLATION_STATUS.INPROGRESS)
        expect(updated.progress).toBe(40)
    })

    it('remove drops the item from translationRequests once the service resolves', async () => {
        const store = useTranslationRequestStore()
        const doomed = makeRequest({ id: 1, title: 'doomed' })
        const survivor = makeRequest({ id: 2, title: 'survivor' })
        seedItems(store, [doomed, survivor])

        await store.remove(doomed)

        expect(mockedRemove).toHaveBeenCalledWith(doomed)
        expect(store.translationRequests.items).toEqual([survivor])
    })
})
