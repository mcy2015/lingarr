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
        <InputComponent
            v-else
            :id="field.key"
            v-model="fieldValue"
            :type="
                field.type === PLUGIN_SETTING_TYPE.SECRET ? INPUT_TYPE.PASSWORD : INPUT_TYPE.TEXT
            "
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
import { useSettingStore } from '@/cells/app/stores/setting'
import InputComponent from '@/cells/settings/components/InputComponent.vue'
import SelectComponent from '@/cells/kernel/components/SelectComponent.vue'

const pluginApi = pluginService(axios)

const props = defineProps<{
    field: IPluginSettingField
}>()

const { t } = useI18n()

const emit = defineEmits(['save'])

const settingsStore = useSettingStore()

const isValid = ref<boolean>(
    props.field.minLength == null && props.field.type !== PLUGIN_SETTING_TYPE.URL
)

const remoteOptions = ref<LabelValue[]>([])
const remoteError = ref<string | null>(null)
const remoteSelect = ref<SelectComponentExpose | null>(null)

/// 敏感值统一策略(输入型):Secret 编辑态不回显旧值 —— 服务端只回
/// 「已配置」布尔(不回明文),输入恒从空开始;空输入 = 「留空保持不变」,
/// 不写空串覆盖已存密钥。
const isSecret = computed(() => props.field.type === PLUGIN_SETTING_TYPE.SECRET)
const secretConfigured = computed(() => {
    if (!isSecret.value) return false
    return settingsStore.isEncryptedSettingConfigured(
        props.field.key as keyof IEncryptedSettingStates
    )
})

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
        // Secret 永不回显:输入框恒从空开始,占位符说明「留空保持不变」
        if (isSecret.value) return ''
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
        if (isSecret.value) {
            // 空输入 = 保持已配置的密钥不变,不做任何写入
            if (newValue === '') return
            settingsStore.updateEncryptedSetting(
                props.field.key as keyof IEncryptedSettingStates,
                newValue,
                isValid.value
            )
        } else {
            settingsStore.updateSetting(props.field.key as keyof ISettings, newValue, isValid.value)
        }
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
