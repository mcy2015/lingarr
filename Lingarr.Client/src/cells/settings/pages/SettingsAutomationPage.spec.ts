// @vitest-environment happy-dom
// SettingsAutomationPage (设置 · 自动化 tab, IA 重构批 2):策略卡 + 定时任务
// 下钻行;SERVICE_TYPE 为空数组时首用守卫(警示 + 跳翻译服务 tab)。
import { describe, expect, it, onTestFinished, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { flushPromises, mount } from '@vue/test-utils'
import SettingsAutomationPage from '@/cells/settings/pages/SettingsAutomationPage.vue'
import { useSettingStore } from '@/cells/app/stores/setting'
import { ISettings } from '@/types'
import en from '@/i18n/locales/en.json'

vi.mock('@/cells/app/api/settingService', () => ({
    settingService: () => ({
        setSetting: vi.fn().mockResolvedValue(undefined),
        getSettings: vi.fn().mockResolvedValue({}),
        getEncryptedSettingStates: vi.fn().mockResolvedValue({})
    })
}))

vi.mock('@/cells/settings/services/scheduleService', () => ({
    scheduleService: () => ({
        disableJob: vi.fn().mockResolvedValue(undefined),
        enableJob: vi.fn().mockResolvedValue(undefined),
        recurringJobs: vi.fn().mockResolvedValue([])
    })
}))

vi.mock('@/cells/settings/components/AutomationBatchesCard.vue', () => ({
    default: { name: 'AutomationBatchesCard', template: '<div data-test="batches-card" />' }
}))

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

async function mountPage(serviceType: string) {
    const pinia = createPinia()
    const settingStore = useSettingStore(pinia)
    settingStore.storeSetting('service_type' as keyof ISettings, serviceType)

    const router = createRouter({
        history: createMemoryHistory(),
        routes: [
            {
                path: '/settings',
                component: { template: '<div />' },
                children: [
                    { path: 'providers', name: 'settings-providers', component: { template: '<div />' } },
                    { path: 'automation', name: 'settings-automation', component: SettingsAutomationPage }
                ]
            },
            { path: '/schedule', name: 'schedule', component: { template: '<div />' } }
        ]
    })
    await router.push('/settings/automation')
    await router.isReady()

    const wrapper = mount(SettingsAutomationPage, {
        global: { plugins: [pinia, i18n, router] }
    })
    onTestFinished(() => wrapper.unmount())
    await flushPromises()
    return { wrapper, router }
}

describe('SettingsAutomationPage', () => {
    it('renders the strategy card and the schedule drill row', async () => {
        const { wrapper } = await mountPage('["libretranslate"]')

        expect(wrapper.find('[data-test="batches-card"]').exists()).toBe(true)
        expect(wrapper.find('[data-test="automation-row-schedule"]').exists()).toBe(true)
        expect(wrapper.find('[data-test="automation-no-providers"]').exists()).toBe(false)
    })

    it('drills to the schedule page from the entry row', async () => {
        const { wrapper, router } = await mountPage('["libretranslate"]')

        await wrapper.find('[data-test="automation-open-schedule"]').trigger('click')
        await flushPromises()
        expect(router.currentRoute.value.name).toBe('schedule')
    })

    it('shows the first-use guard with a link to providers when none are configured', async () => {
        const { wrapper, router } = await mountPage('[]')

        const guard = wrapper.find('[data-test="automation-no-providers"]')
        expect(guard.exists()).toBe(true)
        expect(guard.text()).toContain(en.settings.automation.servicesLink)

        await wrapper.find('[data-test="automation-goto-providers"]').trigger('click')
        await flushPromises()
        expect(router.currentRoute.value.name).toBe('settings-providers')
    })
})
