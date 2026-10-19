// @vitest-environment happy-dom
// GlossaryCard (screen-06 C): header carries「添加术语」and「导入 CSV」;
// the import runs a real file parse (header/malformed/mode mapping handled
// by glossaryCsv.spec) and submits the valid rows to POST /api/glossary/bulk,
// toasting the combined imported/skipped counts and refreshing the list.
// The export button (R9a) fetches /api/glossary/export as a blob and drives
// the object-URL download; buildGlossaryCsv is covered here as the pure
// client-side mirror of the export format (BOM + escaping).
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { flushPromises, mount } from '@vue/test-utils'
import GlossaryCard from '@/cells/settings/components/GlossaryCard.vue'
import { useToastStore } from '@/cells/app/stores/toast'
import { buildGlossaryCsv, parseGlossaryCsv } from '@/cells/settings/services/glossaryCsv'
import { ITranslationGlossary } from '@/types'
import en from '@/i18n/locales/en.json'

const getAll = vi.hoisted(() => vi.fn())
const bulkImport = vi.hoisted(() => vi.fn())
const axiosGet = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/glossaryService', () => ({
    glossaryService: () => ({
        getAll,
        create: vi.fn(),
        update: vi.fn(),
        remove: vi.fn(),
        bulkImport
    })
}))

// The card reaches the export endpoint through the shared configured axios
// static (baseURL + interceptors live on it); only `get` is exercised here.
vi.mock('axios', () => ({ default: { get: axiosGet } }))

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

const term = (id: number, sourceTerm: string, targetTerm: string, isLocked: boolean): ITranslationGlossary => ({
    id,
    sourceTerm,
    targetTerm,
    note: null,
    isLocked
})

async function mountCard() {
    const pinia = createPinia()
    const toastStore = useToastStore(pinia)

    const wrapper = mount(GlossaryCard, {
        global: { plugins: [pinia, i18n] }
    })
    await flushPromises()
    return { wrapper, toastStore }
}

function pickCsv(wrapper: Awaited<ReturnType<typeof mountCard>>['wrapper'], content: string): void {
    const input = wrapper.find('[data-test="glossary-csv-input"]').element as HTMLInputElement
    const file = new File([content], 'glossary.csv', { type: 'text/csv' })
    Object.defineProperty(input, 'files', { value: [file] })
    input.dispatchEvent(new Event('change'))
}

describe('GlossaryCard · 导入 CSV', () => {
    it('shows the import button next to add-term in the card header', async () => {
        getAll.mockResolvedValue([term(1, 'Ye Wenjie', '叶文洁', true)])
        const { wrapper } = await mountCard()

        expect(wrapper.find('[data-test="glossary-import"]').exists()).toBe(true)
        expect(wrapper.find('[data-test="glossary-csv-input"]').exists()).toBe(true)
        expect(wrapper.text()).toContain('Ye Wenjie')
    })

    it('submits parsed rows to the bulk endpoint, toasts counts and refreshes', async () => {
        getAll.mockResolvedValue([])
        bulkImport.mockResolvedValue({ imported: 2, skipped: 1 })
        const { wrapper, toastStore } = await mountCard()
        expect(getAll).toHaveBeenCalledTimes(1)

        // one malformed row is skipped client-side, one is skipped server-side
        pickCsv(wrapper, 'source,target,mode\nYe Wenjie,叶文洁,locked\nWallfacer,面壁者\nbroken-row')

        await flushPromises()

        expect(bulkImport).toHaveBeenCalledTimes(1)
        expect(bulkImport).toHaveBeenCalledWith([
            { sourceTerm: 'Ye Wenjie', targetTerm: '叶文洁', matchMode: 'locked' },
            { sourceTerm: 'Wallfacer', targetTerm: '面壁者', matchMode: 'context' }
        ])
        // client-side + server-side skipped rows are both reported
        expect(toastStore.toasts[0].message).toBe('Imported 2 · skipped 2')
        expect(toastStore.toasts[0].type).toBe('success')
        // the list is refreshed after the import
        expect(getAll).toHaveBeenCalledTimes(2)
    })

    it('does not call the bulk endpoint for a file with no valid rows', async () => {
        getAll.mockResolvedValue([])
        const { wrapper, toastStore } = await mountCard()

        pickCsv(wrapper, 'only-source\n,无源')
        await flushPromises()

        expect(bulkImport).not.toHaveBeenCalled()
        expect(toastStore.toasts[0].message).toBe('Imported 0 · skipped 2')
        expect(toastStore.toasts[0].type).toBe('info')
    })

    it('toasts an error when the bulk endpoint fails', async () => {
        getAll.mockResolvedValue([])
        bulkImport.mockRejectedValue(new Error('network'))
        const { wrapper, toastStore } = await mountCard()

        pickCsv(wrapper, 'Ye Wenjie,叶文洁')
        await flushPromises()

        expect(toastStore.toasts[0].type).toBe('error')
        expect(toastStore.toasts[0].message).toBe(en.settings.glossary.importFailed)
    })
})

describe('GlossaryCard · 添加/编辑行动作区(UI 批一 · 3:按钮恒同行,绝不竖排)', () => {
    it('renders the create-row actions as one atomic horizontal group', async () => {
        getAll.mockResolvedValue([])
        const { wrapper } = await mountCard()

        // the header「Add term」button opens the inline create row
        const addButton = wrapper.findAll('button').find((button) => button.text() === en.settings.glossary.add)
        expect(addButton).toBeDefined()
        await addButton!.trigger('click')

        const actions = wrapper.find('[data-test="glossary-create-actions"]')
        expect(actions.exists()).toBe(true)
        // one flex row: both action buttons share it and can never wrap apart
        expect(actions.classes()).toContain('flex')
        expect(actions.classes()).toContain('flex-none')
        const buttons = actions.findAll('button')
        expect(buttons).toHaveLength(2)
        for (const button of buttons) {
            expect(button.classes()).toContain('whitespace-nowrap')
        }
        expect(buttons[0].attributes('data-test')).toBe('glossary-create-save')
        expect(buttons[1].attributes('data-test')).toBe('glossary-create-close')
        // the lock toggle stays a sibling of the group, not a third stacked button
        expect(actions.text()).not.toContain(en.settings.glossary.locked)
    })

    it('keeps the edit-row actions in one horizontal group with the constant Save label (UI 批一 · 2)', async () => {
        getAll.mockResolvedValue([term(1, 'Ye Wenjie', '叶文洁', true)])
        const { wrapper } = await mountCard()

        const detail = wrapper.findAll('button').find((button) => button.text() === 'Details')
        expect(detail).toBeDefined()
        await detail!.trigger('click')

        const actions = wrapper.find('[data-test="glossary-edit-actions"]')
        expect(actions.exists()).toBe(true)
        expect(actions.classes()).toContain('flex')
        const buttons = actions.findAll('button')
        expect(buttons).toHaveLength(2)
        expect(buttons[0].attributes('data-test')).toBe('glossary-edit-save')
        expect(buttons[0].text()).toBe(en.common.save)
        expect(buttons[0].text()).not.toBe(en.common.saved)
        expect(buttons[1].attributes('data-test')).toBe('glossary-edit-close')
    })

    it('collapses the create row via the close button', async () => {
        getAll.mockResolvedValue([])
        const { wrapper } = await mountCard()

        const addButton = wrapper.findAll('button').find((button) => button.text() === en.settings.glossary.add)
        await addButton!.trigger('click')
        await wrapper.find('[data-test="glossary-create-close"]').trigger('click')

        expect(wrapper.find('[data-test="glossary-create-actions"]').exists()).toBe(false)
    })
})

describe('GlossaryCard · 导出 CSV (R9a)', () => {
    // happy-dom has no Blob URL store — the object URL surface is stubbed
    // so the download path itself stays under test. Originals are captured
    // before the first override and put back afterwards.
    const originalCreateObjectURL = window.URL.createObjectURL
    const originalRevokeObjectURL = window.URL.revokeObjectURL
    let createObjectURL: ReturnType<typeof vi.fn>
    let revokeObjectURL: ReturnType<typeof vi.fn>
    let clickedAnchor: HTMLAnchorElement | null

    beforeEach(() => {
        createObjectURL = vi.fn(() => 'blob:http://localhost/glossary')
        revokeObjectURL = vi.fn()
        Object.defineProperty(window.URL, 'createObjectURL', { value: createObjectURL, configurable: true })
        Object.defineProperty(window.URL, 'revokeObjectURL', { value: revokeObjectURL, configurable: true })
        clickedAnchor = null
        vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
            clickedAnchor = this
        })
    })

    afterEach(() => {
        vi.restoreAllMocks()
        restoreUrlHelper('createObjectURL', originalCreateObjectURL)
        restoreUrlHelper('revokeObjectURL', originalRevokeObjectURL)
    })

    function restoreUrlHelper(name: 'createObjectURL' | 'revokeObjectURL', original: unknown): void {
        if (typeof original === 'function') {
            Object.defineProperty(window.URL, name, { value: original, configurable: true })
        } else if (name === 'createObjectURL') {
            delete (window.URL as { createObjectURL?: unknown }).createObjectURL
        } else {
            delete (window.URL as { revokeObjectURL?: unknown }).revokeObjectURL
        }
    }

    it('renders the export button right beside the import button', async () => {
        getAll.mockResolvedValue([term(1, 'Ye Wenjie', '叶文洁', true)])
        const { wrapper } = await mountCard()

        const exportButton = wrapper.find('[data-test="glossary-export"]')
        expect(exportButton.exists()).toBe(true)
        expect(exportButton.text()).toBe(en.settings.glossary.export)
        // 紧邻导入按钮:同一 header 行内前后相邻
        const importButton = wrapper.find('[data-test="glossary-import"]')
        expect(exportButton.element.previousElementSibling).toBe(importButton.element)
    })

    it('downloads the endpoint blob as lingarr-glossary.csv and revokes the object URL', async () => {
        getAll.mockResolvedValue([term(1, 'Ye Wenjie', '叶文洁', true)])
        const csv = '\uFEFFsource,target,mode\r\nYe Wenjie,叶文洁,locked\r\n'
        axiosGet.mockResolvedValue({ data: new Blob([csv], { type: 'text/csv' }) })

        const { wrapper } = await mountCard()
        await wrapper.find('[data-test="glossary-export"]').trigger('click')
        await flushPromises()

        expect(axiosGet).toHaveBeenCalledWith('/api/glossary/export', { responseType: 'blob' })
        expect(axiosGet).toHaveBeenCalledTimes(1)
        // the exact server blob flows into the object URL, untouched
        expect(createObjectURL).toHaveBeenCalledTimes(1)
        const blob = createObjectURL.mock.calls[0][0] as Blob
        expect(await blob.text()).toBe(csv)
        // the download anchor carries the canonical filename and was clicked
        expect(clickedAnchor).toBeInstanceOf(HTMLAnchorElement)
        expect((clickedAnchor as HTMLAnchorElement).download).toBe('lingarr-glossary.csv')
        // the object URL is released after the download fires
        expect(revokeObjectURL).toHaveBeenCalledWith('blob:http://localhost/glossary')
    })

    it('toasts an error and never creates an object URL when the export fails', async () => {
        getAll.mockResolvedValue([])
        axiosGet.mockRejectedValue(new Error('network'))

        const { wrapper, toastStore } = await mountCard()
        await wrapper.find('[data-test="glossary-export"]').trigger('click')
        await flushPromises()

        expect(createObjectURL).not.toHaveBeenCalled()
        expect(revokeObjectURL).not.toHaveBeenCalled()
        expect(toastStore.toasts[0].type).toBe('error')
        expect(toastStore.toasts[0].message).toBe(en.common.loadFailed)
    })
})

describe('buildGlossaryCsv · 导出格式(与导入解析器逐字对称)', () => {
    it('writes the UTF-8 BOM and the header row for an empty glossary', () => {
        const csv = buildGlossaryCsv([])

        expect(csv.charCodeAt(0)).toBe(0xfeff)
        expect(csv).toBe('\uFEFFsource,target,mode\r\n')
    })

    it('maps isLocked onto the locked/context mode spellings and escapes commas and quotes', () => {
        const csv = buildGlossaryCsv([
            { sourceTerm: 'Ye Wenjie', targetTerm: '叶文洁', isLocked: true },
            { sourceTerm: 'Wall, facer', targetTerm: '面壁者', isLocked: false },
            { sourceTerm: 'say "hi"', targetTerm: '说“你好”', isLocked: true }
        ])

        expect(csv).toBe(
            '\uFEFFsource,target,mode\r\n' +
                'Ye Wenjie,叶文洁,locked\r\n' +
                '"Wall, facer",面壁者,context\r\n' +
                '"say ""hi""",说“你好”,locked\r\n'
        )
    })

    it('re-imports losslessly: parseGlossaryCsv restores exactly the exported entries', () => {
        const entries = [
            { sourceTerm: 'Ye Wenjie', targetTerm: '叶文洁', isLocked: true },
            { sourceTerm: 'Wall, facer', targetTerm: '面壁者', isLocked: false },
            { sourceTerm: 'say "hi"', targetTerm: '说“你好”', isLocked: true },
            { sourceTerm: 'Sophon', targetTerm: '智子', isLocked: false }
        ]

        const parsed = parseGlossaryCsv(buildGlossaryCsv(entries))

        expect(parsed.skipped).toBe(0)
        expect(parsed.entries).toEqual([
            { sourceTerm: 'Ye Wenjie', targetTerm: '叶文洁', matchMode: 'locked' },
            { sourceTerm: 'Wall, facer', targetTerm: '面壁者', matchMode: 'context' },
            { sourceTerm: 'say "hi"', targetTerm: '说“你好”', matchMode: 'locked' },
            { sourceTerm: 'Sophon', targetTerm: '智子', matchMode: 'context' }
        ])
    })

    it('keeps the BOM as real EF BB BF bytes once wrapped in a Blob', async () => {
        const bytes = new Uint8Array(await new Blob([buildGlossaryCsv([])]).arrayBuffer())

        expect(Array.from(bytes.slice(0, 3))).toEqual([0xef, 0xbb, 0xbf])
    })
})
