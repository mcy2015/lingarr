// @vitest-environment happy-dom
// Path-mapping drill-down (screen-06 B drill): the pb-head carries the
// identity (back to the sources group + the real mapping count, plus the
// unreachable warning when the ground-truth check fails), the domain explain
// line answers "what do source/target mean", the source column is a text
// input (*arr's container view — Lingarr's own file browser can never reach
// it) while the destination keeps the picker, and the test row answers with
// the same conversion the sync pipeline uses.
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { flushPromises, mount } from '@vue/test-utils'
import en from '@/i18n/locales/en.json'
import MappingPage from '@/cells/settings/pages/MappingPage.vue'
import DrillDownPage from '@/cells/app/components/DrillDownPage.vue'

const getMappingsMock = vi.hoisted(() => vi.fn())
const setMappingsMock = vi.hoisted(() => vi.fn())
const statusMock = vi.hoisted(() => vi.fn())
const testMappingMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/settings/services/mappingService', () => ({
    mappingService: () => ({
        getMappings: getMappingsMock,
        setMappings: setMappingsMock,
        status: statusMock,
        testMapping: testMappingMock
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
    statusMock.mockReset().mockResolvedValue({ checked: 0, unreachable: [] })
    testMappingMock.mockReset().mockResolvedValue({
        ok: false,
        matched: false,
        mappedPath: '',
        exists: false
    })
})

describe('MappingPage · pb-head', () => {
    it('roots on the shared DrillDownPage skeleton (dd-page)', async () => {
        const { wrapper } = await mountPage()

        const skeleton = wrapper.findComponent(DrillDownPage)
        expect(skeleton.exists()).toBe(true)
        const root = wrapper.find('[data-test="dd-page"]')
        expect(root.exists()).toBe(true)
        // root-ness: the skeleton div is the first element under the mount
        // point (fragment root: the directory modal renders after it)
        expect([...(root.element.parentElement?.children ?? [])].indexOf(root.element)).toBe(0)
    })

    it('renders the head with the sources back label and the real mapping count', async () => {
        const { wrapper } = await mountPage()

        expect(wrapper.find('[data-test="pb-back"]').text()).toContain(
            en.settings.menu.groups.sources
        )
        expect(wrapper.text()).toContain(en.settings.sources.mapping)
        expect(wrapper.find('.mono').text()).toContain('2')
    })

    it('appends the unreachable warning to the sub line only when paths are missing', async () => {
        statusMock.mockResolvedValue({ checked: 3, unreachable: [{ mediaType: 'movie', path: '/x' }] })
        const { wrapper } = await mountPage()
        const sub = wrapper.find('[data-test="dd-page"] .mono.truncate')
        expect(sub.text()).toContain('1')
        expect(sub.text()).toContain('unreachable')

        statusMock.mockResolvedValue({ checked: 3, unreachable: [] })
        const clean = await mountPage()
        expect(clean.wrapper.find('[data-test="dd-page"] .mono.truncate').text()).not.toContain(
            'unreachable'
        )
    })

    it('navigates back to the sources group screen', async () => {
        const { wrapper, router } = await mountPage()

        await wrapper.find('[data-test="pb-back"]').trigger('click')
        await flushPromises()
        expect(router.currentRoute.value.name).toBe('settings-sources')
    })
})

describe('MappingPage · 领域说明与源/目标输入', () => {
    it('shows the domain explain line before any mapping row', async () => {
        const { wrapper } = await mountPage()

        const explain = wrapper.find('[data-test="mapping-explain"]')
        expect(explain.exists()).toBe(true)
        expect(explain.text()).toContain(en.services.mapping.explain)
        // it precedes the first mapping row
        const follows = (explain.element as Element).compareDocumentPosition(
            wrapper.find('[data-test="mapping-pair-0"]').element as Node
        ) & Node.DOCUMENT_POSITION_FOLLOWING
        expect(follows).toBeTruthy()
    })

    it('renders one row per mapping — source as text input, destination keeps the picker', async () => {
        const { wrapper } = await mountPage()

        expect(wrapper.findAll('[data-test^="mapping-pair-"]')).toHaveLength(2)
        const sourceInput = wrapper.find('[data-test="mapping-source-input-0"]')
            .element as HTMLInputElement
        expect(sourceInput.value).toBe('/media/movies')
        expect(wrapper.find('[data-test="mapping-pair-0"]').text()).toContain('/tv/movies')
        // empty destination offers the pick entry instead of a path
        expect(wrapper.find('[data-test="mapping-pick-target-1"]').exists()).toBe(true)
        // the source picker is gone everywhere — the file browser shows
        // Lingarr's own filesystem and can never reach the *arr view
        expect(wrapper.findAll('[data-test^="mapping-pick-source-"]').length).toBe(0)
    })

    it('adds an empty row with a source input and removes an existing one', async () => {
        const { wrapper } = await mountPage()

        await wrapper.find('[data-test="mapping-add"]').trigger('click')
        await flushPromises()
        expect(wrapper.findAll('[data-test^="mapping-pair-"]')).toHaveLength(3)
        expect(wrapper.find('[data-test="mapping-source-input-2"]').exists()).toBe(true)

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
            // saving also refreshes the ground-truth status
            expect(statusMock).toHaveBeenCalled()
            // the original 1s save guard elapses and re-enables the button
            vi.advanceTimersByTime(1100)
            await flushPromises()
            expect(wrapper.find('[data-test="mapping-save"]').attributes('disabled')).toBeUndefined()
        } finally {
            vi.useRealTimers()
        }
    })
})

describe('MappingPage · 测试映射(verdict 三分支)', () => {
    it('answers a reachable mapping with the mapped path', async () => {
        testMappingMock.mockResolvedValue({
            ok: true,
            matched: true,
            mappedPath: '/tv/movies/Foo',
            exists: true
        })
        const { wrapper } = await mountPage()

        await wrapper.find('[data-test="mapping-test-input"]').setValue('/media/movies/Foo')
        await wrapper.find('[data-test="mapping-test-run"]').trigger('click')
        await flushPromises()

        expect(testMappingMock).toHaveBeenCalledWith('/media/movies/Foo', 'Movie')
        const result = wrapper.find('[data-test="mapping-test-result"]')
        expect(result.text()).toContain('/tv/movies/Foo')
        expect(result.text()).toContain('✓')
    })

    it('answers a matched but missing directory explicitly', async () => {
        testMappingMock.mockResolvedValue({
            ok: false,
            matched: true,
            mappedPath: '/tv/movies/Foo',
            exists: false
        })
        const { wrapper } = await mountPage()

        await wrapper.find('[data-test="mapping-test-input"]').setValue('/media/movies/Foo')
        await wrapper.find('[data-test="mapping-test-run"]').trigger('click')
        await flushPromises()

        const result = wrapper.find('[data-test="mapping-test-result"]')
        expect(result.text()).toContain('✗')
        expect(result.text()).toContain('/tv/movies/Foo')
    })

    it('answers an unmatched path with the no-hit verdict', async () => {
        testMappingMock.mockResolvedValue({
            ok: false,
            matched: false,
            mappedPath: '/other/Foo',
            exists: false
        })
        const { wrapper } = await mountPage()

        await wrapper.find('[data-test="mapping-test-input"]').setValue('/other/Foo')
        await wrapper.find('[data-test="mapping-test-run"]').trigger('click')
        await flushPromises()

        const result = wrapper.find('[data-test="mapping-test-result"]')
        expect(result.text()).toContain('✗')
        expect(result.text()).toContain('not contained by any saved source path')
    })
})
