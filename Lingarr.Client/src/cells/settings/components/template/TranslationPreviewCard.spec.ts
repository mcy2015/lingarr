// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { createI18n } from 'vue-i18n'
import { flushPromises, mount, VueWrapper } from '@vue/test-utils'
import TranslationPreviewCard from '@/cells/settings/components/template/TranslationPreviewCard.vue'
import en from '@/i18n/locales/en.json'

const previewMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/translateService', () => ({
    translateService: () => ({
        previewTranslation: previewMock
    })
}))

async function mountCard(service = 'openai'): Promise<VueWrapper> {
    const i18n = createI18n({
        legacy: false,
        locale: 'en',
        messages: { en },
        missingWarn: false,
        fallbackWarn: false
    })

    const wrapper = mount(TranslationPreviewCard, {
        props: { service },
        global: { plugins: [i18n] }
    })
    await flushPromises()
    return wrapper
}

function runButton(wrapper: VueWrapper) {
    return wrapper.findAll('button').find((button) => button.text().includes('Translate'))
}

describe('TranslationPreviewCard · 试译预览 (P0-B)', () => {
    it('seeds the input with the sample sentence and no result yet', async () => {
        const wrapper = await mountCard()

        expect((wrapper.find('input').element as HTMLInputElement).value).toBe(
            'The Shōgun commands the Anjin to sail west.'
        )
        expect(wrapper.find('[data-testid="preview-output"]').exists()).toBe(false)
    })

    it('renders translation, glossary chips and meta on success', async () => {
        previewMock.mockResolvedValue({
            ok: true,
            translated: '将军命令按针向西航行。',
            latencyMs: 798.2,
            cost: 0.0001,
            glossaryHits: [
                { sourceTerm: 'Anjin', targetTerm: '按针', isLocked: true },
                { sourceTerm: 'Shōgun', targetTerm: '将军', isLocked: false }
            ],
            message: null
        })
        const wrapper = await mountCard('gemini')

        await runButton(wrapper)!.trigger('click')
        await flushPromises()

        expect(previewMock).toHaveBeenCalledWith({
            serviceType: 'gemini',
            text: 'The Shōgun commands the Anjin to sail west.'
        })
        expect(wrapper.find('[data-testid="preview-output"]').text()).toContain(
            '将军命令按针向西航行。'
        )
        const chips = wrapper.findAll('[data-testid="preview-glossary"] span')
        expect(chips).toHaveLength(2)
        expect(chips[0].text()).toBe('Anjin → 按针')
        const meta = wrapper.find('[data-testid="preview-meta"]').text()
        expect(meta).toContain('798 ms')
        expect(meta).toContain('$0.0001')
    })

    it('omits the cost segment from the meta line when unpriced', async () => {
        previewMock.mockResolvedValue({
            ok: true,
            translated: '您好',
            latencyMs: 12,
            cost: null,
            glossaryHits: [],
            message: null
        })
        const wrapper = await mountCard()

        await runButton(wrapper)!.trigger('click')
        await flushPromises()

        const meta = wrapper.find('[data-testid="preview-meta"]').text()
        expect(meta).toContain('12 ms')
        expect(meta).not.toContain('$')
    })

    it('renders the structured failure message when the provider fails', async () => {
        previewMock.mockResolvedValue({
            ok: false,
            translated: null,
            latencyMs: 3,
            cost: null,
            glossaryHits: [],
            message: 'OpenAI API key or model is not configured.'
        })
        const wrapper = await mountCard()

        await wrapper.find('input').setValue('Hello')
        await runButton(wrapper)!.trigger('click')
        await flushPromises()

        const error = wrapper.find('[data-testid="preview-error"]')
        expect(error.exists()).toBe(true)
        expect(error.text()).toContain('not configured')
        expect(wrapper.find('[data-testid="preview-output"]').exists()).toBe(false)
    })

    it('surfaces a rejected request as a structured failure', async () => {
        previewMock.mockRejectedValue(new Error('request failed'))
        const wrapper = await mountCard()

        await wrapper.find('input').setValue('Hello')
        await runButton(wrapper)!.trigger('click')
        await flushPromises()

        expect(wrapper.find('[data-testid="preview-error"]').text()).toContain('request failed')
    })

    it('disables the run button for an empty line', async () => {
        const wrapper = await mountCard()

        await wrapper.find('input').setValue('   ')

        expect(runButton(wrapper)!.attributes('disabled')).toBeDefined()
        expect(previewMock).not.toHaveBeenCalled()
    })
})
