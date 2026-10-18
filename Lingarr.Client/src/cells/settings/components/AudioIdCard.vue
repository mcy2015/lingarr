<template>
    <!-- ═══ 音轨识别卡(走查 C/D):远程 ASR 接线 —— 服务地址 / API key /
         模型,外加 mkvpropedit 标签回写开关(默认关;写媒体文件本体须显式授权)。
         未配置时详情页的「识别音轨」按钮会返回可读的 409 提示。 -->
    <div class="border-line bg-secondary rounded-md border" data-test="audio-id-card">
        <div class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-3">
            <h2 class="text-[13px] font-semibold">{{ t('settings.audioId.title') }}</h2>
            <span
                class="dot flex-none"
                :class="configured ? 'bg-success' : 'bg-line'"
                data-test="audio-id-dot"></span>
            <span class="text-secondary-content ml-auto text-[11px]">
                {{ t('settings.audioId.meta') }}
            </span>
        </div>

        <div class="flex flex-col gap-3 px-4 py-3.5">
            <label class="flex flex-col gap-1 text-[12.5px]">
                <span class="text-secondary-content">
                    {{ t('settings.audioId.url') }}
                </span>
                <input
                    v-model="url"
                    type="text"
                    class="border-line bg-tertiary text-primary-content mono rounded-md border px-2.5 py-1.5 text-xs focus:border-accent focus:outline-none"
                    :placeholder="t('settings.audioId.urlPlaceholder')"
                    data-test="audio-id-url"
                    @blur="saveUrl" />
            </label>

            <div class="flex flex-col gap-1 text-[12.5px]">
                <span class="text-secondary-content">
                    {{ t('settings.audioId.apiKey') }}
                </span>
                <div class="flex gap-1.5">
                    <input
                        :type="revealed ? 'text' : 'password'"
                        v-model="apiKeyDraft"
                        class="border-line bg-tertiary text-primary-content mono min-w-0 flex-1 rounded-md border px-2.5 py-1.5 text-xs focus:border-accent focus:outline-none"
                        :placeholder="keyConfigured ? t('settings.audioId.keySet') : t('settings.audioId.keyPlaceholder')"
                        data-test="audio-id-key"
                        @blur="saveKey" />
                    <button
                        type="button"
                        class="border-line text-secondary-content hover:text-primary-content flex-none cursor-pointer rounded-md border px-2 text-xs transition-colors"
                        :title="revealed ? t('common.hide') : t('common.show')"
                        data-test="audio-id-reveal"
                        @click="revealed = !revealed">
                        {{ revealed ? '🙈' : '👁' }}
                    </button>
                </div>
            </div>

            <label class="flex flex-col gap-1 text-[12.5px]">
                <span class="text-secondary-content">
                    {{ t('settings.audioId.model') }}
                </span>
                <input
                    v-model="model"
                    type="text"
                    class="border-line bg-tertiary text-primary-content mono rounded-md border px-2.5 py-1.5 text-xs focus:border-accent focus:outline-none"
                    placeholder="FunAudioLLM/SenseVoiceSmall"
                    data-test="audio-id-model"
                    @blur="saveModel" />
            </label>

            <label class="flex items-start gap-2 text-[12.5px]">
                <input
                    v-model="writeback"
                    type="checkbox"
                    class="accent-accent mt-0.5 cursor-pointer"
                    data-test="audio-id-writeback"
                    @change="saveWriteback" />
                <span>
                    {{ t('settings.audioId.writeback') }}
                    <span class="text-secondary-content/70 block text-[11px]">
                        {{ t('settings.audioId.writebackHint') }}
                    </span>
                </span>
            </label>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import axios from 'axios'
import { ENCRYPTED_SETTINGS, SETTINGS, IEncryptedSettingStates, ISettings } from '@/types'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { settingService } from '@/cells/app/api/settingService'

const { t } = useI18n()
const settingStore = useSettingStore()
const toastStore = useToastStore()
const settingApi = settingService(axios)

const url = ref('')
const model = ref('')
const writeback = ref(false)
const apiKeyDraft = ref('')
const revealed = ref(false)

const keyConfigured = computed(
    () => settingStore.encrypted_settings[ENCRYPTED_SETTINGS.AUDIO_ID_API_KEY] === true
)
const configured = computed(() => url.value.trim() !== '' && keyConfigured.value)

onMounted(() => {
    url.value = (settingStore.getSetting(SETTINGS.AUDIO_ID_URL) as string) ?? ''
    model.value =
        (settingStore.getSetting(SETTINGS.AUDIO_ID_MODEL) as string) || 'FunAudioLLM/SenseVoiceSmall'
    writeback.value = settingStore.getSetting(SETTINGS.AUDIO_ID_WRITEBACK) === 'true'
})

async function persistSetting(key: keyof ISettings, value: string): Promise<void> {
    try {
        await settingApi.setSetting(key, value)
        settingStore.updateSetting(key, value, true)
    } catch {
        toastStore.push({ message: t('common.saveFailed'), type: 'error' })
    }
}

async function persistEncrypted(key: keyof IEncryptedSettingStates, value: string): Promise<void> {
    try {
        await settingApi.setEncryptedSetting(key, value)
        settingStore.setEncryptedSettingState(key, value !== '')
        toastStore.push({ message: t('common.saved'), type: 'success' })
    } catch {
        toastStore.push({ message: t('common.saveFailed'), type: 'error' })
    }
}

const saveUrl = () => void persistSetting(SETTINGS.AUDIO_ID_URL as keyof ISettings, url.value.trim())
const saveModel = () =>
    void persistSetting(SETTINGS.AUDIO_ID_MODEL as keyof ISettings, model.value.trim())
const saveWriteback = () =>
    void persistSetting(SETTINGS.AUDIO_ID_WRITEBACK as keyof ISettings, writeback.value ? 'true' : 'false')
const saveKey = () => {
    const draft = apiKeyDraft.value.trim()
    if (draft === '') return
    apiKeyDraft.value = ''
    void persistEncrypted(ENCRYPTED_SETTINGS.AUDIO_ID_API_KEY as keyof IEncryptedSettingStates, draft)
}
</script>
