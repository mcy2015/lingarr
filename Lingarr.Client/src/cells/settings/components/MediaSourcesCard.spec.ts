// @vitest-environment happy-dom
// MediaSourcesCard (screen-06 B): Sonarr/Radarr setlist rows with real
// addresses and sync counts, re-sync firing the existing index jobs, the
// edit modal echoing current values, the webhook fold and the mapping
// drill-down row.
import { beforeEach, describe, expect, it, onTestFinished, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { flushPromises, mount } from '@vue/test-utils'
import MediaSourcesCard from '@/cells/settings/components/MediaSourcesCard.vue'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { SETTINGS } from '@/types'
import en from '@/i18n/locales/en.json'
// side effect: installs String.prototype.format used by InputComponent validation
import '@/cells/kernel/utils/string'

const indexMoviesMock = vi.hoisted(() => vi.fn())
const indexShowsMock = vi.hoisted(() => vi.fn())
const moviesMock = vi.hoisted(() => vi.fn())
const showsMock = vi.hoisted(() => vi.fn())
const setEncryptedSettingMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/settings/services/scheduleService', () => ({
    scheduleService: () => ({
        indexMovies: indexMoviesMock,
        indexShows: indexShowsMock
    })
}))

vi.mock('@/cells/app/api/mediaService', () => ({
    mediaService: () => ({
        movies: moviesMock,
        shows: showsMock
    })
}))

const statusMock = vi.hoisted(() =>
    vi.fn().mockResolvedValue({ checked: 0, unreachable: [] })
)

vi.mock('@/cells/settings/services/mappingService', () => ({
    mappingService: () => ({
        getMappings: vi
            .fn()
            .mockResolvedValue([
                { sourcePath: '/media', destinationPath: '/tv', mediaType: 'Show' }
            ]),
        setMappings: vi.fn().mockResolvedValue(undefined),
        status: statusMock
    })
}))

vi.mock('@/cells/app/api/settingService', () => ({
    settingService: () => ({
        setSetting: vi.fn().mockResolvedValue(undefined),
        setEncryptedSetting: setEncryptedSettingMock,
        getSettings: vi.fn().mockResolvedValue({}),
        getEncryptedSettingStates: vi.fn().mockResolvedValue({})
    })
}))

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

beforeEach(() => {
    indexMoviesMock.mockReset().mockResolvedValue(undefined)
    indexShowsMock.mockReset().mockResolvedValue(undefined)
    moviesMock.mockReset().mockResolvedValue({ totalCount: 312, items: [] })
    showsMock.mockReset().mockResolvedValue({ totalCount: 46, items: [] })
    setEncryptedSettingMock.mockReset().mockResolvedValue(undefined)
})

async function mountCard() {
    const pinia = createPinia()
    const settingStore = useSettingStore(pinia)
    settingStore.storeSetting(SETTINGS.RADARR_URL, 'http://radarr:7878')
    settingStore.storeSetting(SETTINGS.SONARR_URL, 'http://sonarr:8989')
    // Security walk fix: encrypted settings are boolean "configured" flags
    // server-side — no key material exists client-side to seed or leak.
    settingStore.encrypted_settings.radarr_api_key = true
    settingStore.encrypted_settings.sonarr_api_key = true

    const router = createRouter({
        history: createMemoryHistory(),
        routes: [
            { path: '/', name: 'home', component: { template: '<div />' } },
            {
                path: '/settings/mapping',
                name: 'settings-mapping',
                component: { template: '<div />' }
            }
        ]
    })
    await router.push('/')
    await router.isReady()

    const wrapper = mount(MediaSourcesCard, {
        global: { plugins: [pinia, i18n, router] }
    })
    onTestFinished(() => wrapper.unmount())
    await flushPromises()
    return { wrapper, pinia, router, toastStore: useToastStore(pinia) }
}

describe('MediaSourcesCard · 行渲染', () => {
    it('renders Sonarr/Radarr rows with the configured address and synced counts', async () => {
        const { wrapper } = await mountCard()

        const radarr = wrapper.find('[data-test="source-row-radarr"]')
        expect(radarr.text()).toContain('Radarr')
        expect(radarr.text()).toContain('http://radarr:7878')
        expect(radarr.text()).toContain(en.settings.sources.moviesSynced.replace('{count}', '312'))

        const sonarr = wrapper.find('[data-test="source-row-sonarr"]')
        expect(sonarr.text()).toContain('http://sonarr:8989')
        expect(sonarr.text()).toContain(en.settings.sources.showsSynced.replace('{count}', '46'))
    })

    it('falls back to a not-configured hint when the address is empty', async () => {
        const { wrapper, pinia } = await mountCard()
        const settingStore = useSettingStore(pinia)
        settingStore.storeSetting(SETTINGS.SONARR_URL, '')
        await flushPromises()

        const sonarr = wrapper.find('[data-test="source-row-sonarr"]')
        expect(sonarr.text()).toContain(en.settings.sources.notConfigured)
        // resync is disabled without an address
        expect(wrapper.find('[data-test="resync-sonarr"]').attributes('disabled')).toBeDefined()
    })
})

describe('MediaSourcesCard · 重新同步', () => {
    it('triggers the existing movie/show index jobs and toasts', async () => {
        const { wrapper, toastStore } = await mountCard()

        await wrapper.find('[data-test="resync-radarr"]').trigger('click')
        await flushPromises()
        expect(indexMoviesMock).toHaveBeenCalledTimes(1)
        expect(toastStore.toasts[0].message).toBe(
            en.settings.sources.syncTriggered.replace('{name}', 'Radarr')
        )

        await wrapper.find('[data-test="resync-sonarr"]').trigger('click')
        await flushPromises()
        expect(indexShowsMock).toHaveBeenCalledTimes(1)
    })
})

describe('MediaSourcesCard · 编辑模态', () => {
    it('echoes the stored address in the edit modal', async () => {
        const { wrapper } = await mountCard()

        await wrapper.find('[data-test="edit-source-radarr"]').trigger('click')
        await flushPromises()

        const modal = document.querySelector('[data-test="source-modal-radarr"]')
        expect(modal).not.toBeNull()
        const inputs = [...(modal?.querySelectorAll('input') ?? [])] as HTMLInputElement[]
        expect(inputs[0].value).toBe('http://radarr:7878')
    })

    it('never echoes the stored API key and hints to leave blank to keep it', async () => {
        const { wrapper } = await mountCard()

        await wrapper.find('[data-test="edit-source-sonarr"]').trigger('click')
        await flushPromises()

        const modal = document.querySelector('[data-test="source-modal-sonarr"]')
        const inputs = [...(modal?.querySelectorAll('input') ?? [])] as HTMLInputElement[]
        // 敏感值统一策略(输入型):type=password 且不回显,占位符说明留空保持不变
        expect(inputs[1].type).toBe('password')
        expect(inputs[1].value).toBe('')
        expect(inputs[1].placeholder).toBe(en.services.integration.apiKeyConfigured)
    })

    it('keeps the stored key when the input is cleared (no empty overwrite)', async () => {
        vi.useFakeTimers()
        try {
            const { wrapper, pinia } = await mountCard()
            setEncryptedSettingMock.mockClear()

            await wrapper.find('[data-test="edit-source-radarr"]').trigger('click')
            await flushPromises()

            const modal = document.querySelector('[data-test="source-modal-radarr"]')
            const keyInput = [...(modal?.querySelectorAll('input') ?? [])][1] as HTMLInputElement
            keyInput.value = ''
            keyInput.dispatchEvent(new Event('input'))
            await vi.advanceTimersByTimeAsync(1100)
            await flushPromises()

            expect(setEncryptedSettingMock).not.toHaveBeenCalled()
            expect(
                useSettingStore(pinia).isEncryptedSettingConfigured('radarr_api_key' as never)
            ).toBe(true)
        } finally {
            vi.useRealTimers()
        }
    })

    it('writes a newly typed key through the encrypted channel once it is valid', async () => {
        vi.useFakeTimers()
        try {
            const { wrapper } = await mountCard()
            setEncryptedSettingMock.mockClear()

            await wrapper.find('[data-test="edit-source-radarr"]').trigger('click')
            await flushPromises()

            const modal = document.querySelector('[data-test="source-modal-radarr"]')
            const keyInput = [...(modal?.querySelectorAll('input') ?? [])][1] as HTMLInputElement
            const freshKey = 'k'.repeat(32)
            keyInput.value = freshKey
            keyInput.dispatchEvent(new Event('input'))
            await vi.advanceTimersByTimeAsync(1100)
            await flushPromises()

            expect(setEncryptedSettingMock).toHaveBeenCalledWith('radarr_api_key', freshKey)
        } finally {
            vi.useRealTimers()
        }
    })
})

describe('MediaSourcesCard · Webhook 折叠与路径映射行', () => {
    it('reveals the webhook URLs only after unfolding', async () => {
        const { wrapper } = await mountCard()

        expect(wrapper.text()).not.toContain('/api/webhook/radarr')
        await wrapper.find('[data-test="webhook-fold"]').trigger('click')
        expect(wrapper.text()).toContain('/api/webhook/radarr')
        expect(wrapper.text()).toContain('/api/webhook/sonarr')
    })

    it('shows the mapping count and drills down to the mapping page', async () => {
        const { wrapper, router } = await mountCard()

        const row = wrapper.find('[data-test="mapping-row"]')
        expect(row.text()).toContain(i18n.global.t('settings.sources.mappingHint', 1))
        // clean ground truth: no unreachable warning anywhere
        expect(wrapper.find('[data-test="mapping-unreachable"]').exists()).toBe(false)
        await wrapper.find('[data-test="mapping-open"]').trigger('click')
        await flushPromises()
        expect(router.currentRoute.value.name).toBe('settings-mapping')
    })

    it('sits between the source rows and the webhook fold, warning when paths are unreachable', async () => {
        statusMock.mockResolvedValueOnce({
            checked: 3,
            unreachable: [
                { mediaType: 'movie', path: '/nope/a' },
                { mediaType: 'show', path: '/nope/b' }
            ]
        })
        const { wrapper } = await mountCard()
        await new Promise((resolve) => setTimeout(resolve, 0))

        // row order: sources → mapping → webhook (mapping follows its cause)
        const follows = (a: string, b: string) =>
            (wrapper.find(`[data-test="${a}"]`).element as Element).compareDocumentPosition(
                wrapper.find(`[data-test="${b}"]`).element as Node
            ) & Node.DOCUMENT_POSITION_FOLLOWING
        expect(follows('source-row-radarr', 'mapping-row')).toBeTruthy()
        expect(follows('mapping-row', 'webhook-fold')).toBeTruthy()

        const warning = wrapper.find('[data-test="mapping-unreachable"]')
        expect(warning.exists()).toBe(true)
        expect(warning.text()).toContain('2')
    })
})

describe('MediaSourcesCard · 媒体源同步 cron 行组(已迁出)', () => {
    it('no longer renders the indexer cron inputs (cron lives in the job table)', async () => {
        const { wrapper, pinia } = await mountCard()
        const settingStore = useSettingStore(pinia)
        settingStore.storeSetting(SETTINGS.MOVIE_SCHEDULE, '0 4 * * *')
        settingStore.storeSetting(SETTINGS.SHOW_SCHEDULE, '0 5 * * *')
        await flushPromises()

        expect(wrapper.text()).not.toContain(en.settings.sources.indexHeading)
        // the only inputs left belong to the source edit modal (closed)
        expect(wrapper.findAll('input')).toHaveLength(0)
    })
})
