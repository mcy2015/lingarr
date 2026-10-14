// @vitest-environment happy-dom
// SettingsSystemPage (screen-06 D): the single row card — version meta from
// the instance store, the auth row with the derived API Key chip and the
// management drill-down, plus the logs/schedule drill rows.
import { describe, expect, it } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { flushPromises, mount } from '@vue/test-utils'
import SettingsSystemPage from '@/cells/settings/pages/SettingsSystemPage.vue'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useInstanceStore } from '@/cells/app/stores/instance'
import { SETTINGS } from '@/types'
import en from '@/i18n/locales/en.json'

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

async function mountPage(options: {
    authEnabled: string
    apiKeyConfigured?: boolean
    version?: string
}) {
    const pinia = createPinia()
    const settingStore = useSettingStore(pinia)
    settingStore.storeSetting(SETTINGS.AUTH_ENABLED, options.authEnabled)
    if (options.apiKeyConfigured !== undefined) {
        // Security walk fix: only the boolean configured flag exists client-side.
        settingStore.encrypted_settings.api_key = options.apiKeyConfigured
    }
    const instanceStore = useInstanceStore(pinia)
    if (options.version) {
        instanceStore.setVersion({
            newVersion: false,
            isDevelopment: false,
            currentVersion: options.version,
            latestVersion: options.version
        })
    }

    const router = createRouter({
        history: createMemoryHistory(),
        routes: [
            { path: '/', name: 'home', component: { template: '<div />' } },
            { path: '/logs', name: 'logs', component: { template: '<div />' } },
            { path: '/schedule', name: 'schedule', component: { template: '<div />' } },
            {
                path: '/settings/authentication',
                name: 'settings-authentication',
                component: { template: '<div />' }
            }
        ]
    })
    await router.push('/')
    await router.isReady()

    const wrapper = mount(SettingsSystemPage, {
        global: { plugins: [pinia, i18n, router] }
    })
    await flushPromises()
    return { wrapper, router }
}

describe('SettingsSystemPage · 版本 meta', () => {
    it('shows the real instance version in the card header', async () => {
        const { wrapper } = await mountPage({ authEnabled: 'true', version: 'v1.9.2' })

        expect(wrapper.find('[data-test="system-version"]').text()).toBe('v1.9.2')
    })

    it('omits the version meta when the store has none yet', async () => {
        const { wrapper } = await mountPage({ authEnabled: 'false' })

        expect(wrapper.find('[data-test="system-version"]').exists()).toBe(false)
    })
})

describe('SettingsSystemPage · 认证行', () => {
    it('derives the API Key chip from the configured flag', async () => {
        const withKey = await mountPage({ authEnabled: 'true', apiKeyConfigured: true })
        expect(withKey.wrapper.find('[data-test="apikey-chip"]').text()).toBe(
            en.settings.system.apiKeyIssued
        )

        const withoutKey = await mountPage({ authEnabled: 'true', apiKeyConfigured: false })
        expect(withoutKey.wrapper.find('[data-test="apikey-chip"]').exists()).toBe(false)
    })

    it('switches the hint by auth state', async () => {
        const on = await mountPage({ authEnabled: 'true' })
        expect(on.wrapper.find('[data-test="system-row-auth"]').text()).toContain(
            en.settings.system.authOn
        )

        const off = await mountPage({ authEnabled: 'false' })
        expect(off.wrapper.find('[data-test="system-row-auth"]').text()).toContain(
            en.settings.system.authOff
        )
    })

    it('drills down to the authentication management page', async () => {
        const { wrapper, router } = await mountPage({ authEnabled: 'true' })

        await wrapper.find('[data-test="auth-manage"]').trigger('click')
        await flushPromises()

        expect(router.currentRoute.value.name).toBe('settings-authentication')
    })
})

describe('SettingsSystemPage · 日志下钻行', () => {
    it('keeps the logs drill row and drops the schedule row (moved to automation tab)', async () => {
        const { wrapper, router } = await mountPage({ authEnabled: 'true' })

        await wrapper.find('[data-test="system-open-logs"]').trigger('click')
        await flushPromises()
        expect(router.currentRoute.value.name).toBe('logs')

        expect(wrapper.find('[data-test="system-row-schedule"]').exists()).toBe(false)
    })
})
