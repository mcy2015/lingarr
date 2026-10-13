// @vitest-environment happy-dom
// ⌘K command palette (prototype global-interactions §3): open/close paths,
// the 300ms-debounced media/task searches, the bilingual keyword filter on
// the static settings group, group hiding, the empty state and the
// select-to-navigate behaviour.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { DOMWrapper, flushPromises, mount, VueWrapper } from '@vue/test-utils'
import CommandPalette from '@/cells/app/components/CommandPalette.vue'
import en from '@/i18n/locales/en.json'
import zh from '@/i18n/locales/zh-CN.json'
import {
    IMediaLibraryItem,
    IPagedResult,
    ITranslationRequest,
    MEDIA_TRANSLATION_STATE,
    TRANSLATION_STATUS
} from '@/types'

const mediaApi = vi.hoisted(() => ({
    library: vi.fn()
}))
const requestApi = vi.hoisted(() => ({
    requests: vi.fn()
}))

vi.mock('@/cells/app/api/mediaService', () => ({
    mediaService: () => mediaApi
}))
vi.mock('@/cells/app/api/translationRequestService', () => ({
    translationRequestService: () => requestApi
}))

const page = <T,>(items: T[]): IPagedResult<T> => ({
    items,
    pageNumber: 1,
    pageSize: items.length,
    totalCount: items.length
})

const mediaItem = (id: number, title: string, mediaType: string): IMediaLibraryItem =>
    ({
        id,
        mediaType,
        title,
        seasonCount: 1,
        episodeCount: 10,
        tracks: [],
        state: MEDIA_TRANSLATION_STATE.IN_PROGRESS,
        completedCount: 0,
        failedCount: 0,
        includeInTranslation: false
    }) as IMediaLibraryItem

const taskItem = (id: number, title: string): ITranslationRequest =>
    ({
        id,
        jobId: `job-${id}`,
        title,
        sourceLanguage: 'en',
        targetLanguage: 'zh',
        mediaType: 'Show',
        status: TRANSLATION_STATUS.INPROGRESS,
        progress: 72,
        createdAt: new Date().toISOString()
    }) as ITranslationRequest

let wrapper: VueWrapper | null = null

/// The palette teleports to body — queries go through the document.
const q = (selector: string): DOMWrapper<Element> | null => {
    const element = document.querySelector(selector)
    return element ? new DOMWrapper(element) : null
}

const mountPalette = async (locale = 'en') => {
    const router = createRouter({
        history: createMemoryHistory(),
        routes: [
            { path: '/', name: 'dashboard', component: { template: '<div />' } },
            { path: '/queue/:id', name: 'queue-detail', component: { template: '<div />' } },
            { path: '/library/show/:id', name: 'media-detail-show', component: { template: '<div />' }, props: true },
            { path: '/library/movie/:id', name: 'media-detail-movie', component: { template: '<div />' }, props: true },
            { path: '/logs', name: 'logs', component: { template: '<div />' } },
            { path: '/settings/providers', name: 'settings-providers', component: { template: '<div />' } }
        ]
    })
    await router.push('/')
    await router.isReady()

    const i18n = createI18n({
        legacy: false,
        locale,
        messages: { en, 'zh-CN': zh }
    })
    wrapper = mount(CommandPalette, {
        attachTo: document.body,
        global: { plugins: [router, i18n] }
    })
    await flushPromises()
    return { wrapper, router }
}

beforeEach(() => {
    vi.useFakeTimers()
    mediaApi.library.mockReset()
    requestApi.requests.mockReset()
    mediaApi.library.mockResolvedValue(page([]))
    requestApi.requests.mockResolvedValue(page([]))
})

afterEach(async () => {
    wrapper?.unmount()
    wrapper = null
    document.body.innerHTML = ''
    vi.useRealTimers()
})

const openPalette = async (): Promise<void> => {
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', metaKey: true, cancelable: true }))
    await vi.advanceTimersByTimeAsync(300)
    await flushPromises()
}

describe('CommandPalette (⌘K global search)', () => {
    it('stays hidden until ⌘K toggles it on and mounts focused on the input', async () => {
        await mountPalette()

        expect(q('[data-test="cmdk-overlay"]')).toBeNull()

        await openPalette()

        expect(q('[data-test="cmdk-overlay"]')).not.toBeNull()
        expect(q('[data-test="cmdk-input"]')).not.toBeNull()
        expect((q('[data-test="cmdk-input"]')!.element as HTMLInputElement).value).toBe('')
    })

    it('closes on ESC and on a second ⌘K', async () => {
        await mountPalette()

        await openPalette()
        window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', cancelable: true }))
        await flushPromises()
        expect(q('[data-test="cmdk-overlay"]')).toBeNull()

        await openPalette()
        // a second ⌘K while open toggles it closed again
        window.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', metaKey: true, cancelable: true }))
        await flushPromises()
        expect(q('[data-test="cmdk-overlay"]')).toBeNull()
    })

    it('debounces the media and task searches by 300ms and caps groups at 4/3', async () => {
        mediaApi.library.mockResolvedValue(
            page([
                mediaItem(1, 'Shōgun', 'Show'),
                mediaItem(2, 'Dune: Part Two', 'Movie'),
                mediaItem(3, 'The Bear', 'Show'),
                mediaItem(4, '3 Body Problem', 'Show'),
                mediaItem(5, 'True Detective', 'Show')
            ])
        )
        requestApi.requests.mockResolvedValue(
            page([
                taskItem(2417, 'Shōgun S01E05'),
                taskItem(2418, 'Dune: Part Two'),
                taskItem(2409, 'True Detective S04E06'),
                taskItem(2400, 'Extra')
            ])
        )

        await mountPalette()
        await openPalette()

        expect(mediaApi.library).toHaveBeenCalledTimes(1)
        expect(mediaApi.library).toHaveBeenCalledWith(
            expect.objectContaining({ searchQuery: '', sortBy: 'Title' })
        )

        await q('[data-test="cmdk-input"]')!.setValue('sho')
        expect(mediaApi.library).toHaveBeenCalledTimes(1) // not yet — debounce

        await vi.advanceTimersByTimeAsync(300)
        expect(mediaApi.library).toHaveBeenCalledTimes(2)
        expect(mediaApi.library).toHaveBeenLastCalledWith(
            expect.objectContaining({ searchQuery: 'sho' })
        )

        // caps: media ≤ 4, tasks ≤ 3
        expect(document.querySelectorAll('[data-test^="cmdk-item-media-"]')).toHaveLength(4)
        expect(document.querySelectorAll('[data-test^="cmdk-item-task-"]')).toHaveLength(3)
    })

    it('filters the settings group by Chinese and English keywords', async () => {
        await mountPalette()
        await openPalette()

        await q('[data-test="cmdk-input"]')!.setValue('hangfire')
        await vi.advanceTimersByTimeAsync(300)
        await flushPromises()

        const settingsItems = document.querySelectorAll('[data-test^="cmdk-item-settings-"]')
        expect(settingsItems).toHaveLength(1)
        expect(settingsItems[0].getAttribute('data-test')).toBe('cmdk-item-settings-schedule')
        expect(settingsItems[0].textContent ?? '').toContain('Scheduled jobs')

        // the other groups are empty (server returned nothing) and hidden
        expect(document.body.textContent ?? '').not.toContain(en.cmdk.group.media)
    })

    it('matches Chinese keywords against the English UI locale', async () => {
        await mountPalette()
        await openPalette()

        await q('[data-test="cmdk-input"]')!.setValue('术语')
        await vi.advanceTimersByTimeAsync(300)
        await flushPromises()

        const settingsItems = document.querySelectorAll('[data-test^="cmdk-item-settings-"]')
        expect(settingsItems).toHaveLength(1)
        expect(settingsItems[0].getAttribute('data-test')).toBe('cmdk-item-settings-quality')
    })

    it('renders group labels in the active locale', async () => {
        await mountPalette('zh-CN')
        await openPalette()

        expect(document.body.textContent ?? '').toContain(zh.cmdk.group.media)
        expect(document.body.textContent ?? '').toContain(zh.cmdk.group.tasks)
        expect(document.body.textContent ?? '').toContain(zh.cmdk.group.settings)
    })

    it('shows the empty state when nothing matches', async () => {
        await mountPalette()
        await openPalette()

        await q('[data-test="cmdk-input"]')!.setValue('zzz-no-match')
        await vi.advanceTimersByTimeAsync(300)
        await flushPromises()

        expect(q('[data-test="cmdk-empty"]')).not.toBeNull()
        expect(document.body.textContent ?? '').not.toContain(en.cmdk.group.settings)
    })

    it('navigates on select and closes the modal', async () => {
        requestApi.requests.mockResolvedValue(page([taskItem(2417, 'Shōgun S01E05')]))

        const { router } = await mountPalette()
        await openPalette()

        const item = q('[data-test="cmdk-item-task-2417"]')
        expect(item).not.toBeNull()
        expect(item!.text()).toContain('#2417')
        expect(item!.text()).toContain('72%')

        await item!.trigger('click')
        await flushPromises()

        expect(router.currentRoute.value.name).toBe('queue-detail')
        expect(q('[data-test="cmdk-overlay"]')).toBeNull()
    })

    it('routes media results to the matching detail screen', async () => {
        mediaApi.library.mockResolvedValue(page([mediaItem(7, 'Dune: Part Two', 'Movie')]))

        const { router } = await mountPalette()
        await openPalette()

        await q('[data-test="cmdk-item-media-Movie-7"]')!.trigger('click')
        await flushPromises()

        expect(router.currentRoute.value.name).toBe('media-detail-movie')
        expect(router.currentRoute.value.params.id).toBe('7')
    })
})
