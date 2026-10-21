<template>
    <!-- ═══ Plex 刷新卡(B03,媒体源 tab):行式状态(dot + 地址 + 刷新开关态 +
         编辑),编辑模态照 MediaSourcesCard 的 Teleport 形态 —— 地址走
         InputComponent URL 校验,token 走 trailing 插槽 reveal + blur/Enter
         显式提交(留空保持不变),刷新开关 ToggleButton。敏感值永不回显明文。 -->
    <div class="border-line bg-secondary rounded-md border" data-test="plex-card">
        <div class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-3">
            <h2 class="text-[13px] font-semibold">{{ t('settings.plex.title') }}</h2>
            <span
                class="dot flex-none"
                :class="configured ? 'bg-success' : 'bg-line'"
                data-test="plex-dot"></span>
            <span class="text-secondary-content ml-auto text-[11px]">
                {{ t('settings.plex.meta') }}
            </span>
        </div>

        <div
            class="flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-2.5 text-[13px]"
            data-test="plex-row">
            <b class="flex-none">Plex</b>
            <span
                class="text-secondary-content mono min-w-0 flex-1 truncate text-xs"
                :title="hint">
                {{ hint }}
            </span>
            <span
                class="text-secondary-content flex-none text-[11px]"
                data-test="plex-refresh-state">
                {{ refreshEnabled ? t('settings.plex.refreshOn') : t('settings.plex.refreshOff') }}
            </span>
            <button
                type="button"
                class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                data-test="edit-plex"
                @click="editing = true">
                {{ t('settings.plex.edit') }}
            </button>
        </div>

        <!-- 编辑模态:地址 + token(reveal)+ 刷新开关 -->
        <Teleport to="body">
            <Transition
                enter-active-class="transition-opacity duration-200 ease-in-out"
                leave-active-class="transition-opacity duration-200 ease-in-out"
                enter-from-class="opacity-0"
                leave-to-class="opacity-0">
                <div
                    v-if="editing"
                    class="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4"
                    data-test="plex-modal"
                    @click="editing = false"
                    @keydown.escape="editing = false">
                    <div
                        class="bg-secondary border-line shell-scroll max-h-[85vh] w-full max-w-md overflow-y-auto rounded-lg border p-5"
                        role="dialog"
                        aria-modal="true"
                        @click.stop>
                        <div class="mb-4 flex items-center gap-2.5">
                            <h2 class="text-[15px] font-semibold">{{ t('settings.plex.title') }}</h2>
                            <button
                                type="button"
                                class="border-line text-primary-content hover:bg-tertiary ml-auto cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                                @click="editing = false">
                                {{ t('common.close') }}
                            </button>
                        </div>
                        <div class="space-y-3">
                            <InputComponent
                                v-model="urlModel"
                                :validation-type="INPUT_VALIDATION_TYPE.URL"
                                :label="t('settings.plex.url')"
                                :placeholder="t('settings.plex.urlPlaceholder')"
                                :error-message="t('settings.plex.urlError')"
                                @update:validation="(val) => (urlValid = val)" />
                            <!-- token:reveal 按钮经 trailing 插槽进框内;保存改为
                                 blur / Enter 显式提交,空输入不覆盖已存令牌。 -->
                            <InputComponent
                                v-model="tokenDraft"
                                :type="revealedToken !== null ? INPUT_TYPE.TEXT : INPUT_TYPE.PASSWORD"
                                :validation-type="INPUT_VALIDATION_TYPE.STRING"
                                :label="t('settings.plex.token')"
                                :placeholder="
                                    tokenConfigured ? t('settings.plex.tokenConfigured') : t('settings.plex.tokenPlaceholder')
                                "
                                :debounce="0"
                                @blur="commitToken"
                                @submit="commitToken">
                                <template #trailing>
                                    <button
                                        v-if="tokenConfigured"
                                        type="button"
                                        class="hover:bg-tertiary flex h-6 w-6 cursor-pointer items-center justify-center rounded-sm transition-colors disabled:cursor-not-allowed disabled:opacity-40"
                                        :disabled="revealing"
                                        :title="revealedToken !== null ? t('common.hide') : t('common.show')"
                                        :aria-label="revealedToken !== null ? t('common.hide') : t('common.show')"
                                        data-test="plex-token-visibility"
                                        @click="toggleTokenReveal">
                                        <Icon
                                            :name="revealedToken !== null ? 'eyeOn' : 'eyeOff'"
                                            class="h-4 w-4" />
                                    </button>
                                </template>
                            </InputComponent>
                            <div class="flex items-center space-x-2 pt-1">
                                <ToggleButton v-model="refreshEnabledModel">
                                    <span class="text-primary-content text-xs font-medium">
                                        {{ t('settings.plex.refresh') }}
                                    </span>
                                    <span class="text-secondary-content/70 block text-[11px]">
                                        {{ t('settings.plex.refreshHint') }}
                                    </span>
                                </ToggleButton>
                            </div>
                        </div>
                    </div>
                </div>
            </Transition>
        </Teleport>
    </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import axios from 'axios'
import { ENCRYPTED_SETTINGS, INPUT_TYPE, INPUT_VALIDATION_TYPE, SETTINGS } from '@/types'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { settingService } from '@/cells/app/api/settingService'
import InputComponent from '@/cells/settings/components/InputComponent.vue'
import ToggleButton from '@/cells/kernel/components/ToggleButton.vue'
import Icon from '@/cells/kernel/components/icons/Icon.vue'

const { t } = useI18n()
const settingStore = useSettingStore()
const toastStore = useToastStore()
const settingApi = settingService(axios)

const editing = ref(false)
const urlValid = ref(false)

const url = computed(() => (settingStore.getSetting(SETTINGS.PLEX_URL) as string) ?? '')
const refreshEnabled = computed(
    () => settingStore.getSetting(SETTINGS.PLEX_REFRESH_ENABLED) === 'true'
)
const tokenConfigured = computed(
    () => settingStore.isEncryptedSettingConfigured(ENCRYPTED_SETTINGS.PLEX_TOKEN)
)
const configured = computed(() => url.value.trim() !== '' && tokenConfigured.value)
const hint = computed(() =>
    configured.value ? url.value : t('settings.plex.notConfigured')
)

/// 地址保存:校验通过才落库(toast「已保存」),无效输入停在框里不写。
const urlModel = computed({
    get: (): string => url.value,
    set: (newValue: string): void => {
        settingStore.updateSetting(SETTINGS.PLEX_URL, newValue, urlValid.value)
        if (urlValid.value) {
            toastStore.push({ message: t('common.saved'), type: 'success' })
        }
    }
})

/// 刷新开关:开关即存 'true'/'false'(布尔语义,无校验概念)。
const refreshEnabledModel = computed({
    get: (): string => (refreshEnabled.value ? 'true' : 'false'),
    set: (newValue: string | boolean): void => {
        const value = newValue.toString() === 'true' ? 'true' : 'false'
        settingStore.updateSetting(SETTINGS.PLEX_REFRESH_ENABLED, value, true)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})

// ── token:输入草稿 + 显式提交 + 按需 reveal ─────────────────────────
const tokenDraft = ref('')
const revealedToken = ref<string | null>(null)
const revealing = ref(false)
const tokenCommitting = ref(false)

watch(editing, () => {
    // 关闭(或重开)模态即收回已显示的明文并清空草稿 —— 显示态只属于打开的模态。
    revealedToken.value = null
    tokenDraft.value = ''
})

async function toggleTokenReveal(): Promise<void> {
    if (!tokenConfigured.value || revealing.value) return
    if (revealedToken.value !== null) {
        revealedToken.value = null
        tokenDraft.value = ''
        return
    }
    revealing.value = true
    try {
        revealedToken.value = await settingApi.revealEncryptedSetting(ENCRYPTED_SETTINGS.PLEX_TOKEN)
        tokenDraft.value = revealedToken.value
    } catch {
        toastStore.push({ message: t('common.loadFailed'), type: 'error' })
    } finally {
        revealing.value = false
    }
}

/// blur / Enter 才提交;空输入 = 保持已存令牌不变,不写空串覆盖。
async function commitToken(): Promise<void> {
    if (tokenCommitting.value) return
    const value = tokenDraft.value.trim()
    if (value === '') return
    tokenCommitting.value = true
    try {
        const ok = await settingStore.updateEncryptedSetting(ENCRYPTED_SETTINGS.PLEX_TOKEN, value)
        if (ok) toastStore.push({ message: t('common.saved'), type: 'success' })
    } finally {
        tokenCommitting.value = false
        tokenDraft.value = revealedToken.value ?? ''
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
