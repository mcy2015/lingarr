// @vitest-environment happy-dom
// Authentication drill-down (screen-06 D drill): the pb-head carries the
// identity (back to the system group + the real enabled state as the mono sub
// line) while both original sections survive — the auth switch (guarded by
// the has-any-users probe) and the API key area (generate + usage).
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { flushPromises, mount } from '@vue/test-utils'
import en from '@/i18n/locales/en.json'
import AuthenticationPage from '@/cells/settings/pages/AuthenticationPage.vue'
import DrillDownPage from '@/cells/app/components/DrillDownPage.vue'
import { useSettingStore } from '@/cells/app/stores/setting'
import { ENCRYPTED_SETTINGS, SETTINGS } from '@/types'

const hasAnyUsersMock = vi.hoisted(() => vi.fn())
const generateApiKeyMock = vi.hoisted(() => vi.fn())
const revokeApiKeyMock = vi.hoisted(() => vi.fn())
const setSettingMock = vi.hoisted(() => vi.fn())
const setEncryptedSettingMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/authService', () => ({
    authService: () => ({
        hasAnyUsers: hasAnyUsersMock,
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
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

async function mountPage() {
    const router = createRouter({
        history: createMemoryHistory(),
        routes: [
            { path: '/', name: 'home', component: { template: '<div />' } },
            {
                path: '/settings/system',
                name: 'settings-system',
                component: { template: '<div />' }
            }
        ]
    })
    await router.push('/')
    await router.isReady()

    const pinia = createPinia()
    const wrapper = mount(AuthenticationPage, {
        global: { plugins: [pinia, router, i18n] }
    })
    await flushPromises()
    return { wrapper, router, pinia }
}

beforeEach(() => {
    hasAnyUsersMock.mockReset().mockResolvedValue(true)
    generateApiKeyMock.mockReset().mockResolvedValue({ apiKey: 'fresh-key' })
    revokeApiKeyMock.mockReset().mockResolvedValue(undefined)
    setSettingMock.mockReset().mockResolvedValue(undefined)
    setEncryptedSettingMock.mockReset().mockResolvedValue(undefined)
})



describe('AuthenticationPage · pb-head', () => {
    it('roots on the shared DrillDownPage skeleton (dd-page, scrolling column)', async () => {
        const { wrapper } = await mountPage()

        const skeleton = wrapper.findComponent(DrillDownPage)
        expect(skeleton.exists()).toBe(true)
        const root = wrapper.find('[data-test="dd-page"]')
        expect(root.exists()).toBe(true)
        // root-ness: the skeleton div is the first element under the mount point
        expect([...(root.element.parentElement?.children ?? [])].indexOf(root.element)).toBe(0)
        // card-flow page: the whole column scrolls
        expect(root.element.classList.contains('overflow-y-auto')).toBe(true)
        expect(root.element.classList.contains('overflow-hidden')).toBe(false)
    })

    it('renders the head with the system back label and the real state sub line', async () => {
        const { wrapper, pinia } = await mountPage()
        const settingStore = useSettingStore(pinia)
        settingStore.storeSetting(SETTINGS.AUTH_ENABLED, 'true')
        await flushPromises()

        expect(wrapper.find('[data-test="pb-back"]').text()).toContain(
            en.settings.menu.groups.system
        )
        expect(wrapper.text()).toContain(en.settings.system.authRow)
        expect(wrapper.find('.mono').text()).toContain(en.settings.system.authOn)
    })

    it('navigates back to the system group screen', async () => {
        const { wrapper, router } = await mountPage()

        await wrapper.find('[data-test="pb-back"]').trigger('click')
        await flushPromises()
        expect(router.currentRoute.value.name).toBe('settings-system')
    })
})

describe('AuthenticationPage · 认证开关(原功能)', () => {
    it('writes auth_enabled when flipping the switch after the user probe passes', async () => {
        const { wrapper } = await mountPage()

        await wrapper.find('[data-test="auth-row-enabled"] [role="switch"]').trigger('click')
        await flushPromises()

        expect(hasAnyUsersMock).toHaveBeenCalledTimes(1)
        const written = setSettingMock.mock.calls.find(
            (call) => call[0] === SETTINGS.AUTH_ENABLED
        )
        expect(written?.[1]).toBe('true')
    })

    it('blocks enabling and surfaces the error when no user exists', async () => {
        hasAnyUsersMock.mockResolvedValue(false)
        const { wrapper } = await mountPage()

        await wrapper.find('[data-test="auth-row-enabled"] [role="switch"]').trigger('click')
        await flushPromises()

        expect(wrapper.text()).toContain(en.settings.authentication.noUsers)
        expect(setSettingMock).not.toHaveBeenCalled()
    })
})

describe('AuthenticationPage · API Key 区(原功能)', () => {
    it('shows the key area only while auth is enabled', async () => {
        const { wrapper, pinia } = await mountPage()
        const settingStore = useSettingStore(pinia)

        settingStore.storeSetting(SETTINGS.AUTH_ENABLED, 'false')
        await flushPromises()
        expect(wrapper.find('[data-test="apikey-card"]').exists()).toBe(false)

        settingStore.storeSetting(SETTINGS.AUTH_ENABLED, 'true')
        await flushPromises()
        expect(wrapper.find('[data-test="apikey-card"]').exists()).toBe(true)
    })

    it('generates a key through the auth endpoint and renders it', async () => {
        const { wrapper, pinia } = await mountPage()
        const settingStore = useSettingStore(pinia)
        settingStore.storeSetting(SETTINGS.AUTH_ENABLED, 'true')
        await flushPromises()

        await wrapper.find('[data-test="apikey-generate"]').trigger('click')
        await flushPromises()

        expect(generateApiKeyMock).toHaveBeenCalledTimes(1)
        expect(wrapper.find('[data-test="apikey-value"]').text()).toContain('fresh-key')
        expect(settingStore.getEncryptedSetting(ENCRYPTED_SETTINGS.API_KEY)).toBe('fresh-key')
    })
})

describe('AuthenticationPage · API Key 吊销(P2-4)', () => {
    it('shows the revoke button only while a key exists', async () => {
        const { wrapper, pinia } = await mountPage()
        const settingStore = useSettingStore(pinia)
        settingStore.storeSetting(SETTINGS.AUTH_ENABLED, 'true')
        settingStore.encrypted_settings[ENCRYPTED_SETTINGS.API_KEY] = ''
        await flushPromises()
        expect(wrapper.find('[data-test="apikey-revoke"]').exists()).toBe(false)

        settingStore.encrypted_settings[ENCRYPTED_SETTINGS.API_KEY] = 'existing-key'
        await flushPromises()
        expect(wrapper.find('[data-test="apikey-revoke"]').exists()).toBe(true)
    })

    it('revokes through the auth endpoint, clears the stored key and toasts', async () => {
        const { wrapper, pinia } = await mountPage()
        const settingStore = useSettingStore(pinia)
        settingStore.storeSetting(SETTINGS.AUTH_ENABLED, 'true')
        settingStore.encrypted_settings[ENCRYPTED_SETTINGS.API_KEY] = 'existing-key'
        await flushPromises()

        await wrapper.find('[data-test="apikey-revoke"]').trigger('click')
        await flushPromises()

        expect(revokeApiKeyMock).toHaveBeenCalledTimes(1)
        // The revoke path writes the empty string back through the encrypted
        // settings channel — an empty stored key never validates server-side.
        const written = setEncryptedSettingMock.mock.calls.find(
            (call) => call[0] === ENCRYPTED_SETTINGS.API_KEY
        )
        expect(written?.[1]).toBe('')
        expect(settingStore.getEncryptedSetting(ENCRYPTED_SETTINGS.API_KEY)).toBe('')
    })
})

describe('AuthenticationPage · 修改密码卡(P0-3)', () => {
    it('shows the change-password card only while auth is enabled', async () => {
        const { wrapper, pinia } = await mountPage()
        const settingStore = useSettingStore(pinia)

        settingStore.storeSetting(SETTINGS.AUTH_ENABLED, 'true')
        await flushPromises()
        expect(wrapper.find('[data-test="change-password-card"]').exists()).toBe(true)

        settingStore.storeSetting(SETTINGS.AUTH_ENABLED, 'false')
        await flushPromises()
        expect(wrapper.find('[data-test="change-password-card"]').exists()).toBe(false)
    })

    it('carries the shared change-password form inside the card', async () => {
        const { wrapper, pinia } = await mountPage()
        const settingStore = useSettingStore(pinia)
        settingStore.storeSetting(SETTINGS.AUTH_ENABLED, 'true')
        await flushPromises()

        const card = wrapper.find('[data-test="change-password-card"]')
        expect(card.find('[data-test="change-password-form"]').exists()).toBe(true)
        expect(card.find('[data-test="change-password-current"]').exists()).toBe(true)
        expect(card.find('[data-test="change-password-new"]').exists()).toBe(true)
        expect(card.find('[data-test="change-password-confirm"]').exists()).toBe(true)
    })
})
