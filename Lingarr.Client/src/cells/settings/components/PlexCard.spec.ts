// @vitest-environment happy-dom
// PlexCard (B03): row state (dot/address/refresh switch echo), the edit
// modal's URL validation (invalid input never writes), the token contract
// (password field, never echoes the stored value, blur/Enter commit, empty
// keeps the stored token, reveal through the trailing slot), and the
// refresh switch persisting plex_refresh_enabled through settingService.
import { beforeEach, describe, expect, it, onTestFinished, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import PlexCard from '@/cells/settings/components/PlexCard.vue'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { SETTINGS } from '@/types'
import en from '@/i18n/locales/en.json'
// side effect: installs String.prototype.format used by InputComponent validation
import '@/cells/kernel/utils/string'

const setSettingMock = vi.hoisted(() => vi.fn().mockResolvedValue(undefined))
const setEncryptedSettingMock = vi.hoisted(() => vi.fn().mockResolvedValue(undefined))
const revealEncryptedSettingMock = vi.hoisted(() => vi.fn().mockResolvedValue(''))

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

interface MountOptions {
    url?: string
    tokenConfigured?: boolean
    refreshEnabled?: boolean
}

async function mountCard(options: MountOptions = {}) {
    const pinia = createPinia()
    const settingStore = useSettingStore(pinia)
    settingStore.storeSetting(SETTINGS.PLEX_URL, options.url ?? 'http://plex:32400')
    settingStore.storeSetting(
        SETTINGS.PLEX_REFRESH_ENABLED,
        options.refreshEnabled ? 'true' : 'false'
    )
    // Encrypted settings are boolean "configured" flags — no plaintext exists
    // client-side to seed.
    settingStore.encrypted_settings.plex_token = options.tokenConfigured ?? true

    const wrapper = mount(PlexCard, {
        global: { plugins: [pinia, i18n] }
    })
    onTestFinished(() => wrapper.unmount())
    await flushPromises()
    return { wrapper, pinia, toastStore: useToastStore(pinia) }
}

/// The edit button lives on the mounted row; the modal it opens renders
/// through Teleport into document.body (MediaSourcesCard precedent), so its
/// contents are queried on document.
async function openModal(wrapper: VueWrapper) {
    await wrapper.find('[data-test="edit-plex"]').trigger('click')
    await flushPromises()
    return document.querySelector('[data-test="plex-modal"]')
}

function modalInputs(): HTMLInputElement[] {
    return [
        ...(document.querySelector('[data-test="plex-modal"]')?.querySelectorAll('input') ?? [])
    ] as HTMLInputElement[]
}

describe('PlexCard · 行状态', () => {
    beforeEach(() => {
        setSettingMock.mockClear()
        setEncryptedSettingMock.mockClear()
        revealEncryptedSettingMock.mockClear().mockResolvedValue('')
    })

    it('renders the configured address and the refresh switch state', async () => {
        const { wrapper } = await mountCard()

        expect(wrapper.find('[data-test="plex-dot"]').classes()).toContain('bg-success')
        expect(wrapper.find('[data-test="plex-row"]').text()).toContain('http://plex:32400')
        expect(wrapper.find('[data-test="plex-refresh-state"]').text()).toBe(
            en.settings.plex.refreshOff
        )
    })

    it('shows the honest not-configured hint when url or token is missing', async () => {
        const { wrapper } = await mountCard({ url: '', tokenConfigured: false })

        expect(wrapper.find('[data-test="plex-dot"]').classes()).toContain('bg-line')
        expect(wrapper.find('[data-test="plex-row"]').text()).toContain(
            en.settings.plex.notConfigured
        )
    })

    it('echoes the refresh-on state from the stored setting', async () => {
        const { wrapper } = await mountCard({ refreshEnabled: true })
        expect(wrapper.find('[data-test="plex-refresh-state"]').text()).toBe(
            en.settings.plex.refreshOn
        )
    })
})

describe('PlexCard · 编辑模态', () => {
    beforeEach(() => {
        setSettingMock.mockClear()
        setEncryptedSettingMock.mockClear()
        revealEncryptedSettingMock.mockClear().mockResolvedValue('')
    })

    it('echoes the stored address and never the stored token', async () => {
        const { wrapper } = await mountCard()
        const modal = await openModal(wrapper)

        expect(modal).not.toBeNull()
        const inputs = modalInputs()
        expect(inputs[0].value).toBe('http://plex:32400')
        // sensitive-value policy: password type, empty value, placeholder
        // explains "leave empty to keep"
        expect(inputs[1].type).toBe('password')
        expect(inputs[1].value).toBe('')
        expect(inputs[1].placeholder).toBe(en.settings.plex.tokenConfigured)
    })

    it('URL validation: an invalid URL never writes, a valid one saves through settingService', async () => {
        const { wrapper, toastStore } = await mountCard()
        await openModal(wrapper)

        vi.useFakeTimers()
        try {
            const urlInput = modalInputs()[0]
            urlInput.value = 'plex'
            urlInput.dispatchEvent(new Event('input'))
            await vi.advanceTimersByTimeAsync(1100)

            // no scheme → invalid → no write, no saved toast
            expect(setSettingMock).not.toHaveBeenCalledWith('plex_url', 'plex')
            expect(toastStore.toasts).toHaveLength(0)

            urlInput.value = 'http://plex:32400'
            urlInput.dispatchEvent(new Event('input'))
            await vi.advanceTimersByTimeAsync(1100)

            expect(setSettingMock).toHaveBeenCalledWith('plex_url', 'http://plex:32400')
            expect(toastStore.toasts[0].message).toBe(en.common.saved)
        } finally {
            vi.useRealTimers()
        }
    })

    it('token: commit waits for blur — a pause mid-token writes nothing', async () => {
        const { wrapper } = await mountCard()
        await openModal(wrapper)

        const tokenInput = modalInputs()[1]
        tokenInput.value = 'plex-token-abc'
        tokenInput.dispatchEvent(new Event('input'))
        await flushPromises()

        expect(setEncryptedSettingMock).not.toHaveBeenCalled()

        tokenInput.dispatchEvent(new Event('blur'))
        await flushPromises()

        expect(setEncryptedSettingMock).toHaveBeenCalledTimes(1)
        expect(setEncryptedSettingMock).toHaveBeenCalledWith('plex_token', 'plex-token-abc')
        expect(tokenInput.value).toBe('')
    })

    it('token: an empty blur keeps the stored token (no empty overwrite)', async () => {
        const { wrapper } = await mountCard()
        await openModal(wrapper)

        const tokenInput = modalInputs()[1]
        tokenInput.value = ''
        tokenInput.dispatchEvent(new Event('input'))
        tokenInput.dispatchEvent(new Event('blur'))
        await flushPromises()

        expect(setEncryptedSettingMock).not.toHaveBeenCalled()
    })

    it('token reveal: fetches the stored token on demand, hides without a second request', async () => {
        revealEncryptedSettingMock.mockResolvedValue('revealed-token')
        const { wrapper } = await mountCard()
        await openModal(wrapper)

        const visibility = document.querySelector(
            '[data-test="plex-token-visibility"]'
        ) as HTMLButtonElement | null
        visibility?.dispatchEvent(new MouseEvent('click', { bubbles: true }))
        await flushPromises()

        expect(revealEncryptedSettingMock).toHaveBeenCalledWith('plex_token')
        const tokenInput = modalInputs()[1]
        expect(tokenInput.type).toBe('text')
        expect(tokenInput.value).toBe('revealed-token')

        // The reveal flips the field type password→text, which swaps the
        // InputComponent's dynamic root (form→div) and rebuilds the slot
        // subtree — the visibility button must be re-queried or the second
        // click lands on a detached node.
        document
            .querySelector('[data-test="plex-token-visibility"]')
            ?.dispatchEvent(new MouseEvent('click', { bubbles: true }))
        await flushPromises()

        expect(revealEncryptedSettingMock).toHaveBeenCalledTimes(1)
        expect(modalInputs()[1].type).toBe('password')
        expect(modalInputs()[1].value).toBe('')
    })

    it('token reveal: no eye button when no token is configured', async () => {
        const { wrapper } = await mountCard({ tokenConfigured: false })
        await openModal(wrapper)

        expect(document.querySelector('[data-test="plex-token-visibility"]')).toBeNull()
    })

    it('refresh switch: toggling persists plex_refresh_enabled and flips the row state', async () => {
        const { wrapper } = await mountCard({ refreshEnabled: false })
        await openModal(wrapper)

        const switchButton = document.querySelector(
            '[data-test="plex-modal"] button[role="switch"]'
        ) as HTMLButtonElement | null
        switchButton?.dispatchEvent(new MouseEvent('click', { bubbles: true }))
        await flushPromises()

        expect(setSettingMock).toHaveBeenCalledWith('plex_refresh_enabled', 'true')
        expect(wrapper.find('[data-test="plex-refresh-state"]').text()).toBe(
            en.settings.plex.refreshOn
        )
    })
})
