// @vitest-environment happy-dom
// WhisperCard (B04): the three-field wiring surface of the whisper
// transcription fallback — configured-state dot, endpoint/model persisting
// through settingService on blur, the key contract (password field, never
// echoes the stored value, blur/Enter commit, empty keeps, reveal on
// demand), and the connection test (disabled until fully configured, posts
// through subtitleSearchService.testConnection, honest ok/fail rendering).
import { beforeEach, describe, expect, it, onTestFinished, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import WhisperCard from '@/cells/settings/components/WhisperCard.vue'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { SETTINGS } from '@/types'
import en from '@/i18n/locales/en.json'

const setSettingMock = vi.hoisted(() => vi.fn().mockResolvedValue(undefined))
const setEncryptedSettingMock = vi.hoisted(() => vi.fn().mockResolvedValue(undefined))
const revealEncryptedSettingMock = vi.hoisted(() => vi.fn().mockResolvedValue(''))
const testConnectionMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/settingService', () => ({
    settingService: () => ({
        setSetting: setSettingMock,
        setEncryptedSetting: setEncryptedSettingMock,
        getSettings: vi.fn().mockResolvedValue({}),
        getEncryptedSettingStates: vi.fn().mockResolvedValue({}),
        revealEncryptedSetting: revealEncryptedSettingMock
    })
}))

vi.mock('@/cells/app/api/subtitleSearchService', () => ({
    subtitleSearchService: {
        testConnection: testConnectionMock
    }
}))

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

interface MountOptions {
    endpoint?: string
    model?: string
    keyConfigured?: boolean
}

async function mountCard(options: MountOptions = {}) {
    const pinia = createPinia()
    const settingStore = useSettingStore(pinia)
    settingStore.storeSetting(SETTINGS.WHISPER_ENDPOINT, options.endpoint ?? '')
    settingStore.storeSetting(SETTINGS.WHISPER_MODEL, options.model ?? '')
    settingStore.encrypted_settings.whisper_api_key = options.keyConfigured ?? false

    const wrapper = mount(WhisperCard, {
        global: { plugins: [pinia, i18n] }
    })
    onTestFinished(() => wrapper.unmount())
    await flushPromises()
    return { wrapper, pinia, toastStore: useToastStore(pinia) }
}

/// The card's three inputs in order: endpoint, model, key.
const inputs = (wrapper: VueWrapper): HTMLInputElement[] =>
    wrapper.findAll('input').map((input) => input.element as HTMLInputElement)

describe('WhisperCard · 配置态', () => {
    beforeEach(() => {
        setSettingMock.mockClear()
        testConnectionMock.mockReset()
    })

    it('shows the unconfigured dot when any of the three settings is missing', async () => {
        const { wrapper } = await mountCard()
        expect(wrapper.find('[data-test="whisper-dot"]').classes()).toContain('bg-line')

        const partial = await mountCard({ endpoint: 'http://whisper:8000/v1' })
        expect(partial.wrapper.find('[data-test="whisper-dot"]').classes()).toContain('bg-line')

        const full = await mountCard({
            endpoint: 'http://whisper:8000/v1',
            model: 'whisper-1',
            keyConfigured: true
        })
        expect(full.wrapper.find('[data-test="whisper-dot"]').classes()).toContain('bg-success')
    })

    it('echoes the stored endpoint and model, never the stored key', async () => {
        const { wrapper } = await mountCard({
            endpoint: 'http://whisper:8000/v1',
            model: 'whisper-1',
            keyConfigured: true
        })

        const [endpoint, model, key] = inputs(wrapper)
        expect(endpoint.value).toBe('http://whisper:8000/v1')
        expect(model.value).toBe('whisper-1')
        // sensitive-value policy: password type, empty value, placeholder
        // explains "leave empty to keep"
        expect(key.type).toBe('password')
        expect(key.value).toBe('')
        expect(key.placeholder).toBe(en.settings.whisper.keySet)
    })
})

describe('WhisperCard · 保存契约', () => {
    beforeEach(() => {
        setSettingMock.mockClear()
        setEncryptedSettingMock.mockClear()
        revealEncryptedSettingMock.mockClear().mockResolvedValue('')
    })

    it('persists endpoint and model on blur through settingService', async () => {
        const { wrapper, toastStore } = await mountCard()

        const [endpoint, model] = inputs(wrapper)
        endpoint.value = 'http://whisper:8000/v1'
        endpoint.dispatchEvent(new Event('input'))
        endpoint.dispatchEvent(new Event('blur'))
        await flushPromises()

        expect(setSettingMock).toHaveBeenCalledWith('whisper_endpoint', 'http://whisper:8000/v1')
        expect(toastStore.toasts[0].message).toBe(en.common.saved)

        model.value = 'whisper-1'
        model.dispatchEvent(new Event('input'))
        model.dispatchEvent(new Event('blur'))
        await flushPromises()

        expect(setSettingMock).toHaveBeenCalledWith('whisper_model', 'whisper-1')
    })

    it('key: blur commits through the encrypted store, empty keeps the stored value', async () => {
        const { wrapper } = await mountCard({ keyConfigured: true })

        const key = inputs(wrapper)[2]
        key.value = 'whisper-key-abc'
        key.dispatchEvent(new Event('input'))
        await flushPromises()
        expect(setEncryptedSettingMock).not.toHaveBeenCalled()

        key.dispatchEvent(new Event('blur'))
        await flushPromises()

        expect(setEncryptedSettingMock).toHaveBeenCalledTimes(1)
        expect(setEncryptedSettingMock).toHaveBeenCalledWith('whisper_api_key', 'whisper-key-abc')
        expect(key.value).toBe('')

        // an empty blur never overwrites the stored secret
        key.dispatchEvent(new Event('blur'))
        await flushPromises()
        expect(setEncryptedSettingMock).toHaveBeenCalledTimes(1)
    })

    it('key reveal: fetches the stored key on demand and hides again without a second fetch', async () => {
        revealEncryptedSettingMock.mockResolvedValue('revealed-key')
        const { wrapper } = await mountCard({ keyConfigured: true })

        const visibility = wrapper.find('[data-test="whisper-reveal"]')
        await visibility.trigger('click')
        await flushPromises()

        expect(revealEncryptedSettingMock).toHaveBeenCalledWith('whisper_api_key')
        const key = inputs(wrapper)[2]
        expect(key.type).toBe('text')
        expect(key.value).toBe('revealed-key')

        await wrapper.find('[data-test="whisper-reveal"]').trigger('click')
        await flushPromises()

        expect(revealEncryptedSettingMock).toHaveBeenCalledTimes(1)
        expect(inputs(wrapper)[2].type).toBe('password')
        expect(inputs(wrapper)[2].value).toBe('')
    })

    it('no reveal button while no key is configured', async () => {
        const { wrapper } = await mountCard()
        expect(wrapper.find('[data-test="whisper-reveal"]').exists()).toBe(false)
    })
})

describe('WhisperCard · 连接测试', () => {
    beforeEach(() => {
        setSettingMock.mockClear()
        testConnectionMock.mockReset()
    })

    it('stays disabled until endpoint, model and key are all present', async () => {
        const { wrapper } = await mountCard({ endpoint: 'http://whisper:8000/v1' })
        expect(wrapper.find('[data-test="whisper-test"]').attributes('disabled')).toBeDefined()

        const ready = await mountCard({
            endpoint: 'http://whisper:8000/v1',
            model: 'whisper-1',
            keyConfigured: true
        })
        expect(ready.wrapper.find('[data-test="whisper-test"]').attributes('disabled')).toBeUndefined()
    })

    it('renders the ok verdict of the provider test', async () => {
        testConnectionMock.mockResolvedValue({
            ok: true,
            latencyMs: 42,
            message: 'Whisper endpoint reachable (model list answered).'
        })
        const { wrapper } = await mountCard({
            endpoint: 'http://whisper:8000/v1',
            model: 'whisper-1',
            keyConfigured: true
        })

        await wrapper.find('[data-test="whisper-test"]').trigger('click')
        await flushPromises()

        expect(testConnectionMock).toHaveBeenCalledWith('whisper')
        expect(wrapper.find('[data-test="whisper-test-ok"]').text()).toContain('reachable')
    })

    it('renders the failure verdict when the test answers ok=false', async () => {
        testConnectionMock.mockResolvedValue({
            ok: false,
            latencyMs: 5,
            message: 'whisper rejected the credentials (HTTP 401).'
        })
        const { wrapper } = await mountCard({
            endpoint: 'http://whisper:8000/v1',
            model: 'whisper-1',
            keyConfigured: true
        })

        await wrapper.find('[data-test="whisper-test"]').trigger('click')
        await flushPromises()

        expect(wrapper.find('[data-test="whisper-test-failed"]').text()).toContain('401')
    })
})
