// @vitest-environment happy-dom
// PluginField · 敏感值统一策略(输入型)验收:Secret 字段编辑态不回显旧值
// (加密设置批量读取只回「已配置」布尔,前端从头就没有明文),占位符说明
// 「留空保持不变」,空输入不写空串覆盖已存密钥;非 Secret 字段回显行为不变。
// 「显示」为显式动作:经按 key 的 reveal 端点取回明文,文本态可编辑保存。
// 凭据表单交互完整修复批:#1B 框内单眼睛(trailing 插槽)、#2 真成功才
// emit('save')、#3 清除凭据、#5 reveal 在途守卫、#7 secret 无验证图标、
// #8 blur/Enter 显式提交(半截输入零写请求)。
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { flushPromises, mount } from '@vue/test-utils'
import PluginField from '@/cells/settings/components/PluginField.vue'
import ValidationIcon from '@/cells/kernel/components/ValidationIcon.vue'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { IPluginSettingField } from '@/types'
import en from '@/i18n/locales/en.json'

const setSettingMock = vi.hoisted(() => vi.fn())
const setEncryptedSettingMock = vi.hoisted(() => vi.fn())
const revealEncryptedSettingMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/settingService', () => ({
    settingService: () => ({
        setSetting: setSettingMock,
        setEncryptedSetting: setEncryptedSettingMock,
        getSettings: vi.fn().mockResolvedValue({}),
        getEncryptedSettingStates: vi.fn().mockResolvedValue({}),
        revealEncryptedSetting: revealEncryptedSettingMock
    })
}))

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

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

async function mountField(field: IPluginSettingField, secretConfigured = false) {
    const pinia = createPinia()
    if (secretConfigured) {
        useSettingStore(pinia).encrypted_settings.openai_api_key = true
    }
    const wrapper = mount(PluginField, {
        props: { field },
        global: { plugins: [pinia, i18n] }
    })
    await flushPromises()
    return { wrapper, pinia }
}

let confirmMock: ReturnType<typeof vi.fn> | null = null

function stubConfirm(accept: boolean): void {
    // happy-dom 未实现 window.confirm —— 以全局 stub 提供之
    confirmMock = vi.fn().mockReturnValue(accept)
    vi.stubGlobal('confirm', confirmMock)
}

beforeEach(() => {
    setSettingMock.mockReset().mockResolvedValue(undefined)
    setEncryptedSettingMock.mockReset().mockResolvedValue(undefined)
    revealEncryptedSettingMock.mockReset().mockResolvedValue('')
})

afterEach(() => {
    confirmMock = null
    vi.unstubAllGlobals()
    vi.useRealTimers()
})

describe('PluginField · Secret 输入型策略', () => {
    it('renders Secret as a password input starting empty (no value exists client-side)', async () => {
        const { wrapper } = await mountField(secretField, true)

        const input = wrapper.find('input')
        expect(input.attributes('type')).toBe('password')
        // 不回显:输入框恒从空开始 —— store 里只有布尔状态,无明文可渲染
        expect((input.element as HTMLInputElement).value).toBe('')
    })

    it('hints with the keep-unchanged placeholder while the secret is configured', async () => {
        const { wrapper } = await mountField(secretField, true)

        expect(wrapper.find('input').attributes('placeholder')).toBe(
            en.services.plugins.secretConfigured
        )
    })

    it('falls back to the manifest default placeholder when nothing is configured', async () => {
        const { wrapper } = await mountField({ ...secretField, default: 'paste-token' })
        expect(wrapper.find('input').attributes('placeholder')).toBe('paste-token')
    })

    it('never writes when the input stays or becomes empty (keep-unchanged semantics)', async () => {
        const { wrapper, pinia } = await mountField(secretField, true)
        setEncryptedSettingMock.mockClear()

        const input = wrapper.find('input')
        // select-all + delete → blur 提交空输入 = 保持不变,不写空串覆盖
        await input.setValue('')
        await input.trigger('blur')
        await flushPromises()

        expect(setEncryptedSettingMock).not.toHaveBeenCalled()
        expect(
            useSettingStore(pinia).isEncryptedSettingConfigured('openai_api_key' as never)
        ).toBe(true)
    })

    it('writes the new secret only once the input is valid, flips the flag, and emits save', async () => {
        const { wrapper, pinia } = await mountField({ ...secretField, minLength: 20 }, true)
        setEncryptedSettingMock.mockClear()

        const input = wrapper.find('input')
        await input.setValue('new-secret-value-4200')
        await input.trigger('blur')
        await flushPromises()

        expect(setEncryptedSettingMock).toHaveBeenCalledWith(
            'openai_api_key',
            'new-secret-value-4200'
        )
        // 写入后本地只记「已配置」布尔,不存明文
        expect(
            useSettingStore(pinia).isEncryptedSettingConfigured('openai_api_key' as never)
        ).toBe(true)
        expect(wrapper.emitted('save')).toBeTruthy()
        // #8:blur 保存后既有语义保持 —— 值清空,占位符切「已配置,留空保持不变」
        expect((input.element as HTMLInputElement).value).toBe('')
        expect(input.attributes('placeholder')).toBe(en.services.plugins.secretConfigured)
    })
})

describe('PluginField · 单眼睛与无验证图标(#1B/#7)', () => {
    it('keeps exactly one eye button inside the box: the server reveal, no local toggle', async () => {
        const { wrapper } = await mountField(secretField, true)

        // 框内(password form)只有 reveal 一颗按钮 —— 本地显隐不渲染
        expect(wrapper.findAll('form button')).toHaveLength(1)
        expect(wrapper.findAll('[data-test="secret-visibility-openai_api_key"]')).toHaveLength(1)
        // 框外不再有第二颗眼睛按钮(旧版复制版 reveal 已移除)
        expect(wrapper.findAll('button')).toHaveLength(2) // reveal + clear credential
    })

    it('renders no eye button at all when the secret is not configured', async () => {
        const { wrapper } = await mountField(secretField, false)

        expect(wrapper.findAll('form button')).toHaveLength(0)
        expect(wrapper.find('[data-test="secret-visibility-openai_api_key"]').exists()).toBe(false)
    })

    it('hides the validation icon on secret fields (outcome lives in badge/toast)', async () => {
        const { wrapper } = await mountField({ ...secretField, minLength: 20 }, true)
        expect(wrapper.findComponent(ValidationIcon).exists()).toBe(false)
    })

    it('keeps the validation icon on non-secret validated fields', async () => {
        const { wrapper } = await mountField({ ...textField, minLength: 3 })
        expect(wrapper.findComponent(ValidationIcon).exists()).toBe(true)
    })
})

describe('PluginField · 显式提交时序(#2/#8)', () => {
    it('does not write or toast while the user pauses mid-input — commit waits for blur/Enter', async () => {
        vi.useFakeTimers()
        const { wrapper, pinia } = await mountField(secretField, true)
        setEncryptedSettingMock.mockClear()

        const value = 'k'.repeat(32)
        const input = wrapper.find('input')
        // 32 字符中途停顿 3 次:停顿期间零写请求、零 toast
        for (const typed of [10, 21, 32]) {
            await input.setValue(value.slice(0, typed))
            await vi.advanceTimersByTimeAsync(3000)
            await flushPromises()
        }
        expect(setEncryptedSettingMock).not.toHaveBeenCalled()
        expect(useToastStore(pinia).toasts).toHaveLength(0)

        // blur 后一次性提交
        await input.trigger('blur')
        await flushPromises()
        expect(setEncryptedSettingMock).toHaveBeenCalledTimes(1)
        expect(setEncryptedSettingMock).toHaveBeenCalledWith('openai_api_key', value)
    })

    it('commits on Enter through the prevented form submit (no lost input, no navigation)', async () => {
        const { wrapper } = await mountField({ ...secretField, minLength: 20 }, true)
        setEncryptedSettingMock.mockClear()

        const input = wrapper.find('input')
        await input.setValue('enter-committed-secret-99')
        await wrapper.find('form').trigger('submit')
        await flushPromises()

        expect(setEncryptedSettingMock).toHaveBeenCalledWith(
            'openai_api_key',
            'enter-committed-secret-99'
        )
        expect(wrapper.emitted('save')).toBeTruthy()
    })

    it('emits no save and stays unconfigured when the write fails (no optimistic save)', async () => {
        setEncryptedSettingMock.mockRejectedValueOnce(new Error('Request failed with status code 400'))
        const { wrapper, pinia } = await mountField(secretField, false)

        const input = wrapper.find('input')
        await input.setValue('doomed-secret')
        await input.trigger('blur')
        await flushPromises()

        expect(wrapper.emitted('save')).toBeFalsy()
        expect(
            useSettingStore(pinia).isEncryptedSettingConfigured('openai_api_key' as never)
        ).toBe(false)
        // 失败反馈由 store 统一弹出,组件层零「已保存」
        const toasts = useToastStore(pinia).toasts
        expect(toasts).toHaveLength(1)
        expect(toasts[0].type).toBe('error')
        expect(toasts[0].message).toBe(en.common.saveFailed)
    })
})

describe('PluginField · Secret 显示(显式取值)', () => {
    it('reveals the stored secret as editable text via the per-key endpoint', async () => {
        revealEncryptedSettingMock.mockResolvedValue('stored-secret-token')
        const { wrapper } = await mountField(secretField, true)

        await wrapper.find('[data-test="secret-visibility-openai_api_key"]').trigger('click')
        await flushPromises()

        expect(revealEncryptedSettingMock).toHaveBeenCalledWith('openai_api_key')
        const input = wrapper.find('input')
        expect(input.attributes('type')).toBe('text')
        expect((input.element as HTMLInputElement).value).toBe('stored-secret-token')
    })

    it('hides back to an empty password input without a second request', async () => {
        revealEncryptedSettingMock.mockResolvedValue('stored-secret-token')
        const { wrapper } = await mountField(secretField, true)

        const toggle = () =>
            wrapper.find('[data-test="secret-visibility-openai_api_key"]').trigger('click')
        await toggle()
        await flushPromises()
        await toggle()
        await flushPromises()

        expect(revealEncryptedSettingMock).toHaveBeenCalledTimes(1)
        const input = wrapper.find('input')
        expect(input.attributes('type')).toBe('password')
        expect((input.element as HTMLInputElement).value).toBe('')
    })

    it('disables the reveal button while the fetch is in flight (#5, no double request)', async () => {
        let release: (value: string) => void = () => {}
        revealEncryptedSettingMock.mockReturnValueOnce(
            new Promise((resolve) => {
                release = resolve
            })
        )
        const { wrapper } = await mountField(secretField, true)

        const button = wrapper.find('[data-test="secret-visibility-openai_api_key"]')
        await button.trigger('click')
        await flushPromises()

        expect(button.attributes('disabled')).toBeDefined()
        await button.trigger('click')
        await flushPromises()
        expect(revealEncryptedSettingMock).toHaveBeenCalledTimes(1)

        release('tok')
        await flushPromises()
        // reveal 完成后 type 切 TEXT、根元素 form→div 重建,按钮需重查
        expect(
            wrapper.find('[data-test="secret-visibility-openai_api_key"]').attributes('disabled')
        ).toBeUndefined()
    })

    it('offers no reveal button when the secret is not configured', async () => {
        const { wrapper } = await mountField(secretField, false)

        expect(wrapper.find('[data-test="secret-visibility-openai_api_key"]').exists()).toBe(false)
    })

    it('offers no reveal button on non-secret fields', async () => {
        const { wrapper } = await mountField(textField)

        expect(wrapper.find('[data-test="secret-visibility-openai_model"]').exists()).toBe(false)
    })
})

describe('PluginField · 清除凭据(#3)', () => {
    it('offers the clear button only while the secret is configured', async () => {
        const configured = await mountField(secretField, true)
        expect(
            configured.wrapper.find('[data-test="secret-clear-openai_api_key"]').exists()
        ).toBe(true)

        const bare = await mountField(secretField, false)
        expect(bare.wrapper.find('[data-test="secret-clear-openai_api_key"]').exists()).toBe(false)
    })

    it('does nothing when the user cancels the confirm', async () => {
        stubConfirm(false)
        const { wrapper, pinia } = await mountField(secretField, true)
        setEncryptedSettingMock.mockClear()

        await wrapper.find('[data-test="secret-clear-openai_api_key"]').trigger('click')
        await flushPromises()

        expect(setEncryptedSettingMock).not.toHaveBeenCalled()
        expect(
            useSettingStore(pinia).isEncryptedSettingConfigured('openai_api_key' as never)
        ).toBe(true)
        expect(wrapper.emitted('save')).toBeFalsy()
        expect(useToastStore(pinia).toasts).toHaveLength(0)
    })

    it('clears via an empty-string write, flips the mirror off and emits refresh (not save)', async () => {
        stubConfirm(true)
        const { wrapper, pinia } = await mountField(secretField, true)

        await wrapper.find('[data-test="secret-clear-openai_api_key"]').trigger('click')
        await flushPromises()

        // 空串写回 = 服务端既有清除语义,后端零改动
        expect(setEncryptedSettingMock).toHaveBeenCalledWith('openai_api_key', '')
        expect(
            useSettingStore(pinia).isEncryptedSettingConfigured('openai_api_key' as never)
        ).toBe(false)
        // 清除走 refresh(只刷新卡背,不触发「已保存」)
        expect(wrapper.emitted('refresh')).toBeTruthy()
        expect(wrapper.emitted('save')).toBeFalsy()
        const toasts = useToastStore(pinia).toasts
        expect(toasts).toHaveLength(1)
        expect(toasts[0].message).toBe(en.services.plugins.credentialCleared)
        expect(toasts[0].type).toBe('success')
    })

    it('keeps the state and toasts clearFailed when the clear write fails', async () => {
        stubConfirm(true)
        setEncryptedSettingMock.mockRejectedValueOnce(new Error('500'))
        const { wrapper, pinia } = await mountField(secretField, true)

        await wrapper.find('[data-test="secret-clear-openai_api_key"]').trigger('click')
        await flushPromises()

        expect(
            useSettingStore(pinia).isEncryptedSettingConfigured('openai_api_key' as never)
        ).toBe(true)
        expect(wrapper.emitted('save')).toBeFalsy()
        const toasts = useToastStore(pinia).toasts
        expect(toasts).toHaveLength(1)
        expect(toasts[0].message).toBe(en.services.plugins.clearFailed)
        expect(toasts[0].type).toBe('error')
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

    it('keeps saving non-secret fields on input (no blur/Enter gate)', async () => {
        vi.useFakeTimers()
        const { wrapper } = await mountField({ ...textField, default: undefined })
        setSettingMock.mockClear()

        const input = wrapper.find('input')
        await input.setValue('gpt-4o-mini')
        await vi.advanceTimersByTimeAsync(1100)
        await flushPromises()

        expect(setSettingMock).toHaveBeenCalledWith('openai_model', 'gpt-4o-mini')
    })
})
