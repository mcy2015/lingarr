// @vitest-environment happy-dom
// DirectoryModal (UI batch 1, item 1): the directory browser used to have NO
// close entry at all (only backdrop/Escape). It now carries exactly one — the
// footer「Close」next to「Select」— matching the footer-only close convention
// the other modals converge on.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createI18n } from 'vue-i18n'
import { mount, type VueWrapper } from '@vue/test-utils'
import DirectoryModal from '@/cells/settings/components/DirectoryModal.vue'
import en from '@/i18n/locales/en.json'

vi.mock('@/cells/app/api/directoryService', () => ({
    directoryService: () => ({ get: vi.fn().mockResolvedValue([]) })
}))

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

/// The dialog teleports to document.body and test-utils does not unmount
/// wrappers automatically — a dialog left open would shadow the next test's
/// document.querySelector. Unmount after each test.
let activeWrapper: VueWrapper | null = null

afterEach(() => {
    activeWrapper?.unmount()
    activeWrapper = null
})

async function mountModal() {
    // isOpen from the start: the fetch watch only reacts to changes, so no
    // directory call is needed for these dialog-shell assertions
    activeWrapper = mount(DirectoryModal, {
        props: { isOpen: true },
        global: { plugins: [i18n] }
    })
    return activeWrapper
}

describe('DirectoryModal · 关闭入口唯一', () => {
    it('renders exactly one close-class button — the footer one — alongside Select', async () => {
        await mountModal()

        const dialog = document.querySelector('[role="dialog"]')
        expect(dialog).not.toBeNull()
        const closeButtons = [...dialog!.querySelectorAll('button')].filter(
            (button) => button.textContent?.trim() === en.common.close
        )
        expect(closeButtons).toHaveLength(1)
        expect(closeButtons[0].getAttribute('data-test')).toBe('directory-modal-close')
        // the primary action stays in the same footer row
        expect(dialog!.textContent).toContain(en.services.directory.select)
    })

    it('emits close when the footer close button is clicked', async () => {
        const wrapper = await mountModal()

        ;(document.querySelector('[data-test="directory-modal-close"]') as HTMLElement).click()
        await Promise.resolve()

        expect(wrapper.emitted('close')).toHaveLength(1)
    })
})
