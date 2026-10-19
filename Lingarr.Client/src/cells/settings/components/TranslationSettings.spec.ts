// @vitest-environment happy-dom
// TranslationSettings(提供商页「请求与重试」行式卡):数值字段失焦钳制(R17)。
// 设置卡无 CTA、失焦即存是有意设计 —— 钳制只发生在 InputComponent 的 blur
// 处理里,越界值夹回边界后立刻写入,防抖窗口内的原值不再原样入库。
// 行内输入走默认 1000ms 防抖(vitest 真实定时器下测试内不会触发),
// blur 钳制路径是同步 emit,故无需推进定时器。
import { beforeEach, describe, expect, it, onTestFinished, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { flushPromises, mount, VueWrapper } from '@vue/test-utils'
import TranslationSettings from '@/cells/settings/components/TranslationSettings.vue'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { ISettings, SETTINGS } from '@/types'
import en from '@/i18n/locales/en.json'

const setSettingMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/settingService', () => ({
    settingService: () => ({
        setSetting: setSettingMock,
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

type Mounted = {
    wrapper: VueWrapper
    pinia: ReturnType<typeof createPinia>
    settingStore: ReturnType<typeof useSettingStore>
    toastStore: ReturnType<typeof useToastStore>
}

async function mountCard(): Promise<Mounted> {
    const pinia = createPinia()
    const settingStore = useSettingStore(pinia)
    // service_type 是 JSON 数组串(组件内 JSON.parse);openai 属批量服务,
    // 配合 use_batch_translation=true 让 batchSize 行也渲染
    const defaults: Record<string, string> = {
        [SETTINGS.SERVICE_TYPE]: '["openai"]',
        [SETTINGS.USE_BATCH_TRANSLATION]: 'true',
        [SETTINGS.MAX_BATCH_SIZE]: '500',
        [SETTINGS.REQUEST_TIMEOUT]: '5',
        [SETTINGS.MAX_RETRIES]: '5',
        [SETTINGS.RETRY_DELAY]: '1',
        [SETTINGS.RETRY_DELAY_MULTIPLIER]: '2'
    }
    for (const [key, value] of Object.entries(defaults)) {
        settingStore.storeSetting(key as keyof ISettings, value)
    }

    const wrapper = mount(TranslationSettings, {
        global: { plugins: [pinia, i18n] }
    })
    onTestFinished(() => wrapper.unmount())
    await flushPromises()
    return { wrapper, pinia, settingStore, toastStore: useToastStore(pinia) }
}

const rowInput = (wrapper: VueWrapper, rowKey: string): HTMLInputElement => {
    const input = wrapper.find(`[data-test="req-row-${rowKey}"] input`)
    expect(input.exists()).toBe(true)
    return input.element as HTMLInputElement
}

/// 行内输入:先 input(进防抖,不落库),再 blur(触发钳制同步 emit)
async function typeAndBlur(wrapper: VueWrapper, rowKey: string, value: string): Promise<void> {
    const input = rowInput(wrapper, rowKey)
    input.value = value
    input.dispatchEvent(new Event('input'))
    await wrapper.vm.$nextTick()
    input.dispatchEvent(new Event('blur'))
    await flushPromises()
}

const clampToast = (toastStore: ReturnType<typeof useToastStore>) =>
    toastStore.toasts.find((toast) => toast.type === 'info')

beforeEach(() => {
    setSettingMock.mockClear().mockResolvedValue(undefined)
})

describe('TranslationSettings · 行渲染', () => {
    it('renders all five numeric rows for a batch-capable primary service', async () => {
        const { wrapper } = await mountCard()

        for (const key of ['batchSize', 'requestTimeout', 'maxRetries', 'retryDelay', 'retryDelayMultiplier']) {
            expect(wrapper.find(`[data-test="req-row-${key}"]`).exists()).toBe(true)
        }
        expect(rowInput(wrapper, 'batchSize').value).toBe('500')
    })

    it('hides the batch-size row when batch translation is off', async () => {
        const { wrapper, settingStore } = await mountCard()
        settingStore.storeSetting(SETTINGS.USE_BATCH_TRANSLATION, 'false')
        await flushPromises()

        expect(wrapper.find('[data-test="req-row-batchSize"]').exists()).toBe(false)
    })
})

describe('TranslationSettings · 数值失焦钳制(R17)', () => {
    it('clamps an over-limit retry count back to 10 and persists the clamped value', async () => {
        const mounted = await mountCard()

        await typeAndBlur(mounted.wrapper, 'maxRetries', '999')

        // element shows the boundary, not the typed 999
        expect(rowInput(mounted.wrapper, 'maxRetries').value).toBe('10')
        // the clamped value is what reaches the store and the API — 999 never lands
        expect(mounted.settingStore.getSetting(SETTINGS.MAX_RETRIES)).toBe('10')
        expect(setSettingMock).toHaveBeenCalledWith('max_retries', '10')
        expect(setSettingMock.mock.calls.map((call) => call[1])).not.toContain('999')
        // one info toast naming the applied range
        const toast = clampToast(mounted.toastStore)
        expect(toast?.message).toBe(en.toast.validation.clamped.replace('{range}', '0–10'))
    })

    it('clamps an under-limit batch size back to the minimum of 1', async () => {
        const mounted = await mountCard()

        await typeAndBlur(mounted.wrapper, 'batchSize', '0')

        expect(mounted.settingStore.getSetting(SETTINGS.MAX_BATCH_SIZE)).toBe('1')
        expect(setSettingMock).toHaveBeenCalledWith('max_batch_size', '1')
        expect(clampToast(mounted.toastStore)?.message).toBe(
            en.toast.validation.clamped.replace('{range}', '1–2000')
        )
    })

    it('clamps an under-minute timeout up to 1', async () => {
        const mounted = await mountCard()

        await typeAndBlur(mounted.wrapper, 'requestTimeout', '0')

        expect(mounted.settingStore.getSetting(SETTINGS.REQUEST_TIMEOUT)).toBe('1')
        expect(setSettingMock).toHaveBeenCalledWith('request_timeout', '1')
    })

    it('leaves in-range values untouched on blur — no clamp toast, no write', async () => {
        const mounted = await mountCard()

        await typeAndBlur(mounted.wrapper, 'maxRetries', '7')

        // still pending in the 1000ms debounce window: nothing persisted yet,
        // and crucially no clamp toast for an in-range value
        expect(setSettingMock).not.toHaveBeenCalled()
        expect(clampToast(mounted.toastStore)).toBeUndefined()
    })
})
