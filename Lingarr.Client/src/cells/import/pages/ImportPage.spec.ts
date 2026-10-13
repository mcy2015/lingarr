// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { mount, VueWrapper } from '@vue/test-utils'
import ImportPage from '@/cells/import/pages/ImportPage.vue'
import { useSettingStore } from '@/cells/app/stores/setting'
import { SETTINGS } from '@/types'
import en from '@/i18n/locales/en.json'

const estimateMock = vi.hoisted(() => vi.fn())
const showsMock = vi.hoisted(() => vi.fn())
const parseFilenamesMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/import/services/importService', () => ({
    importService: () => ({
        estimate: estimateMock,
        scanPath: vi.fn(),
        parseFilenames: parseFilenamesMock,
        importPaths: vi.fn(),
        importFiles: vi.fn()
    })
}))

vi.mock('@/cells/app/api/mediaService', () => ({
    mediaService: () => ({ shows: showsMock })
}))

estimateMock.mockResolvedValue({ cost: 0.11, currency: 'USD' })
showsMock.mockResolvedValue({ items: [] })
parseFilenamesMock.mockResolvedValue([])

interface ImportRowVm {
    fileName: string
    sizeBytes?: number
    action: 'translate' | 'store'
}

interface ImportVm {
    rows: ImportRowVm[]
}

// flushPromises relies on setImmediate/setTimeout which the fake timers
// own — advancing by 0ms yields the event loop the same way and drains
// the microtask queue (same approach as LibraryPage.spec).
const settle = async (): Promise<void> => {
    await vi.advanceTimersByTimeAsync(0)
}

// the estimate refresh is debounced 300ms
const settleEstimate = async (): Promise<void> => {
    await vi.advanceTimersByTimeAsync(400)
}

async function mountImportPage(): Promise<{ wrapper: VueWrapper; vm: ImportVm }> {
    const pinia = createPinia()
    const settingStore = useSettingStore(pinia)
    settingStore.storeSetting(SETTINGS.SOURCE_LANGUAGES, [{ code: 'EN', name: 'English' }])
    settingStore.storeSetting(SETTINGS.TARGET_LANGUAGES, [{ code: 'ZH', name: 'Chinese' }])

    const i18n = createI18n({
        legacy: false,
        locale: 'en',
        messages: { en },
        missingWarn: false,
        fallbackWarn: false
    })

    const router = createRouter({
        history: createMemoryHistory(),
        routes: [{ path: '/import', name: 'import', component: ImportPage }]
    })
    await router.push('/import')
    await router.isReady()

    const wrapper = mount(ImportPage, {
        global: { plugins: [pinia, i18n, router] }
    })
    await settle()

    return { wrapper, vm: wrapper.vm as unknown as ImportVm }
}

function addRow(vm: ImportVm, fileName: string, sizeBytes: number, action: 'translate' | 'store') {
    vm.rows.push({
        file: null,
        serverPath: undefined,
        sizeBytes,
        fileName,
        action,
        parse: null,
        inspection: null,
        result: null
    } as unknown as ImportRowVm)
}

function ctaButton(wrapper: VueWrapper) {
    return wrapper.findAll('button').find((button) => button.text().startsWith('Import '))
}

describe('ImportPage · 导入成本预估 (P0-C)', () => {
    beforeEach(() => {
        vi.useFakeTimers()
        estimateMock.mockClear()
    })

    afterEach(() => {
        vi.useRealTimers()
    })

    it('does not estimate or show a cost suffix without translate rows', async () => {
        const { wrapper } = await mountImportPage()
        await settleEstimate()

        expect(estimateMock).not.toHaveBeenCalled()
        expect(wrapper.text()).not.toContain('≈')
        expect(wrapper.text()).not.toContain('cost unknown')
    })

    it('estimates translate rows with the halved-byte heuristic and appends the cost', async () => {
        const { wrapper, vm } = await mountImportPage()

        addRow(vm, 'a.srt', 2000, 'translate')
        addRow(vm, 'b.ass', 1000, 'store')
        await settleEstimate()

        expect(estimateMock).toHaveBeenCalledTimes(1)
        expect(estimateMock).toHaveBeenCalledWith({
            files: [{ name: 'a.srt', characters: 1000 }]
        })
        expect(wrapper.text()).toContain('≈ $0.11')
        expect(ctaButton(wrapper)!.text()).toContain('≈ $0.11')
    })

    it('recalculates when a row action switches and clears the suffix when none translate', async () => {
        const { wrapper, vm } = await mountImportPage()

        addRow(vm, 'a.srt', 2000, 'translate')
        await settleEstimate()
        expect(wrapper.text()).toContain('≈ $0.11')

        vm.rows[0].action = 'store'
        await settleEstimate()

        expect(wrapper.text()).not.toContain('≈')
        expect(ctaButton(wrapper)!.text()).toBe('Import 1 · translate 0')
    })

    it('shows cost unknown when the provider has no rate (null, not $0)', async () => {
        estimateMock.mockResolvedValue({ cost: null, currency: 'USD' })
        const { wrapper, vm } = await mountImportPage()

        addRow(vm, 'a.srt', 2000, 'translate')
        await settleEstimate()

        expect(wrapper.text()).toContain('cost unknown')
        expect(ctaButton(wrapper)!.text()).toContain('cost unknown')
        expect(wrapper.text()).not.toContain('$0.00')
    })

    it('treats a failed estimate request as unknown cost', async () => {
        estimateMock.mockRejectedValue(new Error('offline'))
        const { wrapper, vm } = await mountImportPage()

        addRow(vm, 'a.srt', 2000, 'translate')
        await settleEstimate()

        expect(wrapper.text()).toContain('cost unknown')
    })
})
