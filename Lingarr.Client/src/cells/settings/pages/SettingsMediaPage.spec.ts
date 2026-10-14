// @vitest-environment happy-dom
// SettingsMediaPage (设置 · 媒体源 tab, IA 重构批 2):瘦身后仅承载媒体源卡,
// 自动化批次卡已迁往自动化 tab。
import { describe, expect, it, onTestFinished, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { mount } from '@vue/test-utils'
import SettingsMediaPage from '@/cells/settings/pages/SettingsMediaPage.vue'
import en from '@/i18n/locales/en.json'

vi.mock('@/cells/settings/components/MediaSourcesCard.vue', () => ({
    default: { name: 'MediaSourcesCard', template: '<div data-test="media-sources-card" />' }
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

describe('SettingsMediaPage', () => {
    it('hosts only the media sources card', async () => {
        const pinia = createPinia()
        const wrapper = mount(SettingsMediaPage, {
            global: { plugins: [pinia, i18n] }
        })
        onTestFinished(() => wrapper.unmount())

        expect(wrapper.find('[data-test="media-sources-card"]').exists()).toBe(true)
        expect(wrapper.find('[data-test="batches-card"]').exists()).toBe(false)
    })
})
