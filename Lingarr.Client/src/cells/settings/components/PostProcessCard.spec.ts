// @vitest-environment happy-dom
// PostProcessCard (B01): variant mode select (none/simplified/traditional,
// import-option vocabulary) persists subtitle_postprocess_chinese_variant and
// the existing-library batch follows the preview→confirm shape — preview
// counts come from the server (real numbers only), execute needs a preview,
// mode changes invalidate both, and 400 messages surface verbatim.
import { describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { flushPromises, mount } from '@vue/test-utils'
import PostProcessCard from '@/cells/settings/components/PostProcessCard.vue'
import { useToastStore } from '@/cells/app/stores/toast'
import en from '@/i18n/locales/en.json'

const setSetting = vi.hoisted(() => vi.fn().mockResolvedValue(undefined))
const getSettings = vi.hoisted(() => vi.fn().mockResolvedValue({}))

vi.mock('@/cells/app/api/settingService', () => ({
    settingService: () => ({
        setSetting,
        getSettings,
        getEncryptedSettingStates: vi.fn().mockResolvedValue({})
    })
}))

const preview = vi.hoisted(() => vi.fn())
const execute = vi.hoisted(() => vi.fn())

vi.mock('@/cells/settings/services/postProcessService', () => ({
    postProcessService: () => ({
        preview: (...args: unknown[]) => preview(...args),
        execute: (...args: unknown[]) => execute(...args)
    })
}))

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

interface MountOptions {
    storedMode?: string
}

async function mountCard(options: MountOptions = {}) {
    getSettings.mockResolvedValue(
        options.storedMode ? { subtitle_postprocess_chinese_variant: options.storedMode } : {}
    )
    preview.mockReset()
    execute.mockReset()
    setSetting.mockClear()

    const pinia = createPinia()
    const wrapper = mount(PostProcessCard, {
        global: { plugins: [pinia, i18n] }
    })
    await flushPromises()
    return { wrapper, toastStore: useToastStore(pinia) }
}

function modeSelect(wrapper: Awaited<ReturnType<typeof mountCard>>['wrapper']) {
    return wrapper.find('[data-test="postprocess-mode"]')
}

async function pickMode(
    wrapper: Awaited<ReturnType<typeof mountCard>>['wrapper'],
    value: string
) {
    const select = modeSelect(wrapper)
    ;(select.element as HTMLSelectElement).value = value
    await select.trigger('change')
    await flushPromises()
}

describe('PostProcessCard · 繁简模式', () => {
    it('defaults to none and disables the batch entry', async () => {
        const { wrapper } = await mountCard()

        expect((modeSelect(wrapper).element as HTMLSelectElement).value).toBe('none')
        expect((wrapper.find('[data-test="postprocess-preview"]').element as HTMLButtonElement).disabled).toBe(
            true
        )
        expect((wrapper.find('[data-test="postprocess-execute"]').element as HTMLButtonElement).disabled).toBe(
            true
        )
    })

    it('loads a stored simplified mode and enables only the preview step', async () => {
        const { wrapper } = await mountCard({ storedMode: 'simplified' })

        expect((modeSelect(wrapper).element as HTMLSelectElement).value).toBe('simplified')
        expect((wrapper.find('[data-test="postprocess-preview"]').element as HTMLButtonElement).disabled).toBe(
            false
        )
        // confirm needs a preview first
        expect((wrapper.find('[data-test="postprocess-execute"]').element as HTMLButtonElement).disabled).toBe(
            true
        )
    })

    it('persists a mode change and invalidates a previous preview', async () => {
        const { wrapper } = await mountCard({ storedMode: 'simplified' })
        preview.mockResolvedValue({ mode: 'simplified', scanned: 4, wouldChange: 2 })
        await wrapper.find('[data-test="postprocess-preview"]').trigger('click')
        await flushPromises()
        expect(wrapper.find('[data-test="postprocess-preview-result"]').exists()).toBe(true)

        await pickMode(wrapper, 'traditional')

        expect(setSetting).toHaveBeenCalledWith('subtitle_postprocess_chinese_variant', 'traditional')
        expect(wrapper.find('[data-test="postprocess-preview-result"]').exists()).toBe(false)
        expect((wrapper.find('[data-test="postprocess-preview"]').element as HTMLButtonElement).disabled).toBe(
            false
        )
        expect((wrapper.find('[data-test="postprocess-execute"]').element as HTMLButtonElement).disabled).toBe(
            true
        )
    })

    it('rejects an unknown stored value back to none', async () => {
        const { wrapper } = await mountCard({ storedMode: 'sideways' })

        expect((modeSelect(wrapper).element as HTMLSelectElement).value).toBe('none')
    })
})

describe('PostProcessCard · 存量批处理', () => {
    it('shows the server count after a preview and unlocks the confirm step', async () => {
        const { wrapper } = await mountCard({ storedMode: 'simplified' })
        preview.mockResolvedValue({ mode: 'simplified', scanned: 7, wouldChange: 3 })

        await wrapper.find('[data-test="postprocess-preview"]').trigger('click')
        await flushPromises()

        expect(preview).toHaveBeenCalledTimes(1)
        expect(wrapper.find('[data-test="postprocess-preview-result"]').text()).toBe(
            en.settings.postProcess.previewResult.replace('{changed}', '3').replace('{scanned}', '7')
        )
        expect((wrapper.find('[data-test="postprocess-execute"]').element as HTMLButtonElement).disabled).toBe(
            false
        )
    })

    it('reports the honest zero-change preview', async () => {
        const { wrapper } = await mountCard({ storedMode: 'traditional' })
        preview.mockResolvedValue({ mode: 'traditional', scanned: 5, wouldChange: 0 })

        await wrapper.find('[data-test="postprocess-preview"]').trigger('click')
        await flushPromises()

        expect(wrapper.find('[data-test="postprocess-preview-result"]').text()).toContain(
            en.settings.postProcess.previewNoChange.replace('{scanned}', '5')
        )
    })

    it('surfaces a refused batch (mode none / over cap) verbatim as an error toast', async () => {
        const { wrapper, toastStore } = await mountCard({ storedMode: 'simplified' })
        preview.mockRejectedValue({
            isAxiosError: true,
            response: { data: { message: 'The library holds more than 500 subtitle files; the batch cap is 500.' } }
        })

        await wrapper.find('[data-test="postprocess-preview"]').trigger('click')
        await flushPromises()

        expect(toastStore.toasts[0].message).toContain('500')
        expect(toastStore.toasts[0].type).toBe('error')
        expect(wrapper.find('[data-test="postprocess-preview-result"]').exists()).toBe(false)
    })

    it('executes after a preview and reports the real per-file counts', async () => {
        const { wrapper, toastStore } = await mountCard({ storedMode: 'simplified' })
        preview.mockResolvedValue({ mode: 'simplified', scanned: 4, wouldChange: 2 })
        execute.mockResolvedValue({
            mode: 'simplified',
            scanned: 4,
            converted: 2,
            skipped: 2,
            failed: []
        })

        await wrapper.find('[data-test="postprocess-preview"]').trigger('click')
        await flushPromises()
        await wrapper.find('[data-test="postprocess-execute"]').trigger('click')
        await flushPromises()

        expect(execute).toHaveBeenCalledTimes(1)
        expect(wrapper.find('[data-test="postprocess-execute-result"]').text()).toBe(
            en.settings.postProcess.executed
                .replace('{converted}', '2')
                .replace('{skipped}', '2')
                .replace('{failed}', '0')
        )
        expect(toastStore.toasts[0].type).toBe('success')
    })
})
