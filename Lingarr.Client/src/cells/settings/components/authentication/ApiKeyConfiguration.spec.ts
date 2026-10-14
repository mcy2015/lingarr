// @vitest-environment happy-dom
// ApiKeyConfiguration · 敏感值统一策略(展示型样板)验收:
// a. 卡内「API 密钥」字样恰 1 次(en/zh 两 locale),状态语义来自布尔;
// b. 挂载态只有布尔(已生成/未生成,无值):store 不含明文,值行显示通用掩码;
// c. 「显示密钥」= 显式动作:调 GET /api/auth/apikey 取值 → 本会话可显/隐/复制,
//    显隐往返不再请求;掩码态 DOM(含 html 属性)不含完整明文;
// d. curl 示例恒占位符,已取值时也不内嵌真实值;
// e. 复制按钮把完整 key 写入剪贴板(mock clipboard),未取值时无复制按钮;
// f. 生成响应直接带回值(默认掩码);吊销回未生成并丢弃会话值;
// g. 未取值时无值可显示、显示按钮调端点;密钥被别处吊销时静默回未生成。
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { flushPromises, mount } from '@vue/test-utils'
import type { VueWrapper } from '@vue/test-utils'
import ApiKeyConfiguration from '@/cells/settings/components/authentication/ApiKeyConfiguration.vue'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { ENCRYPTED_SETTINGS, SETTINGS } from '@/types'
import en from '@/i18n/locales/en.json'
import zh from '@/i18n/locales/zh-CN.json'

const generateApiKeyMock = vi.hoisted(() => vi.fn())
const getApiKeyMock = vi.hoisted(() => vi.fn())
const revokeApiKeyMock = vi.hoisted(() => vi.fn())
const setSettingMock = vi.hoisted(() => vi.fn())
const setEncryptedSettingMock = vi.hoisted(() => vi.fn())
const writeTextMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/authService', () => ({
    authService: () => ({
        generateApiKey: generateApiKeyMock,
        getApiKey: getApiKeyMock,
        revokeApiKey: revokeApiKeyMock
    })
}))

vi.mock('@/cells/app/api/settingService', () => ({
    settingService: () => ({
        setSetting: setSettingMock,
        setEncryptedSetting: setEncryptedSettingMock,
        getSettings: vi.fn().mockResolvedValue({}),
        getEncryptedSettingStates: vi.fn().mockResolvedValue({})
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
const GENERIC_MASK = '••••••••'

/** Installs the clipboard mock onto the happy-dom navigator. */
function stubClipboard() {
    Object.defineProperty(navigator, 'clipboard', {
        value: { writeText: writeTextMock },
        configurable: true
    })
}

async function mountCard(configured = true) {
    const pinia = createPinia()
    const settingStore = useSettingStore(pinia)
    settingStore.storeSetting(SETTINGS.AUTH_ENABLED, 'true')
    settingStore.encrypted_settings[ENCRYPTED_SETTINGS.API_KEY] = configured

    const wrapper = mount(ApiKeyConfiguration, {
        global: { plugins: [pinia, i18n] }
    })
    await flushPromises()
    return { wrapper, pinia }
}

/** Reveals the key once through the explicit endpoint fetch. */
async function revealKey(wrapper: VueWrapper) {
    getApiKeyMock.mockResolvedValueOnce({ apiKey: FULL_KEY })
    await wrapper.find('[data-test="apikey-visibility"]').trigger('click')
    await flushPromises()
}

function occurrences(haystack: string, needle: string): number {
    return haystack.split(needle).length - 1
}

beforeEach(() => {
    generateApiKeyMock.mockReset().mockResolvedValue({ apiKey: 'brand-new-key-9876543210fedcba' })
    getApiKeyMock.mockReset().mockResolvedValue({ apiKey: FULL_KEY })
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

describe('ApiKeyConfiguration · a) 标题唯一与状态语义', () => {
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

    it('derives the generated / not-generated status from the boolean flag only', async () => {
        const { wrapper } = await mountCard(true)
        expect(wrapper.find('[data-test="apikey-status"]').text()).toBe(
            en.settings.apiKey.statusGenerated
        )

        const empty = await mountCard(false)
        expect(empty.wrapper.find('[data-test="apikey-status"]').text()).toBe(
            en.settings.apiKey.statusNotGenerated
        )
    })

    it('carries no plaintext in the store after mount (boolean states only)', async () => {
        const { pinia } = await mountCard(true)
        // 挂载态来自布尔:store 里没有可读的密钥明文
        const store = useSettingStore(pinia)
        expect(store.encrypted_settings[ENCRYPTED_SETTINGS.API_KEY]).toBe(true)
        expect(JSON.stringify(store.encrypted_settings)).not.toContain(FULL_KEY)
    })
})

describe('ApiKeyConfiguration · b/c) 未取值会话:显示按钮调端点', () => {
    it('shows the generic mask with no value on mount and does not call the endpoint', async () => {
        const { wrapper } = await mountCard(true)

        expect(wrapper.find('[data-test="apikey-value"]').text()).toBe(GENERIC_MASK)
        expect(wrapper.html()).not.toContain(FULL_KEY)
        expect(getApiKeyMock).not.toHaveBeenCalled()
        // 未取值 → 无值可复制
        expect(wrapper.find('[data-test="apikey-copy"]').exists()).toBe(false)
    })

    it('fetches the key through the reveal endpoint on the first show click', async () => {
        const { wrapper } = await mountCard(true)

        await revealKey(wrapper)

        expect(getApiKeyMock).toHaveBeenCalledTimes(1)
        expect(wrapper.find('[data-test="apikey-value"]').text()).toBe(FULL_KEY)
    })

    it('round-trips reveal → hide → masked without re-requesting the endpoint', async () => {
        const { wrapper } = await mountCard(true)

        await revealKey(wrapper)
        // 隐 → 掩码(真实值掩码:前4+•••+后4)
        await wrapper.find('[data-test="apikey-visibility"]').trigger('click')
        expect(wrapper.find('[data-test="apikey-value"]').text()).toBe('ling•••cdef')
        expect(wrapper.html()).not.toContain(FULL_KEY)

        // 再显 → 本会话内纯切换,不再调端点
        await wrapper.find('[data-test="apikey-visibility"]').trigger('click')
        expect(wrapper.find('[data-test="apikey-value"]').text()).toBe(FULL_KEY)
        expect(getApiKeyMock).toHaveBeenCalledTimes(1)
    })

    it('fully masks short keys in the masked state of a revealed session', async () => {
        const { wrapper } = await mountCard(true)
        getApiKeyMock.mockResolvedValueOnce({ apiKey: 'shortkey' })

        await wrapper.find('[data-test="apikey-visibility"]').trigger('click')
        await flushPromises()
        await wrapper.find('[data-test="apikey-visibility"]').trigger('click')

        expect(wrapper.find('[data-test="apikey-value"]').text()).toBe(GENERIC_MASK)
        expect(wrapper.html()).not.toContain('shortkey')
    })

    it('silently falls back to not-generated when the endpoint answers an empty key', async () => {
        const { wrapper, pinia } = await mountCard(true)
        getApiKeyMock.mockResolvedValueOnce({ apiKey: '' })

        await wrapper.find('[data-test="apikey-visibility"]').trigger('click')
        await flushPromises()

        expect(wrapper.find('[data-test="apikey-status"]').text()).toBe(
            en.settings.apiKey.statusNotGenerated
        )
        expect(wrapper.find('[data-test="apikey-value"]').text()).toBe('—')
        expect(useSettingStore(pinia).encrypted_settings[ENCRYPTED_SETTINGS.API_KEY]).toBe(false)
    })
})

describe('ApiKeyConfiguration · d) curl 恒占位符', () => {
    it('keeps the placeholder token in the usage example even with a revealed key', async () => {
        const { wrapper } = await mountCard(true)

        await wrapper.find('[data-test="apikey-usage-fold"]').trigger('click')
        const curl = wrapper.find('[data-test="apikey-curl"]')
        expect(curl.exists()).toBe(true)
        expect(curl.text()).toContain('YOUR_API_KEY')
        expect(curl.text()).not.toContain(FULL_KEY)

        // 已取值会话的显隐状态下示例仍不内嵌真实值
        await revealKey(wrapper)
        expect(wrapper.find('[data-test="apikey-curl"]').text()).toContain('YOUR_API_KEY')
        expect(wrapper.find('[data-test="apikey-curl"]').text()).not.toContain(FULL_KEY)
    })
})

describe('ApiKeyConfiguration · e) 复制写入完整 key(已取值会话)', () => {
    it('copies the full key to the clipboard from the masked state and toasts', async () => {
        const { wrapper, pinia } = await mountCard(true)
        await revealKey(wrapper)
        // 隐回掩码态再复制:值仅经剪贴板离开
        await wrapper.find('[data-test="apikey-visibility"]').trigger('click')
        const toastStore = useToastStore(pinia)

        await wrapper.find('[data-test="apikey-copy"]').trigger('click')
        await flushPromises()

        expect(writeTextMock).toHaveBeenCalledTimes(1)
        expect(writeTextMock).toHaveBeenCalledWith(FULL_KEY)
        expect(toastStore.toasts[0].message).toBe(en.settings.apiKey.copied)
        // 复制后仍处掩码态,DOM 无明文
        expect(wrapper.find('[data-test="apikey-value"]').text()).toBe('ling•••cdef')
        expect(wrapper.html()).not.toContain(FULL_KEY)
    })

    it('offers no copy button before the key is fetched, and none without a key', async () => {
        const { wrapper } = await mountCard(true)
        expect(wrapper.find('[data-test="apikey-copy"]').exists()).toBe(false)

        const empty = await mountCard(false)
        expect(empty.wrapper.find('[data-test="apikey-copy"]').exists()).toBe(false)
        expect(writeTextMock).not.toHaveBeenCalled()
    })
})

describe('ApiKeyConfiguration · f) 生成/吊销零回归', () => {
    it('generates through the auth endpoint: value from the response, masked, state true', async () => {
        const { wrapper, pinia } = await mountCard(false)

        await wrapper.find('[data-test="apikey-generate"]').trigger('click')
        await flushPromises()

        expect(generateApiKeyMock).toHaveBeenCalledTimes(1)
        const settingStore = useSettingStore(pinia)
        // 生成响应直接带回值:store 只有布尔状态,值仅在本会话组件内
        expect(settingStore.encrypted_settings[ENCRYPTED_SETTINGS.API_KEY]).toBe(true)
        expect(JSON.stringify(settingStore.encrypted_settings)).not.toContain('brand-new-key')
        // 生成后默认仍是掩码态,值行直接可掩码显示(无需再调读取端点)
        expect(wrapper.find('[data-test="apikey-value"]').text()).toBe('bran•••dcba')
        expect(wrapper.html()).not.toContain('brand-new-key-9876543210fedcba')
        expect(getApiKeyMock).not.toHaveBeenCalled()
        // 生成后的会话值可直接复制
        expect(wrapper.find('[data-test="apikey-copy"]').exists()).toBe(true)
    })

    it('shows the revoke button only while the boolean says a key exists', async () => {
        const { wrapper, pinia } = await mountCard(true)
        expect(wrapper.find('[data-test="apikey-revoke"]').exists()).toBe(true)

        useSettingStore(pinia).encrypted_settings[ENCRYPTED_SETTINGS.API_KEY] = false
        await flushPromises()
        expect(wrapper.find('[data-test="apikey-revoke"]').exists()).toBe(false)
    })

    it('revokes through the auth endpoint: back to not-generated, session value dropped', async () => {
        const { wrapper, pinia } = await mountCard(true)
        await revealKey(wrapper)
        expect(wrapper.find('[data-test="apikey-value"]').text()).toBe(FULL_KEY)

        await wrapper.find('[data-test="apikey-revoke"]').trigger('click')
        await flushPromises()

        expect(revokeApiKeyMock).toHaveBeenCalledTimes(1)
        const settingStore = useSettingStore(pinia)
        expect(settingStore.encrypted_settings[ENCRYPTED_SETTINGS.API_KEY]).toBe(false)
        expect(wrapper.find('[data-test="apikey-status"]').text()).toBe(
            en.settings.apiKey.statusNotGenerated
        )
        expect(wrapper.find('[data-test="apikey-value"]').text()).toBe('—')
        expect(wrapper.find('[data-test="apikey-visibility"]').exists()).toBe(false)
        expect(wrapper.find('[data-test="apikey-copy"]').exists()).toBe(false)
        // 吊销后密钥明文不再出现在 DOM
        expect(wrapper.html()).not.toContain(FULL_KEY)
    })

    it('hides the whole card while auth is disabled', async () => {
        const pinia = createPinia()
        const settingStore = useSettingStore(pinia)
        settingStore.storeSetting(SETTINGS.AUTH_ENABLED, 'false')
        settingStore.encrypted_settings[ENCRYPTED_SETTINGS.API_KEY] = true
        const wrapper = mount(ApiKeyConfiguration, { global: { plugins: [pinia, i18n] } })
        await flushPromises()
        expect(wrapper.find('[data-test="apikey-card"]').exists()).toBe(false)
    })
})
