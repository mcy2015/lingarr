<template>
    <!-- ═══ 通知卡(B11,系统 tab):总开关 + webhook 地址 + Telegram 令牌(reveal,
         留空保持)+ 会话 ID + 事件多选(notify_events 值域,四项,NATIVE checkbox)。
         敏感值永不回显明文(WhisperCard 形态);平面字段 blur 即存。 -->
    <div class="border-line bg-secondary rounded-md border" data-test="notifications-card">
        <div class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-3">
            <h2 class="text-[13px] font-semibold">{{ t('settings.notifications.title') }}</h2>
            <span
                class="dot flex-none"
                :class="channelConfigured ? 'bg-success' : 'bg-line'"
                data-test="notifications-dot"></span>
            <span class="text-secondary-content ml-auto text-[11px]">
                {{ t('settings.notifications.meta') }}
            </span>
        </div>

        <div class="flex flex-col gap-3.5 px-4 py-3.5">
            <!-- 总开关:开关即存 'true'/'false'(PlexCard 刷新开关形态) -->
            <ToggleButton v-model="enabledModel" size="small">
                <span class="text-primary-content text-xs font-medium">
                    {{ t('settings.notifications.enabled') }}
                </span>
                <span class="text-secondary-content/70 block text-[11px]">
                    {{ t('settings.notifications.enabledHint') }}
                </span>
            </ToggleButton>

            <label class="flex flex-col gap-1 text-[12.5px]">
                <span class="text-secondary-content">
                    {{ t('settings.notifications.webhookUrl') }}
                </span>
                <input
                    v-model="webhookUrl"
                    type="text"
                    class="border-line bg-tertiary text-primary-content mono rounded-md border px-2.5 py-1.5 text-xs focus:border-accent focus:outline-none"
                    :placeholder="t('settings.notifications.webhookPlaceholder')"
                    data-test="notifications-webhook"
                    @blur="saveWebhook" />
            </label>

            <div class="flex flex-col gap-1 text-[12.5px]">
                <span class="text-secondary-content">
                    {{ t('settings.notifications.telegramToken') }}
                </span>
                <div class="flex gap-1.5">
                    <input
                        :type="revealedToken !== null ? 'text' : 'password'"
                        v-model="tokenDraft"
                        class="border-line bg-tertiary text-primary-content mono min-w-0 flex-1 rounded-md border px-2.5 py-1.5 text-xs focus:border-accent focus:outline-none"
                        :placeholder="
                            tokenConfigured
                                ? t('settings.notifications.telegramTokenConfigured')
                                : t('settings.notifications.telegramTokenPlaceholder')
                        "
                        data-test="notifications-token"
                        @blur="saveToken"
                        @keydown.enter.prevent="saveToken" />
                    <button
                        v-if="tokenConfigured"
                        type="button"
                        class="border-line text-secondary-content hover:text-primary-content flex-none cursor-pointer rounded-md border px-2 text-xs transition-colors disabled:cursor-not-allowed disabled:opacity-40"
                        :disabled="revealing"
                        :title="revealedToken !== null ? t('common.hide') : t('common.show')"
                        :aria-label="revealedToken !== null ? t('common.hide') : t('common.show')"
                        data-test="notifications-token-reveal"
                        @click="toggleReveal">
                        <Icon
                            :name="revealedToken !== null ? 'eyeOn' : 'eyeOff'"
                            class="h-4 w-4" />
                    </button>
                </div>
            </div>

            <label class="flex flex-col gap-1 text-[12.5px]">
                <span class="text-secondary-content">
                    {{ t('settings.notifications.telegramChatId') }}
                </span>
                <input
                    v-model="chatId"
                    type="text"
                    class="border-line bg-tertiary text-primary-content mono rounded-md border px-2.5 py-1.5 text-xs focus:border-accent focus:outline-none"
                    :placeholder="t('settings.notifications.chatIdPlaceholder')"
                    data-test="notifications-chat-id"
                    @blur="saveChatId" />
            </label>

            <!-- 事件多选:notify_events 的值域即 NOTIFY_EVENTS;最后一项保持
                 勾选(空选择会被服务端回退成默认,UI 不允许存出歧义)。 -->
            <div class="flex flex-col gap-1.5 text-[12.5px]">
                <span class="text-secondary-content">
                    {{ t('settings.notifications.events') }}
                </span>
                <div class="flex flex-wrap gap-x-4 gap-y-1.5">
                    <label
                        v-for="event in NOTIFY_EVENTS"
                        :key="event"
                        class="flex cursor-pointer items-center gap-1.5 text-xs">
                        <input
                            type="checkbox"
                            class="border-line bg-tertiary accent-accent h-3.5 w-3.5 cursor-pointer"
                            :checked="selectedEvents.has(event)"
                            :data-test="`notifications-event-${event}`"
                            @change="toggleEvent(event, $event)" />
                        <span class="text-primary-content">{{ t(`settings.notifications.event.${event}`) }}</span>
                    </label>
                </div>
                <span class="text-secondary-content/70 text-[11px]">
                    {{ t('settings.notifications.eventsHint') }}
                </span>
            </div>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import axios from 'axios'
import { ENCRYPTED_SETTINGS, ISettings, NOTIFY_EVENTS, NotifyEvent, SETTINGS } from '@/types'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { settingService } from '@/cells/app/api/settingService'
import ToggleButton from '@/cells/kernel/components/ToggleButton.vue'
import Icon from '@/cells/kernel/components/icons/Icon.vue'

const { t } = useI18n()
const settingStore = useSettingStore()
const toastStore = useToastStore()
const settingApi = settingService(axios)

const webhookUrl = ref('')
const chatId = ref('')
const tokenDraft = ref('')
const revealedToken = ref<string | null>(null)
const revealing = ref(false)

onMounted(() => {
    webhookUrl.value = (settingStore.getSetting(SETTINGS.NOTIFY_WEBHOOK_URL) as string) ?? ''
    chatId.value = (settingStore.getSetting(SETTINGS.NOTIFY_TELEGRAM_CHAT_ID) as string) ?? ''
})

/// Channel wired = webhook URL stored, or the Telegram pair (token + chat
/// id) complete — the same condition the server's dispatch gates on.
const tokenConfigured = computed(
    () => settingStore.encrypted_settings[ENCRYPTED_SETTINGS.NOTIFY_TELEGRAM_BOT_TOKEN] === true
)
const channelConfigured = computed(() => {
    const webhookWired = webhookUrl.value.trim() !== ''
    const telegramWired = tokenConfigured.value && chatId.value.trim() !== ''
    return webhookWired || telegramWired
})

/// Plain fields persist on blur (WhisperCard pattern) — blank stays blank
/// (an empty row simply switches that channel off server-side).
async function persist(key: keyof ISettings, value: string): Promise<void> {
    try {
        await settingStore.updateSetting(key, value, true)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    } catch {
        toastStore.push({ message: t('common.saveFailed'), type: 'error' })
    }
}

const saveWebhook = () => void persist(SETTINGS.NOTIFY_WEBHOOK_URL, webhookUrl.value.trim())
const saveChatId = () => void persist(SETTINGS.NOTIFY_TELEGRAM_CHAT_ID, chatId.value.trim())

/// Master switch: toggling persists notifications_enabled immediately.
const enabledModel = computed({
    get: (): string =>
        settingStore.getSetting(SETTINGS.NOTIFICATIONS_ENABLED) === 'true' ? 'true' : 'false',
    set: (newValue: string | boolean): void => {
        const value = newValue.toString() === 'true' ? 'true' : 'false'
        settingStore.updateSetting(SETTINGS.NOTIFICATIONS_ENABLED, value, true)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})

/// Event selection: split of the stored csv; blank/missing mirrors the
/// server-side fallback (the seeded default pair).
const selectedEvents = computed<Set<string>>(() => {
    const raw = ((settingStore.getSetting(SETTINGS.NOTIFY_EVENTS) as string) ?? '').trim()
    if (raw === '') return new Set<string>(['downloaded', 'upgraded'])
    const selected = raw
        .split(',')
        .map((entry) => entry.trim())
        .filter((entry) => entry !== '')
    return new Set<string>(selected.length > 0 ? selected : ['downloaded', 'upgraded'])
})

async function toggleEvent(event: NotifyEvent, changeEvent: Event): Promise<void> {
    const next = new Set(selectedEvents.value)
    if (next.has(event)) {
        if (next.size <= 1) {
            // The last checked box stays checked: an empty selection would
            // be re-expanded to the server default on the next read, so the
            // UI never saves the ambiguous state — flip the box back
            // honestly instead.
            (changeEvent.target as HTMLInputElement).checked = true
            return
        }

        next.delete(event)
    } else {
        next.add(event)
    }
    await persist(SETTINGS.NOTIFY_EVENTS, [...next].join(','))
}

/// Token: explicit commit (blur / Enter); an empty draft never overwrites
/// the stored secret; the draft never echoes the stored value.
async function saveToken(): Promise<void> {
    const draft = tokenDraft.value.trim()
    if (draft === '') return
    tokenDraft.value = ''
    try {
        await settingApi.setEncryptedSetting(ENCRYPTED_SETTINGS.NOTIFY_TELEGRAM_BOT_TOKEN, draft)
        settingStore.setEncryptedSettingState(ENCRYPTED_SETTINGS.NOTIFY_TELEGRAM_BOT_TOKEN, true)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    } catch {
        toastStore.push({ message: t('common.saveFailed'), type: 'error' })
    }
}

/// Reveal: fetch the stored secret on demand; hide without a second request.
async function toggleReveal(): Promise<void> {
    if (!tokenConfigured.value || revealing.value) return
    if (revealedToken.value !== null) {
        revealedToken.value = null
        tokenDraft.value = ''
        return
    }
    revealing.value = true
    try {
        revealedToken.value = await settingApi.revealEncryptedSetting(
            ENCRYPTED_SETTINGS.NOTIFY_TELEGRAM_BOT_TOKEN
        )
        tokenDraft.value = revealedToken.value
    } catch {
        toastStore.push({ message: t('common.loadFailed'), type: 'error' })
    } finally {
        revealing.value = false
    }
}
</script>

<style scoped>
.dot {
    width: 7px;
    height: 7px;
    border-radius: 50%;
    display: inline-block;
}
</style>
