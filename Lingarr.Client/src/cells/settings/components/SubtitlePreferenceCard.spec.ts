// @vitest-environment happy-dom
// SubtitlePreferenceCard (B07): the prefer_hearing_impaired toggle (default
// off) persists subtitle_search_prefer_hearing_impaired through the setting
// API. The persisted-value baseline keeps hydration and failure rollbacks
// from firing spurious saves; a rejected write reverts the switch and
// surfaces an error toast (never 'danger').
import { describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { flushPromises, mount } from '@vue/test-utils'
import SubtitlePreferenceCard from '@/cells/settings/components/SubtitlePreferenceCard.vue'
import { useToastStore } from '@/cells/app/stores/toast'
import en from '@/i18n/locales/en.json'

const setSetting = vi.hoisted(() => vi.fn().mockResolvedValue(undefined))
const getSettings = vi.hoisted(() => vi.fn().mockResolvedValue({}))

vi.mock('@/cells/app/api/settingService', () => ({
    settingService: () => ({
        setSetting,
        getSettings,
        getEncryptedSettingStates: vi.fn().mockResolvedValue({})
    })
}))

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

async function mountCard(storedValue?: string) {
    getSettings.mockResolvedValue(
        storedValue === undefined ? {} : { subtitle_search_prefer_hearing_impaired: storedValue }
    )
    setSetting.mockClear()

    const pinia = createPinia()
    const wrapper = mount(SubtitlePreferenceCard, {
        global: { plugins: [pinia, i18n] }
    })
    await flushPromises()
    return { wrapper, toastStore: useToastStore(pinia) }
}

/// The toggle is the first button inside the row (ToggleButton renders a
/// <button>); flipping it through the DOM drives the v-model ref.
async function flipOn(wrapper: Awaited<ReturnType<typeof mountCard>>['wrapper']) {
    await wrapper.find('[data-test="prefer-hearing-impaired-row"] button').trigger('click')
    await flushPromises()
}

describe('SubtitlePreferenceCard · 听障优先开关', () => {
    it('defaults to off and writes nothing on mount', async () => {
        const { wrapper } = await mountCard()

        expect(wrapper.find('[data-test="subtitle-preference-card"]').exists()).toBe(true)
        expect(wrapper.find('[data-test="prefer-hearing-impaired-row"]').text()).toContain(
            en.settings.status.disabled
        )
        expect(setSetting).not.toHaveBeenCalled()
    })

    it('hydrates a stored true without re-saving it', async () => {
        const { wrapper } = await mountCard('true')

        expect(wrapper.find('[data-test="prefer-hearing-impaired-row"]').text()).toContain(
            en.settings.status.enabled
        )
        expect(setSetting).not.toHaveBeenCalled()
    })

    it('persists a toggle and toasts success', async () => {
        const { wrapper, toastStore } = await mountCard('false')

        await flipOn(wrapper)

        expect(setSetting).toHaveBeenCalledWith('subtitle_search_prefer_hearing_impaired', 'true')
        expect(toastStore.toasts[0].type).toBe('success')
        expect(wrapper.find('[data-test="prefer-hearing-impaired-row"]').text()).toContain(
            en.settings.status.enabled
        )
    })

    it('reverts the switch and toasts an error when the write fails', async () => {
        setSetting.mockRejectedValueOnce(new Error('network'))
        const { wrapper, toastStore } = await mountCard('false')

        await flipOn(wrapper)

        expect(setSetting).toHaveBeenCalledWith('subtitle_search_prefer_hearing_impaired', 'true')
        expect(toastStore.toasts[0].message).toBe(en.common.saveFailed)
        expect(toastStore.toasts[0].type).toBe('error')
        // honest UI: the server rejected the change, the switch is back off
        expect(wrapper.find('[data-test="prefer-hearing-impaired-row"]').text()).toContain(
            en.settings.status.disabled
        )
        // the rollback itself must not fire a second save
        expect(setSetting).toHaveBeenCalledTimes(1)
    })
})
