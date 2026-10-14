// @vitest-environment happy-dom
// ChangeUsernameCard(认证管理下钻屏):单用户改名表单——本地格式校验先于
// 请求;成功 toast+清空;错误按服务端 reason 码映射文案;仅在认证开启时
// 渲染。与改密不同,改名不吊销会话、不跳登录页。
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { flushPromises, mount, VueWrapper } from '@vue/test-utils'
import ChangeUsernameCard from '@/cells/settings/components/authentication/ChangeUsernameCard.vue'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { ISettings, SETTINGS } from '@/types'
import en from '@/i18n/locales/en.json'
import { ApiError } from '@/cells/app/api/apiError'

const changeUsernameMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/authService', () => ({
    authService: () => ({
        changeUsername: changeUsernameMock
    })
}))

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

const mountCard = async (authEnabled = 'true'): Promise<VueWrapper> => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const settingStore = useSettingStore()
    settingStore.storeSetting(SETTINGS.AUTH_ENABLED as keyof ISettings, authEnabled)
    const wrapper = mount(ChangeUsernameCard, { global: { plugins: [pinia, i18n] } })
    await flushPromises()
    return wrapper
}

const fillAndSubmit = async (wrapper: VueWrapper, username: string, password: string) => {
    await wrapper.find('[data-test="change-username-input"]').setValue(username)
    await wrapper.find('[data-test="change-username-password"]').setValue(password)
    await wrapper.find('form').trigger('submit')
    await flushPromises()
}

beforeEach(() => {
    changeUsernameMock.mockReset().mockResolvedValue({ username: 'mcy2015' })
})

describe('ChangeUsernameCard', () => {
    it('renders only while authentication is enabled', async () => {
        const on = await mountCard('true')
        expect(on.find('[data-test="change-username-card"]').exists()).toBe(true)

        const off = await mountCard('false')
        expect(off.find('[data-test="change-username-card"]').exists()).toBe(false)
    })

    it('rejects an invalid username locally without calling the API', async () => {
        const wrapper = await mountCard()

        await fillAndSubmit(wrapper, 'no', 'secret123')

        const error = wrapper.find('[data-test="change-username-error"]')
        expect(error.exists()).toBe(true)
        expect(error.text()).toContain('3-32')
        expect(changeUsernameMock).not.toHaveBeenCalled()
    })

    it('changes the username: API call, toast, fields cleared, stays on the card', async () => {
        const wrapper = await mountCard()

        await fillAndSubmit(wrapper, 'mcy2015', 'secret123')

        expect(changeUsernameMock).toHaveBeenCalledWith({
            currentPassword: 'secret123',
            newUsername: 'mcy2015'
        })
        expect(useToastStore().toasts[0].message).toContain('mcy2015')
        expect(useToastStore().toasts[0].type).toBe('success')
        expect(
            (wrapper.find('[data-test="change-username-input"]').element as HTMLInputElement).value
        ).toBe('')
        // a rename keeps the session — no redirect, no error banner
        expect(wrapper.find('[data-test="change-username-error"]').exists()).toBe(false)
    })

    it('maps server reason codes to localized copy and keeps the form filled', async () => {
        changeUsernameMock.mockRejectedValueOnce(
            new ApiError({ message: 'conflict', status: 409, data: { reason: 'username_taken' } })
        )
        const wrapper = await mountCard()

        await fillAndSubmit(wrapper, 'mcy2015', 'secret123')

        const error = wrapper.find('[data-test="change-username-error"]')
        expect(error.exists()).toBe(true)
        expect(error.text()).toContain('already taken')
        expect(
            (wrapper.find('[data-test="change-username-input"]').element as HTMLInputElement).value
        ).toBe('mcy2015')
        expect(useToastStore().toasts).toHaveLength(0)
    })
})
