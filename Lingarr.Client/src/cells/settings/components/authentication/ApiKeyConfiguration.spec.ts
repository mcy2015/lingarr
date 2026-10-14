// @vitest-environment happy-dom
// ApiKeyConfiguration · 敏感值统一策略(展示型样板)验收:
// a. 卡内「API 密钥」字样恰 1 次(en/zh 两 locale);
// b. 默认无完整明文 key(DOM 断言),显 → 隐 → 掩码往返;
// c. curl 示例恒占位符,已生成 key 时也不内嵌真实值;
// d. 复制按钮把完整 key 写入剪贴板(mock clipboard);
// e. 生成/吊销/显隐零回归;
// f. 掩码态 DOM(含 html 属性)不含完整值。
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { flushPromises, mount } from '@vue/test-utils'
import ApiKeyConfiguration from '@/cells/settings/components/authentication/ApiKeyConfiguration.vue'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { ENCRYPTED_SETTINGS, SETTINGS } from '@/types'
import en from '@/i18n/locales/en.json'
import zh from '@/i18n/locales/zh-CN.json'

const generateApiKeyMock = vi.hoisted(() => vi.fn())
const revokeApiKeyMock = vi.hoisted(() => vi.fn())
const setSettingMock = vi.hoisted(() => vi.fn())
const setEncryptedSettingMock = vi.hoisted(() => vi.fn())
const writeTextMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/authService', () => ({
    authService: () => ({
        generateApiKey: generateApiKeyMock,
        revokeApiKey: revokeApiKeyMock
    })
}))

vi.mock('@/cells/app/api/settingService', () => ({
    settingService: () => ({
        setSetting: setSettingMock,
        setEncryptedSetting: setEncryptedSettingMock,
        getSettings: vi.fn().mockResolvedValue({}),
        getEncryptedSettings: vi.fn().mockResolvedValue({})
    })
}))

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en, zh },
    missingWarn: false,
    fallbackWarn: false
})

const FULL_KEY = 'ling-live-key-0123456789abcdef'

/** Installs the clipboard mock onto the happy-dom navigator. */
function stubClipboard() {
    Object.defineProperty(navigator, 'clipboard', {
        value: { writeText: writeTextMock },
        configurable: true
    })
}

async function mountCard() {
    const pinia = createPinia()
    const settingStore = useSettingStore(pinia)
    settingStore.storeSetting(SETTINGS.AUTH_ENABLED, 'true')
    settingStore.encrypted_settings[ENCRYPTED_SETTINGS.API_KEY] = FULL_KEY

    const wrapper = mount(ApiKeyConfiguration, {
        global: { plugins: [pinia, i18n] }
    })
    await flushPromises()
    return { wrapper, pinia }
}

function occurrences(haystack: string, needle: string): number {
    return haystack.split(needle).length - 1
}

beforeEach(() => {
    generateApiKeyMock.mockReset().mockResolvedValue({ apiKey: 'brand-new-key-9876543210fedcba' })
    revokeApiKeyMock.mockReset().mockResolvedValue(undefined)
    setSettingMock.mockReset().mockResolvedValue(undefined)
    setEncryptedSettingMock.mockReset().mockResolvedValue(undefined)
    writeTextMock.mockReset().mockResolvedValue(undefined)
    stubClipboard()
    i18n.global.locale.value = 'en'
})

afterEach(() => {
    i18n.global.locale.value = 'en'
})

describe('ApiKeyConfiguration · a) 标题唯一', () => {
    it('renders the card title exactly once (en)', async () => {
        const { wrapper } = await mountCard()
        const card = wrapper.find('[data-test="apikey-card"]')
        expect(card.exists()).toBe(true)
        expect(occurrences(card.text(), 'API Key')).toBe(1)
    })

    it('renders the card title exactly once (zh)', async () => {
        i18n.global.locale.value = 'zh'
        const { wrapper } = await mountCard()
        const card = wrapper.find('[data-test="apikey-card"]')
        expect(occurrences(card.text(), 'API 密钥')).toBe(1)
        // 行内重复 label 已移除:卡内不再有第二个「密钥」字样的值行标签
        expect(card.find('b').exists()).toBe(false)
    })

    it('carries the generated / not-generated status semantics instead of a duplicate label', async () => {
        const { wrapper } = await mountCard()
        expect(wrapper.find('[data-test="apikey-status"]').text()).toBe(
            en.settings.apiKey.statusGenerated
        )

        const pinia = createPinia()
        const settingStore = useSettingStore(pinia)
        settingStore.storeSetting(SETTINGS.AUTH_ENABLED, 'true')
        settingStore.encrypted_settings[ENCRYPTED_SETTINGS.API_KEY] = ''
        const emptyCard = mount(ApiKeyConfiguration, { global: { plugins: [pinia, i18n] } })
        await flushPromises()
        expect(emptyCard.find('[data-test="apikey-status"]').text()).toBe(
            en.settings.apiKey.statusNotGenerated
        )
    })
})

describe('ApiKeyConfiguration · b/f) 默认掩码与显隐往返', () => {
    it('masks by default and keeps the full key out of the DOM', async () => {
        const { wrapper } = await mountCard()

        const value = wrapper.find('[data-test="apikey-value"]')
        expect(value.text()).toBe('ling•••cdef')
        // f) 掩码态 DOM 断言:渲染文本与整个 html(含属性)均无完整明文
        expect(value.text()).not.toContain(FULL_KEY)
        expect(wrapper.html()).not.toContain(FULL_KEY)
    })

    it('fully masks short keys', async () => {
        const pinia = createPinia()
        const settingStore = useSettingStore(pinia)
        settingStore.storeSetting(SETTINGS.AUTH_ENABLED, 'true')
        settingStore.encrypted_settings[ENCRYPTED_SETTINGS.API_KEY] = 'shortkey'
        const wrapper = mount(ApiKeyConfiguration, { global: { plugins: [pinia, i18n] } })
        await flushPromises()

        expect(wrapper.find('[data-test="apikey-value"]').text()).toBe('••••••••')
        expect(wrapper.html()).not.toContain('shortkey')
    })

    it('round-trips reveal → hide → masked without leaking in the masked state', async () => {
        const { wrapper } = await mountCard()

        // 显
        await wrapper.find('[data-test="apikey-visibility"]').trigger('click')
        expect(wrapper.find('[data-test="apikey-value"]').text()).toBe(FULL_KEY)

        // 隐 → 掩码
        await wrapper.find('[data-test="apikey-visibility"]').trigger('click')
        expect(wrapper.find('[data-test="apikey-value"]').text()).toBe('ling•••cdef')
        expect(wrapper.html()).not.toContain(FULL_KEY)
    })
})

describe('ApiKeyConfiguration · c) curl 恒占位符', () => {
    it('keeps the placeholder token in the usage example even with a live key', async () => {
        const { wrapper } = await mountCard()

        await wrapper.find('[data-test="apikey-usage-fold"]').trigger('click')
        const curl = wrapper.find('[data-test="apikey-curl"]')
        expect(curl.exists()).toBe(true)
        expect(curl.text()).toContain('YOUR_API_KEY')
        expect(curl.text()).not.toContain(FULL_KEY)

        // 已显隐状态下示例仍不内嵌真实值
        await wrapper.find('[data-test="apikey-visibility"]').trigger('click')
        expect(wrapper.find('[data-test="apikey-curl"]').text()).toContain('YOUR_API_KEY')
        expect(wrapper.find('[data-test="apikey-curl"]').text()).not.toContain(FULL_KEY)
    })
})

describe('ApiKeyConfiguration · d) 复制写入完整 key', () => {
    it('copies the full key to the clipboard and toasts', async () => {
        const { wrapper, pinia } = await mountCard()
        const toastStore = useToastStore(pinia)

        await wrapper.find('[data-test="apikey-copy"]').trigger('click')
        await flushPromises()

        expect(writeTextMock).toHaveBeenCalledTimes(1)
        expect(writeTextMock).toHaveBeenCalledWith(FULL_KEY)
        expect(toastStore.toasts[0].message).toBe(en.settings.apiKey.copied)
    })

    it('does nothing without a key', async () => {
        const pinia = createPinia()
        const settingStore = useSettingStore(pinia)
        settingStore.storeSetting(SETTINGS.AUTH_ENABLED, 'true')
        settingStore.encrypted_settings[ENCRYPTED_SETTINGS.API_KEY] = ''
        const wrapper = mount(ApiKeyConfiguration, { global: { plugins: [pinia, i18n] } })
        await flushPromises()

        expect(wrapper.find('[data-test="apikey-copy"]').exists()).toBe(false)
        expect(writeTextMock).not.toHaveBeenCalled()
    })
})

describe('ApiKeyConfiguration · e) 生成/吊销零回归', () => {
    it('generates through the auth endpoint, stores the key and stays masked', async () => {
        const { wrapper, pinia } = await mountCard()

        await wrapper.find('[data-test="apikey-generate"]').trigger('click')
        await flushPromises()

        expect(generateApiKeyMock).toHaveBeenCalledTimes(1)
        expect(useSettingStore(pinia).getEncryptedSetting(ENCRYPTED_SETTINGS.API_KEY)).toBe(
            'brand-new-key-9876543210fedcba'
        )
        // 生成后默认仍是掩码态
        expect(wrapper.find('[data-test="apikey-value"]').text()).toBe('bran•••dcba')
        expect(wrapper.html()).not.toContain('brand-new-key-9876543210fedcba')
    })

    it('shows the revoke button only while a key exists', async () => {
        const { wrapper, pinia } = await mountCard()
        expect(wrapper.find('[data-test="apikey-revoke"]').exists()).toBe(true)

        useSettingStore(pinia).encrypted_settings[ENCRYPTED_SETTINGS.API_KEY] = ''
        await flushPromises()
        expect(wrapper.find('[data-test="apikey-revoke"]').exists()).toBe(false)
    })

    it('revokes through the auth endpoint, clears the stored key and hides the value row', async () => {
        const { wrapper, pinia } = await mountCard()

        await wrapper.find('[data-test="apikey-revoke"]').trigger('click')
        await flushPromises()

        expect(revokeApiKeyMock).toHaveBeenCalledTimes(1)
        const written = setEncryptedSettingMock.mock.calls.find(
            (call) => call[0] === ENCRYPTED_SETTINGS.API_KEY
        )
        expect(written?.[1]).toBe('')
        expect(useSettingStore(pinia).getEncryptedSetting(ENCRYPTED_SETTINGS.API_KEY)).toBe('')
        expect(wrapper.find('[data-test="apikey-status"]').text()).toBe(
            en.settings.apiKey.statusNotGenerated
        )
        expect(wrapper.find('[data-test="apikey-value"]').text()).toBe('—')
        expect(wrapper.find('[data-test="apikey-visibility"]').exists()).toBe(false)
    })

    it('hides the whole card while auth is disabled', async () => {
        const pinia = createPinia()
        const settingStore = useSettingStore(pinia)
        settingStore.storeSetting(SETTINGS.AUTH_ENABLED, 'false')
        settingStore.encrypted_settings[ENCRYPTED_SETTINGS.API_KEY] = FULL_KEY
        const wrapper = mount(ApiKeyConfiguration, { global: { plugins: [pinia, i18n] } })
        await flushPromises()
        expect(wrapper.find('[data-test="apikey-card"]').exists()).toBe(false)
    })
})
