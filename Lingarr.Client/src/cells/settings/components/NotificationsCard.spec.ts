// @vitest-environment happy-dom
// NotificationsCard (B11): the notification wiring surface — channel
// configured-state dot, master switch persisting notifications_enabled,
// webhook URL / chat id persisting on blur, the bot token contract
// (password field, never echoes the stored value, blur/Enter commit,
// empty keeps, reveal on demand) and the notify_events multi-select
// (stored csv → checkboxes, last checked box stays checked).
import { beforeEach, describe, expect, it, onTestFinished, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import NotificationsCard from '@/cells/settings/components/NotificationsCard.vue'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { NOTIFY_EVENTS, SETTINGS } from '@/types'
import en from '@/i18n/locales/en.json'

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
    enabled?: boolean
    webhookUrl?: string
    chatId?: string
    events?: string
    tokenConfigured?: boolean
}

async function mountCard(options: MountOptions = {}) {
    const pinia = createPinia()
    const settingStore = useSettingStore(pinia)
    settingStore.storeSetting(
        SETTINGS.NOTIFICATIONS_ENABLED,
        options.enabled ? 'true' : 'false'
    )
    settingStore.storeSetting(SETTINGS.NOTIFY_WEBHOOK_URL, options.webhookUrl ?? '')
    settingStore.storeSetting(SETTINGS.NOTIFY_TELEGRAM_CHAT_ID, options.chatId ?? '')
    settingStore.storeSetting(SETTINGS.NOTIFY_EVENTS, options.events ?? 'downloaded,upgraded')
    // Encrypted settings are boolean "configured" flags — no plaintext
    // exists client-side to seed.
    settingStore.encrypted_settings.notify_telegram_bot_token = options.tokenConfigured ?? false

    const wrapper = mount(NotificationsCard, {
        global: { plugins: [pinia, i18n] }
    })
    onTestFinished(() => wrapper.unmount())
    await flushPromises()
    return { wrapper, pinia, toastStore: useToastStore(pinia) }
}

/// The card's text inputs in order: webhook URL, token, chat id (the four
/// checkboxes are queried by their per-event data-test attributes).
const inputs = (wrapper: VueWrapper): HTMLInputElement[] =>
    wrapper
        .findAll('[data-test="notifications-card"] input[type="text"], [data-test="notifications-card"] input[type="password"]')
        .map((input) => input.element as HTMLInputElement)

describe('NotificationsCard · 配置态', () => {
    it('shows the unconfigured dot while no channel is wired', async () => {
        const { wrapper } = await mountCard()
        expect(wrapper.find('[data-test="notifications-dot"]').classes()).toContain('bg-line')
    })

    it('shows the configured dot for a stored webhook URL alone', async () => {
        const { wrapper } = await mountCard({ webhookUrl: 'https://hooks.example.com/lingarr' })
        expect(wrapper.find('[data-test="notifications-dot"]').classes()).toContain('bg-success')
    })

    it('shows the configured dot for the complete Telegram pair (token + chat id)', async () => {
        const { wrapper } = await mountCard({ chatId: '-1001234567890', tokenConfigured: true })
        expect(wrapper.find('[data-test="notifications-dot"]').classes()).toContain('bg-success')
    })

    it('a token without a chat id is not a wired channel', async () => {
        const { wrapper } = await mountCard({ tokenConfigured: true })
        expect(wrapper.find('[data-test="notifications-dot"]').classes()).toContain('bg-line')
    })
})

describe('NotificationsCard · 总开关与平面字段', () => {
    beforeEach(() => {
        setSettingMock.mockClear()
        setEncryptedSettingMock.mockClear()
    })

    it('toggling the master switch persists notifications_enabled', async () => {
        const { wrapper } = await mountCard({ enabled: false })

        await wrapper.find('[data-test="notifications-card"] button[role="switch"]').trigger('click')
        await flushPromises()

        expect(setSettingMock).toHaveBeenCalledWith('notifications_enabled', 'true')
    })

    it('persists the webhook URL and the chat id on blur', async () => {
        const { wrapper, toastStore } = await mountCard()

        const [webhook, , chatId] = inputs(wrapper)
        webhook.value = 'https://hooks.example.com/lingarr'
        webhook.dispatchEvent(new Event('input'))
        webhook.dispatchEvent(new Event('blur'))
        await flushPromises()

        expect(setSettingMock).toHaveBeenCalledWith(
            'notify_webhook_url',
            'https://hooks.example.com/lingarr'
        )
        expect(toastStore.toasts[0].message).toBe(en.common.saved)

        chatId.value = '-1001234567890'
        chatId.dispatchEvent(new Event('input'))
        chatId.dispatchEvent(new Event('blur'))
        await flushPromises()

        expect(setSettingMock).toHaveBeenCalledWith('notify_telegram_chat_id', '-1001234567890')
    })
})

describe('NotificationsCard · 令牌契约', () => {
    beforeEach(() => {
        setSettingMock.mockClear()
        setEncryptedSettingMock.mockClear()
        revealEncryptedSettingMock.mockClear().mockResolvedValue('')
    })

    it('is a password field that never echoes the stored token', async () => {
        const { wrapper } = await mountCard({ tokenConfigured: true })

        const token = inputs(wrapper)[1]
        expect(token.type).toBe('password')
        expect(token.value).toBe('')
        expect(token.placeholder).toBe(en.settings.notifications.telegramTokenConfigured)
        expect(wrapper.find('[data-test="notifications-token-reveal"]').exists()).toBe(true)
    })

    it('blur commits through the encrypted store; an empty blur keeps the stored token', async () => {
        const { wrapper } = await mountCard({ tokenConfigured: true })

        const token = inputs(wrapper)[1]
        token.value = '12345:card-test-token'
        token.dispatchEvent(new Event('input'))
        await flushPromises()
        expect(setEncryptedSettingMock).not.toHaveBeenCalled()

        token.dispatchEvent(new Event('blur'))
        await flushPromises()

        expect(setEncryptedSettingMock).toHaveBeenCalledTimes(1)
        expect(setEncryptedSettingMock).toHaveBeenCalledWith(
            'notify_telegram_bot_token',
            '12345:card-test-token'
        )
        expect(token.value).toBe('')

        // an empty blur never overwrites the stored secret
        token.dispatchEvent(new Event('blur'))
        await flushPromises()
        expect(setEncryptedSettingMock).toHaveBeenCalledTimes(1)
    })

    it('reveal: fetches the stored token on demand and hides without a second fetch', async () => {
        revealEncryptedSettingMock.mockResolvedValue('12345:revealed')
        const { wrapper } = await mountCard({ tokenConfigured: true })

        await wrapper.find('[data-test="notifications-token-reveal"]').trigger('click')
        await flushPromises()

        expect(revealEncryptedSettingMock).toHaveBeenCalledWith('notify_telegram_bot_token')
        const token = inputs(wrapper)[1]
        expect(token.type).toBe('text')
        expect(token.value).toBe('12345:revealed')

        await wrapper.find('[data-test="notifications-token-reveal"]').trigger('click')
        await flushPromises()

        expect(revealEncryptedSettingMock).toHaveBeenCalledTimes(1)
        expect(inputs(wrapper)[1].type).toBe('password')
        expect(inputs(wrapper)[1].value).toBe('')
    })

    it('no reveal button while no token is configured', async () => {
        const { wrapper } = await mountCard()
        expect(wrapper.find('[data-test="notifications-token-reveal"]').exists()).toBe(false)
    })
})

describe('NotificationsCard · 事件多选', () => {
    beforeEach(() => {
        setSettingMock.mockClear()
    })

    const EVENT_LABELS = en.settings.notifications.event as Record<string, string>

    function checkbox(wrapper: VueWrapper, eventName: string) {
        return wrapper.find<HTMLInputElement>(`[data-test="notifications-event-${eventName}"]`)
    }

    /// :checked is a DOM property binding, not an attribute — assert on the
    /// element property, not wrapper.attributes('checked').
    const isChecked = (wrapper: VueWrapper, eventName: string) =>
        checkbox(wrapper, eventName).element.checked

    it('renders one checkbox per event of the value domain with the stored selection', async () => {
        const { wrapper } = await mountCard({ events: 'downloaded,translation_failed' })

        for (const eventName of NOTIFY_EVENTS) {
            const box = checkbox(wrapper, eventName)
            expect(box.exists()).toBe(true)
            expect(wrapper.text()).toContain(EVENT_LABELS[eventName])
        }
        expect(isChecked(wrapper, 'downloaded')).toBe(true)
        expect(isChecked(wrapper, 'translation_failed')).toBe(true)
        // unlisted events are unchecked
        expect(isChecked(wrapper, 'upgraded')).toBe(false)
    })

    it('a blank stored row renders the server-side default pair', async () => {
        const { wrapper } = await mountCard({ events: '' })
        expect(isChecked(wrapper, 'downloaded')).toBe(true)
        expect(isChecked(wrapper, 'upgraded')).toBe(true)
        expect(isChecked(wrapper, 'job_failed')).toBe(false)
    })

    it('unchecking an event persists the remaining csv selection', async () => {
        const { wrapper } = await mountCard({ events: 'downloaded,upgraded' })

        await checkbox(wrapper, 'downloaded').setValue(false)
        await flushPromises()

        expect(setSettingMock).toHaveBeenCalledWith('notify_events', 'upgraded')
    })

    it('checking a new event persists the joined selection', async () => {
        const { wrapper } = await mountCard({ events: 'downloaded' })

        await checkbox(wrapper, 'job_failed').setValue(true)
        await flushPromises()

        expect(setSettingMock).toHaveBeenCalledWith('notify_events', 'downloaded,job_failed')
    })

    it('the last checked box stays checked — no empty selection is ever saved', async () => {
        const { wrapper, toastStore } = await mountCard({ events: 'upgraded' })

        await checkbox(wrapper, 'upgraded').setValue(false)
        await flushPromises()

        expect(setSettingMock).not.toHaveBeenCalled()
        expect(toastStore.toasts).toHaveLength(0)
        // the refused uncheck flips the box back honestly
        expect(isChecked(wrapper, 'upgraded')).toBe(true)
    })
})
