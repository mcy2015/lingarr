// @vitest-environment happy-dom
// GlossaryCard (screen-06 C): header carries「添加术语」and「导入 CSV」;
// the import runs a real file parse (header/malformed/mode mapping handled
// by glossaryCsv.spec) and submits the valid rows to POST /api/glossary/bulk,
// toasting the combined imported/skipped counts and refreshing the list.
import { describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { flushPromises, mount } from '@vue/test-utils'
import GlossaryCard from '@/cells/settings/components/GlossaryCard.vue'
import { useToastStore } from '@/cells/app/stores/toast'
import { ITranslationGlossary } from '@/types'
import en from '@/i18n/locales/en.json'

const getAll = vi.hoisted(() => vi.fn())
const bulkImport = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/glossaryService', () => ({
    glossaryService: () => ({
        getAll,
        create: vi.fn(),
        update: vi.fn(),
        remove: vi.fn(),
        bulkImport
    })
}))

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
