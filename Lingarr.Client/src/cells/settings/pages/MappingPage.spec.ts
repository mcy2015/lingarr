// @vitest-environment happy-dom
// Path-mapping drill-down (screen-06 B drill): the pb-head carries the
// identity (back to the sources group + the real mapping count) and the head
// actions keep the original CRUD entries (add row / save all), while the card
// body renders the mapping rows with path pickers, media type and remove.
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { flushPromises, mount } from '@vue/test-utils'
import en from '@/i18n/locales/en.json'
import MappingPage from '@/cells/settings/pages/MappingPage.vue'

const getMappingsMock = vi.hoisted(() => vi.fn())
const setMappingsMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/settings/services/mappingService', () => ({
    mappingService: () => ({
        getMappings: getMappingsMock,
        setMappings: setMappingsMock
    })
}))

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

const MAPPINGS = [
    { sourcePath: '/media/movies', destinationPath: '/tv/movies', mediaType: 'Movie' },
    { sourcePath: '/media/shows', destinationPath: '', mediaType: 'Show' }
]

async function mountPage() {
    const router = createRouter({
        history: createMemoryHistory(),
        routes: [
            { path: '/', name: 'home', component: { template: '<div />' } },
            {
                path: '/settings/sources',
                name: 'settings-sources',
                component: { template: '<div />' }
            }
        ]
    })
    await router.push('/')
    await router.isReady()

    const wrapper = mount(MappingPage, {
        global: {
            plugins: [createPinia(), router, i18n],
            // the directory browser has its own spec; only its open state matters here
            stubs: { DirectoryModal: true }
        }
    })
    await flushPromises()
    return { wrapper, router }
}

beforeEach(() => {
    getMappingsMock.mockReset().mockResolvedValue([...MAPPINGS])
    setMappingsMock.mockReset().mockResolvedValue(undefined)
})

describe('MappingPage · pb-head', () => {
    it('renders the head with the sources back label and the real mapping count', async () => {
        const { wrapper } = await mountPage()

        expect(wrapper.find('[data-test="pb-back"]').text()).toContain(
            en.settings.menu.groups.sources
        )
        expect(wrapper.text()).toContain(en.settings.sources.mapping)
        expect(wrapper.find('.mono').text()).toContain('2')
    })

    it('navigates back to the sources group screen', async () => {
        const { wrapper, router } = await mountPage()

        await wrapper.find('[data-test="pb-back"]').trigger('click')
        await flushPromises()
        expect(router.currentRoute.value.name).toBe('settings-sources')
    })
})

describe('MappingPage · CRUD 控件全在', () => {
    it('renders one row per mapping with source/destination paths', async () => {
        const { wrapper } = await mountPage()

        expect(wrapper.findAll('[data-test^="mapping-pair-"]')).toHaveLength(2)
        const first = wrapper.find('[data-test="mapping-pair-0"]')
        expect(first.text()).toContain('/media/movies')
        expect(first.text()).toContain('/tv/movies')
        // empty destination offers the pick entry instead of a path
        expect(wrapper.find('[data-test="mapping-pick-target-1"]').exists()).toBe(true)
    })

    it('adds an empty row and removes an existing one through the store', async () => {
        const { wrapper } = await mountPage()

        await wrapper.find('[data-test="mapping-add"]').trigger('click')
        await flushPromises()
        expect(wrapper.findAll('[data-test^="mapping-pair-"]')).toHaveLength(3)
        expect(wrapper.find('[data-test="mapping-pick-source-2"]').exists()).toBe(true)

        await wrapper.find('[data-test="mapping-remove-0"]').trigger('click')
        await flushPromises()
        expect(wrapper.findAll('[data-test^="mapping-pair-"]')).toHaveLength(2)
    })

    it('saves all mappings through the store from the head action', async () => {
        vi.useFakeTimers()
        try {
            const { wrapper } = await mountPage()

            await wrapper.find('[data-test="mapping-save"]').trigger('click')
            await flushPromises()
            expect(setMappingsMock).toHaveBeenCalledTimes(1)
            expect(setMappingsMock.mock.calls[0][0]).toHaveLength(2)
            // the original 1s save guard elapses and re-enables the button
            vi.advanceTimersByTime(1100)
            await flushPromises()
            expect(wrapper.find('[data-test="mapping-save"]').attributes('disabled')).toBeUndefined()
        } finally {
            vi.useRealTimers()
        }
    })
})
