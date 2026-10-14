// @vitest-environment happy-dom
// P0-3 login flow: a successful login surfaces must_change_password and
// routes accordingly — the forced change screen when the default credentials
// are still in place, the dashboard otherwise.
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { flushPromises, mount } from '@vue/test-utils'
import en from '@/i18n/locales/en.json'
import LoginPage from '@/cells/auth/pages/LoginPage.vue'
import { useInstanceStore } from '@/cells/app/stores/instance'
import { ApiError } from '@/cells/app/api/apiError'

const loginMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/authService', () => ({
    authService: () => ({
        login: loginMock
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

async function mountPage() {
    const router = createRouter({
        history: createMemoryHistory(),
        routes: [
            { path: '/auth/login', name: 'login', component: { template: '<div />' } },
            { path: '/auth/change-password', name: 'change-password', component: { template: '<div />' } },
            { path: '/', name: 'dashboard', component: { template: '<div />' } }
        ]
    })
    await router.push('/auth/login')
    await router.isReady()

    const pinia = createPinia()
    const wrapper = mount(LoginPage, {
        global: { plugins: [pinia, router, i18n] }
    })
    await flushPromises()
    return { wrapper, router, pinia }
}

const submitCredentials = async (
    wrapper: Awaited<ReturnType<typeof mountPage>>['wrapper']
) => {
    await wrapper.find('#username').setValue('admin')
    await wrapper.find('#password').setValue('admin')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
}

beforeEach(() => {
    loginMock.mockReset()
})

describe('LoginPage · 强制改密流转(P0-3)', () => {
    it('routes to the change screen and sets the flag when mustChangePassword is true', async () => {
        loginMock.mockResolvedValue({ mustChangePassword: true })
        const { wrapper, router, pinia } = await mountPage()

        await submitCredentials(wrapper)

        const instanceStore = useInstanceStore(pinia)
        expect(instanceStore.authenticated).toBe(true)
        expect(instanceStore.mustChangePassword).toBe(true)
        expect(router.currentRoute.value.name).toBe('change-password')
    })

    it('routes to the dashboard and clears the flag when mustChangePassword is false', async () => {
        loginMock.mockResolvedValue({ mustChangePassword: false })
        const { wrapper, router, pinia } = await mountPage()

        await submitCredentials(wrapper)

        const instanceStore = useInstanceStore(pinia)
        expect(instanceStore.mustChangePassword).toBe(false)
        expect(router.currentRoute.value.name).toBe('dashboard')
    })

    it('surfaces the server message on failure and stays on the form', async () => {
        loginMock.mockRejectedValue(
            new ApiError({ message: 'bad', status: 401, data: { message: 'Invalid username or password' } })
        )
        const { wrapper, router } = await mountPage()

        await submitCredentials(wrapper)

        expect(wrapper.text()).toContain('Invalid username or password')
        expect(router.currentRoute.value.name).toBe('login')
    })
})
