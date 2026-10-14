// @vitest-environment happy-dom
// P0-3 shared change-password form: client-side validation (required /
// mismatch / strength), the server reason-code mapping, and the
// post-success reset — the server revokes every session, so the form drops
// the local auth state and sends the user back to the login screen.
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { flushPromises, mount } from '@vue/test-utils'
import en from '@/i18n/locales/en.json'
import ChangePasswordForm from '@/cells/app/components/ChangePasswordForm.vue'
import { useInstanceStore } from '@/cells/app/stores/instance'
import { ApiError } from '@/cells/app/api/apiError'

const changePasswordMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/authService', () => ({
    authService: () => ({
        changePassword: changePasswordMock
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

async function mountForm() {
    const router = createRouter({
        history: createMemoryHistory(),
        routes: [
            { path: '/auth/login', name: 'login', component: { template: '<div />' } },
            { path: '/auth/change-password', name: 'change-password', component: { template: '<div />' } }
        ]
    })
    await router.push('/auth/login')
    await router.isReady()

    const pinia = createPinia()
    const wrapper = mount(ChangePasswordForm, {
        global: { plugins: [pinia, router, i18n] },
        attachTo: document.body
    })
    await flushPromises()
    return { wrapper, router, pinia }
}

const fillForm = async (
    wrapper: Awaited<ReturnType<typeof mountForm>>['wrapper'],
    current: string,
    next: string,
    confirm: string
) => {
    await wrapper.find('[data-test="change-password-current"]').setValue(current)
    await wrapper.find('[data-test="change-password-new"]').setValue(next)
    await wrapper.find('[data-test="change-password-confirm"]').setValue(confirm)
}

beforeEach(() => {
    changePasswordMock.mockReset()
})

describe('ChangePasswordForm · 客户端校验', () => {
    it('rejects incomplete input without calling the API', async () => {
        const { wrapper } = await mountForm()

        await wrapper.find('form').trigger('submit')
        await flushPromises()

        expect(wrapper.find('[data-test="change-password-error"]').text()).toContain(
            en.changePassword.errors.required
        )
        expect(changePasswordMock).not.toHaveBeenCalled()
    })

    it('rejects mismatching confirmation without calling the API', async () => {
        const { wrapper } = await mountForm()

        await fillForm(wrapper, 'OldPass123', 'NewPass123', 'Different123')
        await wrapper.find('form').trigger('submit')
        await flushPromises()

        expect(wrapper.find('[data-test="change-password-error"]').text()).toContain(
            en.changePassword.errors.mismatch
        )
        expect(changePasswordMock).not.toHaveBeenCalled()
    })

    it('rejects weak new passwords locally', async () => {
        const { wrapper } = await mountForm()

        await fillForm(wrapper, 'OldPass123', 'nodigits', 'nodigits')
        await wrapper.find('form').trigger('submit')
        await flushPromises()

        expect(wrapper.find('[data-test="change-password-error"]').text()).toContain(
            en.changePassword.errors.weak
        )
        expect(changePasswordMock).not.toHaveBeenCalled()
    })
})

describe('ChangePasswordForm · 服务端 reason 码映射', () => {
    const apiError = (status: number, data: unknown) =>
        new ApiError({ message: 'failed', status, data })

    it('maps invalid_current_password to the localized message', async () => {
        changePasswordMock.mockRejectedValue(
            apiError(401, { reason: 'invalid_current_password', message: 'server text' })
        )
        const { wrapper } = await mountForm()

        await fillForm(wrapper, 'WrongOld1', 'NewPass123', 'NewPass123')
        await wrapper.find('form').trigger('submit')
        await flushPromises()

        expect(wrapper.find('[data-test="change-password-error"]').text()).toContain(
            en.changePassword.errors.invalidCurrent
        )
    })

    it('maps weak_password to the localized message', async () => {
        changePasswordMock.mockRejectedValue(
            apiError(400, { reason: 'weak_password', message: 'server text' })
        )
        const { wrapper } = await mountForm()

        await fillForm(wrapper, 'OldPass123', 'NewPass123', 'NewPass123')
        await wrapper.find('form').trigger('submit')
        await flushPromises()

        expect(wrapper.find('[data-test="change-password-error"]').text()).toContain(
            en.changePassword.errors.weak
        )
    })

    it('falls back to the server message for unknown reasons', async () => {
        changePasswordMock.mockRejectedValue(apiError(500, { message: 'server exploded' }))
        const { wrapper } = await mountForm()

        await fillForm(wrapper, 'OldPass123', 'NewPass123', 'NewPass123')
        await wrapper.find('form').trigger('submit')
        await flushPromises()

        expect(wrapper.find('[data-test="change-password-error"]').text()).toContain(
            'server exploded'
        )
    })
})

describe('ChangePasswordForm · 成功语义(全部会话被吊销)', () => {
    it('resets the auth state and routes back to the login page', async () => {
        changePasswordMock.mockResolvedValue({ mustChangePassword: false })
        const { wrapper, router, pinia } = await mountForm()
        const instanceStore = useInstanceStore(pinia)
        instanceStore.setAuthenticated(true)
        instanceStore.setMustChangePassword(true)

        await fillForm(wrapper, 'OldPass123', 'NewPass123', 'NewPass123')
        await wrapper.find('form').trigger('submit')
        await flushPromises()

        expect(changePasswordMock).toHaveBeenCalledWith({
            currentPassword: 'OldPass123',
            newPassword: 'NewPass123'
        })
        // The server revoked every session — the client must not pretend the
        // current one is still alive.
        expect(instanceStore.authenticated).toBe(false)
        expect(instanceStore.mustChangePassword).toBe(false)
        expect(router.currentRoute.value.name).toBe('login')
    })
})
