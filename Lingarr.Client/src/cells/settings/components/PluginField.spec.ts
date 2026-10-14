// @vitest-environment happy-dom
// PluginField · 敏感值统一策略(输入型)验收:Secret 字段编辑态不回显旧值
// (服务端虽回传明文,前端不渲染进 DOM),占位符说明「留空保持不变」,
// 空输入不写空串覆盖已存密钥;非 Secret 字段回显行为保持不变。
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { flushPromises, mount } from '@vue/test-utils'
import PluginField from '@/cells/settings/components/PluginField.vue'
import { useSettingStore } from '@/cells/app/stores/setting'
import { IPluginSettingField } from '@/types'
import en from '@/i18n/locales/en.json'

const setSettingMock = vi.hoisted(() => vi.fn())
const setEncryptedSettingMock = vi.hoisted(() => vi.fn())

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
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

const STORED_SECRET = 'sk-live-plaintext-secret-0123456789'

const secretField: IPluginSettingField = {
    key: 'openai_api_key',
    label: 'API key',
    type: 'Secret',
    required: true
}

const textField: IPluginSettingField = {
    key: 'openai_model',
    label: 'Model',
    type: 'Text',
    required: false,
    default: 'gpt-4o-mini'
}

async function mountField(field: IPluginSettingField) {
    const pinia = createPinia()
    const wrapper = mount(PluginField, {
        props: { field },
        global: { plugins: [pinia, i18n] }
    })
    await flushPromises()
    return { wrapper, pinia }
}

beforeEach(() => {
    setSettingMock.mockReset().mockResolvedValue(undefined)
    setEncryptedSettingMock.mockReset().mockResolvedValue(undefined)
})

afterEach(() => {
    vi.useRealTimers()
})

describe('PluginField · Secret 输入型策略', () => {
    it('renders Secret as a password input and never echoes the stored value into the DOM', async () => {
        const { wrapper, pinia } = await mountField(secretField)
        useSettingStore(pinia).encrypted_settings.openai_api_key = STORED_SECRET
        await flushPromises()

        const input = wrapper.find('input')
        expect(input.attributes('type')).toBe('password')
        // 不回显:输入框值为空,完整明文不在 DOM
        expect((input.element as HTMLInputElement).value).toBe('')
        expect(wrapper.html()).not.toContain(STORED_SECRET)
    })

    it('hints with the keep-unchanged placeholder while a secret is configured', async () => {
        const { wrapper, pinia } = await mountField(secretField)
        useSettingStore(pinia).encrypted_settings.openai_api_key = STORED_SECRET
        await flushPromises()

        expect(wrapper.find('input').attributes('placeholder')).toBe(
            en.services.plugins.secretConfigured
        )
    })

    it('falls back to the manifest default placeholder when nothing is configured', async () => {
        const { wrapper } = await mountField({ ...secretField, default: 'paste-token' })
        expect(wrapper.find('input').attributes('placeholder')).toBe('paste-token')
    })

    it('never writes when the input stays or becomes empty (keep-unchanged semantics)', async () => {
        vi.useFakeTimers()
        const { wrapper, pinia } = await mountField(secretField)
        useSettingStore(pinia).encrypted_settings.openai_api_key = STORED_SECRET
        await flushPromises()
        setEncryptedSettingMock.mockClear()

        const input = wrapper.find('input')
        // select-all + delete → 空输入 = 保持不变,不写空串覆盖
        await input.setValue('')
        await vi.advanceTimersByTimeAsync(1100)
        await flushPromises()

        expect(setEncryptedSettingMock).not.toHaveBeenCalled()
        expect(useSettingStore(pinia).getEncryptedSetting('openai_api_key' as never)).toBe(
            STORED_SECRET
        )
    })

    it('writes the new secret only once the input is valid, and emits save', async () => {
        vi.useFakeTimers()
        const { wrapper, pinia } = await mountField({ ...secretField, minLength: 20 })
        useSettingStore(pinia).encrypted_settings.openai_api_key = STORED_SECRET
        await flushPromises()
        setEncryptedSettingMock.mockClear()

        const input = wrapper.find('input')
        await input.setValue('new-secret-value-4200')
        await vi.advanceTimersByTimeAsync(1100)
        await flushPromises()

        expect(setEncryptedSettingMock).toHaveBeenCalledWith(
            'openai_api_key',
            'new-secret-value-4200'
        )
        expect(wrapper.emitted('save')).toBeTruthy()
    })
})

describe('PluginField · 非 Secret 字段零回归', () => {
    it('echoes the stored plain setting and keeps the default placeholder', async () => {
        const { wrapper, pinia } = await mountField(textField)
        useSettingStore(pinia).storeSetting('openai_model', 'gpt-4o')
        await flushPromises()

        const input = wrapper.find('input')
        expect(input.attributes('type')).toBe('text')
        expect((input.element as HTMLInputElement).value).toBe('gpt-4o')
        expect(input.attributes('placeholder')).toBe('gpt-4o-mini')
    })
})
