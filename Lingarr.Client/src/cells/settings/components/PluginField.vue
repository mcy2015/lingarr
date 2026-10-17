<template>
    <div>
        <template v-if="field.type === PLUGIN_SETTING_TYPE.REMOTE_DROPDOWN">
            <label class="mb-1 block text-sm font-semibold" :for="field.key">
                {{ field.label }}
                <span v-if="field.required" class="text-danger" aria-hidden="true">*</span>
            </label>
            <SelectComponent
                ref="remoteSelect"
                :options="remoteOptions"
                :selected="fieldValue"
                :load-on-open="true"
                :placeholder="t('services.plugins.selectOption')"
                :no-options="remoteError ?? t('services.plugins.loadingOptions')"
                @update:selected="(value: string) => (fieldValue = value)"
                @fetch-options="loadRemoteOptions" />
        </template>
        <!-- secret 行(#1B/#7/#8):reveal 按钮经 trailing 插槽进框内原生位置
             (框外按钮移除,本地显隐/验证图标由 trailing 让位),保存改为
             blur / Enter 显式提交 —— 半截输入不再触发写请求刷屏。 -->
        <template v-if="isSecret">
            <InputComponent
                :id="field.key"
                v-model="secretDraft"
                :type="revealedSecret !== null ? INPUT_TYPE.TEXT : INPUT_TYPE.PASSWORD"
                :label="field.label"
                :placeholder="secretPlaceholder"
                :validation-type="validationType"
                :min-length="field.minLength ?? undefined"
                :error-message="field.validationErrorMessage ?? undefined"
                :debounce="0"
                @update:validation="(value: boolean) => (isValid = value)"
                @blur="commitSecret"
                @submit="commitSecret">
                <template #trailing>
                    <button
                        v-if="secretConfigured"
                        type="button"
                        class="hover:bg-tertiary flex h-6 w-6 cursor-pointer items-center justify-center rounded-sm transition-colors disabled:cursor-not-allowed disabled:opacity-40"
                        :disabled="revealing"
                        :title="revealedSecret !== null ? t('common.hide') : t('common.show')"
                        :aria-label="revealedSecret !== null ? t('common.hide') : t('common.show')"
                        :data-test="`secret-visibility-${field.key}`"
                        @click="toggleSecretReveal()">
                        <Icon
                            :name="revealedSecret !== null ? 'eyeOn' : 'eyeOff'"
                            class="h-4 w-4" />
                    </button>
                </template>
            </InputComponent>
            <!-- #3 清除凭据:空串写回 = 既有服务端清除语义,后端零改动 -->
            <div v-if="secretConfigured" class="mt-1.5">
                <button
                    type="button"
                    class="border-danger/50 text-danger hover:bg-danger/10 focus-visible:ring-danger cursor-pointer rounded-md border px-2.5 py-1 text-xs font-medium transition-colors focus-visible:ring-2 focus-visible:outline-none disabled:cursor-not-allowed disabled:opacity-50"
                    :disabled="clearing"
                    :data-test="`secret-clear-${field.key}`"
                    @click="clearCredential">
                    {{ t('services.plugins.clearCredential') }}
                </button>
            </div>
        </template>
        <InputComponent
            v-else
            :id="field.key"
            v-model="fieldValue"
            :type="INPUT_TYPE.TEXT"
            :label="field.label"
            :placeholder="secretPlaceholder"
            :validation-type="validationType"
            :min-length="field.minLength ?? undefined"
            :error-message="field.validationErrorMessage ?? undefined"
            @update:validation="(value: boolean) => (isValid = value)" />
        <div v-if="field.description" class="mt-1 text-xs opacity-60">
            {{ field.description }}
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import {
    IEncryptedSettingStates,
    INPUT_TYPE,
    INPUT_VALIDATION_TYPE,
    InputValidationType,
    IPluginSettingField,
    ISettings,
    LabelValue,
    PLUGIN_SETTING_TYPE,
    SelectComponentExpose
} from '@/types'
import axios from 'axios'
import { pluginService } from '@/cells/settings/services/pluginService'
import { settingService } from '@/cells/app/api/settingService'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import InputComponent from '@/cells/settings/components/InputComponent.vue'
import SelectComponent from '@/cells/kernel/components/SelectComponent.vue'
import Icon from '@/cells/kernel/components/icons/Icon.vue'

const pluginApi = pluginService(axios)
const settingApi = settingService(axios)

const props = defineProps<{
    field: IPluginSettingField
}>()

const { t } = useI18n()

const emit = defineEmits(['save', 'refresh'])

const settingsStore = useSettingStore()
const toastStore = useToastStore()

const isValid = ref<boolean>(
    props.field.minLength == null && props.field.type !== PLUGIN_SETTING_TYPE.URL
)

const remoteOptions = ref<LabelValue[]>([])
const remoteError = ref<string | null>(null)
const remoteSelect = ref<SelectComponentExpose | null>(null)

/// 敏感值统一策略(输入型):Secret 编辑态不回显旧值 —— 服务端只回
/// 「已配置」布尔(不回明文),输入恒从空开始;空输入 = 「留空保持不变」,
/// 不写空串覆盖已存密钥。「显示」为用户显式动作:经按 key 的 reveal
/// 端点取回明文填入输入框(cookie 会话专用,服务端留审计)。
const isSecret = computed(() => props.field.type === PLUGIN_SETTING_TYPE.SECRET)
const secretConfigured = computed(() => {
    if (!isSecret.value) return false
    return settingsStore.isEncryptedSettingConfigured(
        props.field.key as keyof IEncryptedSettingStates
    )
})

const revealedSecret = ref<string | null>(null)
const revealing = ref(false)
const clearing = ref(false)
const secretCommitting = ref(false)

/// #8:secret 输入只进本地草稿,blur / Enter(commitSecret)才落库 ——
/// 输入过程中的停顿不再触发防抖写请求。清除/隐藏后草稿同步回空。
const secretDraft = ref('')

async function toggleSecretReveal(): Promise<void> {
    if (!secretConfigured.value || revealing.value) return
    if (revealedSecret.value !== null) {
        revealedSecret.value = null
        secretDraft.value = ''
        return
    }
    revealing.value = true
    try {
        revealedSecret.value = await settingApi.revealEncryptedSetting(props.field.key)
        secretDraft.value = revealedSecret.value
    } catch {
        toastStore.push({ message: t('common.loadFailed'), type: 'error' })
    } finally {
        revealing.value = false
    }
}

/// #2/#8:显式提交(blur 或 Enter)。空输入 = 保持已配置的密钥不变,不写入;
/// updateEncryptedSetting 落库成功(resolve true)才 emit('save),让父级
/// 的「已保存」toast 与卡片刷新只发生在真实成功之后。提交后输入框回到
/// 空(或已显示的明文),占位符说明「留空保持不变」——与原 computed 语义一致。
async function commitSecret(): Promise<void> {
    if (!isSecret.value || secretCommitting.value) return
    const value = secretDraft.value
    if (value === '') return
    secretCommitting.value = true
    try {
        const ok = await settingsStore.updateEncryptedSetting(
            props.field.key as keyof IEncryptedSettingStates,
            value,
            isValid.value
        )
        if (ok) emit('save')
    } finally {
        secretCommitting.value = false
        secretDraft.value = revealedSecret.value ?? ''
    }
}

/// #3:清除凭据 = 显式动作(confirm 走仓库既有模式,同 QueuePage 批量转换),
/// 空串写回加密设置即服务端既有清除语义。成功:镜像置未配置 + emit('refresh')
/// 刷新卡背徽章(refresh 不触发「已保存」toast——清除有自己的成功文案);
/// 失败仅 toast,状态保持。
async function clearCredential(): Promise<void> {
    if (!secretConfigured.value || clearing.value) return
    if (!window.confirm(t('services.plugins.clearConfirmBody'))) return
    clearing.value = true
    try {
        await settingApi.setEncryptedSetting(props.field.key, '')
        revealedSecret.value = null
        secretDraft.value = ''
        settingsStore.setEncryptedSettingState(
            props.field.key as keyof IEncryptedSettingStates,
            false
        )
        toastStore.push({ message: t('services.plugins.credentialCleared'), type: 'success' })
        emit('refresh')
    } catch {
        toastStore.push({ message: t('services.plugins.clearFailed'), type: 'error' })
    } finally {
        clearing.value = false
    }
}

const secretPlaceholder = computed(() => {
    if (!isSecret.value) return props.field.default ?? ''
    return secretConfigured.value
        ? t('services.plugins.secretConfigured')
        : (props.field.default ?? '')
})

const validationType = computed<InputValidationType | undefined>(() => {
    if (props.field.type === PLUGIN_SETTING_TYPE.URL) {
        return INPUT_VALIDATION_TYPE.URL
    }
    if (props.field.minLength != null) {
        return INPUT_VALIDATION_TYPE.STRING
    }
    return undefined
})

const fieldValue = computed<string>({
    get: (): string => {
        const stored = settingsStore.getSetting(props.field.key as keyof ISettings)
        if (typeof stored === 'string') {
            return stored
        }
        if (typeof stored === 'number' || typeof stored === 'boolean') {
            return String(stored)
        }
        return props.field.default ?? ''
    },
    set: (newValue: string): void => {
        settingsStore.updateSetting(props.field.key as keyof ISettings, newValue, isValid.value)
        if (isValid.value) {
            emit('save')
        }
    }
})

async function loadRemoteOptions(): Promise<void> {
    if (!props.field.optionsEndpoint) {
        remoteError.value = t('services.plugins.noOptionsEndpoint')
        remoteSelect.value?.setLoadingState(false)
        return
    }

    try {
        remoteError.value = null
        const response = await pluginApi.getOptions(props.field.optionsEndpoint)
        remoteOptions.value = response.options ?? []
        if (remoteOptions.value.length === 0) {
            remoteError.value = response.message ?? t('services.plugins.noOptionsReturned')
        }
    } catch (error) {
        console.error('Failed to load options:', error)
        remoteError.value = t('services.plugins.optionsLoadError')
    } finally {
        remoteSelect.value?.setLoadingState(false)
    }
}
</script>
