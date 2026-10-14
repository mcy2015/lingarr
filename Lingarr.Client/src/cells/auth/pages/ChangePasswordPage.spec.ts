// @vitest-environment happy-dom
// P0-3 forced change page: unclosable screen the router guard funnels into —
// renders the default-credentials intro and the shared change form.
import { describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { flushPromises, mount } from '@vue/test-utils'
import en from '@/i18n/locales/en.json'
import ChangePasswordPage from '@/cells/auth/pages/ChangePasswordPage.vue'

vi.mock('@/cells/app/api/authService', () => ({
    authService: () => ({
        changePassword: vi.fn()
    })
}))

vi.mock('axios', () => ({ default: { post: vi.fn() } }))

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

describe('ChangePasswordPage · 强制改密页', () => {
    it('renders the forced-change intro and the shared form', async () => {
        const router = createRouter({
            history: createMemoryHistory(),
            routes: [
                { path: '/auth/change-password', name: 'change-password', component: { template: '<div />' } }
            ]
        })
        await router.push('/auth/change-password')
        await router.isReady()

        const wrapper = mount(ChangePasswordPage, {
            global: { plugins: [createPinia(), router, i18n] }
        })
        await flushPromises()

        expect(wrapper.text()).toContain(en.changePassword.forcedTitle)
        expect(wrapper.text()).toContain(en.changePassword.forcedIntro)
        expect(wrapper.find('[data-test="change-password-form"]').exists()).toBe(true)
        // Nothing on this screen navigates away from the change form.
        expect(wrapper.find('[data-test="pb-back"]').exists()).toBe(false)
    })
})
