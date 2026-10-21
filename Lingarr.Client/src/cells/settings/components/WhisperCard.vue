<template>
    <!-- ═══ Whisper 转录兜底卡(B04,媒体源 tab):endpoint / model / API key(reveal,
         留空保持)三键齐备前该源不进 registry;连接测试走 POST
         /api/subtitle-search/test/whisper。敏感值永不回显明文(PlexCard 形态)。 -->
    <div class="border-line bg-secondary rounded-md border" data-test="whisper-card">
        <div class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-3">
            <h2 class="text-[13px] font-semibold">{{ t('settings.whisper.title') }}</h2>
            <span
                class="dot flex-none"
                :class="configured ? 'bg-success' : 'bg-line'"
                data-test="whisper-dot"></span>
            <span class="text-secondary-content ml-auto text-[11px]">
                {{ t('settings.whisper.meta') }}
            </span>
        </div>

        <div class="flex flex-col gap-3 px-4 py-3.5">
            <label class="flex flex-col gap-1 text-[12.5px]">
                <span class="text-secondary-content">
                    {{ t('settings.whisper.endpoint') }}
                </span>
                <input
                    v-model="endpoint"
                    type="text"
                    class="border-line bg-tertiary text-primary-content mono rounded-md border px-2.5 py-1.5 text-xs focus:border-accent focus:outline-none"
                    :placeholder="t('settings.whisper.endpointPlaceholder')"
                    data-test="whisper-endpoint"
                    @blur="saveEndpoint" />
            </label>

            <label class="flex flex-col gap-1 text-[12.5px]">
                <span class="text-secondary-content">
                    {{ t('settings.whisper.model') }}
                </span>
                <input
                    v-model="model"
                    type="text"
                    class="border-line bg-tertiary text-primary-content mono rounded-md border px-2.5 py-1.5 text-xs focus:border-accent focus:outline-none"
                    :placeholder="t('settings.whisper.modelPlaceholder')"
                    data-test="whisper-model"
                    @blur="saveModel" />
            </label>

            <div class="flex flex-col gap-1 text-[12.5px]">
                <span class="text-secondary-content">
                    {{ t('settings.whisper.apiKey') }}
                </span>
                <div class="flex gap-1.5">
                    <input
                        :type="revealedKey !== null ? 'text' : 'password'"
                        v-model="keyDraft"
                        class="border-line bg-tertiary text-primary-content mono min-w-0 flex-1 rounded-md border px-2.5 py-1.5 text-xs focus:border-accent focus:outline-none"
                        :placeholder="
                            keyConfigured
                                ? t('settings.whisper.keySet')
                                : t('settings.whisper.keyPlaceholder')
                        "
                        data-test="whisper-key"
                        @blur="saveKey" />
                    <button
                        v-if="keyConfigured"
                        type="button"
                        class="border-line text-secondary-content hover:text-primary-content flex-none cursor-pointer rounded-md border px-2 text-xs transition-colors disabled:cursor-not-allowed disabled:opacity-40"
                        :disabled="revealing"
                        :title="revealedKey !== null ? t('common.hide') : t('common.show')"
                        :aria-label="revealedKey !== null ? t('common.hide') : t('common.show')"
                        data-test="whisper-reveal"
                        @click="toggleReveal">
                        <Icon :name="revealedKey !== null ? 'eyeOn' : 'eyeOff'" class="h-4 w-4" />
                    </button>
                </div>
            </div>

            <div class="flex flex-wrap items-center gap-2 pt-1">
                <button
                    type="button"
                    class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors disabled:cursor-not-allowed disabled:opacity-40"
                    :disabled="!configured || testing"
                    :title="configured ? '' : t('settings.whisper.testDisabled')"
                    data-test="whisper-test"
                    @click="runTest">
                    {{ testing ? t('settings.subtitleSources.testing') : t('settings.whisper.test') }}
                </button>
                <span
                    v-if="testResult"
                    class="text-[11px]"
                    :class="testResult.ok ? 'text-success' : 'text-danger'"
                    :data-test="testResult.ok ? 'whisper-test-ok' : 'whisper-test-failed'">
                    {{ testResult.message }}
                </span>
            </div>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import axios from 'axios'
import { ENCRYPTED_SETTINGS, ISettings, SETTINGS } from '@/types'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { settingService } from '@/cells/app/api/settingService'
import { subtitleSearchService } from '@/cells/app/api/subtitleSearchService'
import Icon from '@/cells/kernel/components/icons/Icon.vue'

const { t } = useI18n()
const settingStore = useSettingStore()
const toastStore = useToastStore()
const settingApi = settingService(axios)

const endpoint = ref('')
const model = ref('')
const keyDraft = ref('')
const revealedKey = ref<string | null>(null)
const revealing = ref(false)
const testing = ref(false)
const testResult = ref<{ ok: boolean; message: string } | null>(null)

const keyConfigured = computed(
    () => settingStore.encrypted_settings[ENCRYPTED_SETTINGS.WHISPER_API_KEY] === true
)
/// Fully wired = endpoint + model + key: exactly the registry gate's
/// condition — until then the source is neither listed nor testable.
const configured = computed(
    () => endpoint.value.trim() !== '' && model.value.trim() !== '' && keyConfigured.value
)

onMounted(() => {
    endpoint.value = (settingStore.getSetting(SETTINGS.WHISPER_ENDPOINT) as string) ?? ''
    model.value = (settingStore.getSetting(SETTINGS.WHISPER_MODEL) as string) ?? ''
})

/// Plain fields persist on blur (PlexCard pattern — the store's own save
/// path posts and mirrors the value locally) — blank stays blank.
async function persist(key: keyof ISettings, value: string): Promise<void> {
    try {
        await settingStore.updateSetting(key, value, true)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    } catch {
        toastStore.push({ message: t('common.saveFailed'), type: 'error' })
    }
}

const saveEndpoint = () => void persist(SETTINGS.WHISPER_ENDPOINT, endpoint.value.trim())
const saveModel = () => void persist(SETTINGS.WHISPER_MODEL, model.value.trim())

/// Key: explicit commit (blur / Enter); an empty draft never overwrites the
/// stored secret; the draft never echoes the stored value.
async function saveKey(): Promise<void> {
    const draft = keyDraft.value.trim()
    if (draft === '') return
    keyDraft.value = ''
    try {
        await settingApi.setEncryptedSetting(ENCRYPTED_SETTINGS.WHISPER_API_KEY, draft)
        settingStore.setEncryptedSettingState(ENCRYPTED_SETTINGS.WHISPER_API_KEY, true)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    } catch {
        toastStore.push({ message: t('common.saveFailed'), type: 'error' })
    }
}

/// Reveal: fetch the stored secret on demand, hide without a second request.
async function toggleReveal(): Promise<void> {
    if (!keyConfigured.value || revealing.value) return
    if (revealedKey.value !== null) {
        revealedKey.value = null
        keyDraft.value = ''
        return
    }
    revealing.value = true
    try {
        revealedKey.value = await settingApi.revealEncryptedSetting(
            ENCRYPTED_SETTINGS.WHISPER_API_KEY
        )
        keyDraft.value = revealedKey.value
    } catch {
        toastStore.push({ message: t('common.loadFailed'), type: 'error' })
    } finally {
        revealing.value = false
    }
}

/// Connection test: the same per-source endpoint the subtitle-sources card
/// uses — the whisper provider probes GET {endpoint}/models.
async function runTest(): Promise<void> {
    if (!configured.value || testing.value) return
    testing.value = true
    testResult.value = null
    try {
        const result = await subtitleSearchService.testConnection<{
            ok: boolean
            message: string
        }>('whisper')
        testResult.value = result
    } catch (error) {
        const detail = axios.isAxiosError(error)
            ? ((error.response?.data as { message?: string } | undefined)?.message ?? error.message)
            : String(error)
        testResult.value = { ok: false, message: detail }
    } finally {
        testing.value = false
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
