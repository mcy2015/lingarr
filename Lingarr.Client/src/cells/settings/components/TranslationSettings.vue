<template>
    <!-- ═══ 请求与重试行式卡(原型 screen-06 A 组 setlist 形态):批量/超时/
         重试/退避全量保留,自 ServicesPage 平铺卡片迁为行式。 -->
    <div class="border-line bg-secondary max-w-[640px] rounded-md border" data-test="requests-card">
        <div class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-3">
            <h2 class="text-[13px] font-semibold">{{ t('settings.requests.title') }}</h2>
            <span class="text-secondary-content ml-auto text-[11px]">
                {{ t('settings.translation.description') }}
            </span>
        </div>

        <div
            v-for="row in rows"
            :key="row.key"
            class="border-line flex flex-wrap items-center gap-x-3 gap-y-2 border-b px-4 py-2.5 last:border-b-0"
            :data-test="`req-row-${row.key}`">
            <div class="min-w-0 flex-1">
                <b class="text-[13px] font-semibold">{{ row.title }}</b>
                <div class="text-secondary-content mt-0.5 text-[11.5px] leading-snug">
                    {{ row.description }}
                </div>
            </div>
            <div class="flex flex-none items-center gap-2">
                <ToggleButton v-if="row.kind === 'toggle'" v-model="row.model.value">
                    <span class="text-primary-content text-xs font-medium">
                        {{ row.model.value == 'true' ? t('settings.status.enabled') : t('settings.status.disabled') }}
                    </span>
                </ToggleButton>
                <template v-else>
                    <InputComponent
                        v-model="row.model.value"
                        class="w-28"
                        :validation-type="INPUT_VALIDATION_TYPE.NUMBER" />
                </template>
            </div>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed, reactive } from 'vue'
import { useI18n } from 'vue-i18n'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { INPUT_VALIDATION_TYPE, SERVICE_TYPE, SETTINGS } from '@/types'
import InputComponent from '@/cells/settings/components/InputComponent.vue'
import ToggleButton from '@/cells/kernel/components/ToggleButton.vue'

const settingsStore = useSettingStore()
const toastStore = useToastStore()
const { t } = useI18n()

const saved = (): void => toastStore.push({ message: t('common.saved'), type: 'success' })
const isValid = reactive({
    maxBatchSize: true,
    requestTimeout: true,
    maxRetries: true,
    retryDelay: true,
    retryDelayMultiplier: true
})

const serviceType = computed(() => {
    try {
        const raw = settingsStore.getSetting(SETTINGS.SERVICE_TYPE) as string
        const services: string[] = JSON.parse(raw)
        return services[0] ?? ''
    } catch {
        return ''
    }
})

const batchServices = [
    SERVICE_TYPE.ANTHROPIC,
    SERVICE_TYPE.DEEPSEEK,
    SERVICE_TYPE.GEMINI,
    SERVICE_TYPE.LOCALAI,
    SERVICE_TYPE.MISTRAL,
    SERVICE_TYPE.OPENAI,
    SERVICE_TYPE.XAI
] as readonly string[]

const useBatchTranslation = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.USE_BATCH_TRANSLATION) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.USE_BATCH_TRANSLATION, newValue, true)
        saved()
    }
})

const maxBatchSize = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.MAX_BATCH_SIZE) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.MAX_BATCH_SIZE, newValue, isValid.maxBatchSize)
        saved()
    }
})

const requestTimeout = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.REQUEST_TIMEOUT) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.REQUEST_TIMEOUT, newValue, isValid.requestTimeout)
        saved()
    }
})

const maxRetries = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.MAX_RETRIES) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.MAX_RETRIES, newValue, isValid.maxRetries)
        saved()
    }
})

const retryDelay = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.RETRY_DELAY) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.RETRY_DELAY, newValue, isValid.retryDelay)
        saved()
    }
})

const retryDelayMultiplier = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.RETRY_DELAY_MULTIPLIER) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.RETRY_DELAY_MULTIPLIER, newValue, isValid.retryDelayMultiplier)
        saved()
    }
})

interface IRow {
    key: string
    kind: 'toggle' | 'input'
    title: string
    description: string
    model: { value: string }
}

/// 行序 = 原卡片字段序:批量(仅 AI 主服务)→ 批大小(批量开)→ 超时 → 重试 → 退避
const rows = computed<IRow[]>(() => {
    const out: IRow[] = []
    if (batchServices.includes(serviceType.value)) {
        out.push({
            key: 'useBatch',
            kind: 'toggle',
            title: t('settings.translation.useBatch'),
            description: t('settings.translation.useBatchDescription'),
            model: useBatchTranslation
        })
        if (useBatchTranslation.value == 'true') {
            out.push({
                key: 'batchSize',
                kind: 'input',
                title: t('settings.translation.batchSize'),
                description: t('settings.translation.batchSizeDescription'),
                model: maxBatchSize
            })
        }
    }
    out.push(
        {
            key: 'requestTimeout',
            kind: 'input',
            title: t('settings.translation.requestTimeout'),
            description: t('settings.translation.requestTimeoutDescription'),
            model: requestTimeout
        },
        {
            key: 'maxRetries',
            kind: 'input',
            title: t('settings.translation.maxRetries'),
            description: t('settings.translation.maxRetriesDescription'),
            model: maxRetries
        },
        {
            key: 'retryDelay',
            kind: 'input',
            title: t('settings.translation.retryDelay'),
            description: t('settings.translation.retryDelayDescription'),
            model: retryDelay
        },
        {
            key: 'retryDelayMultiplier',
            kind: 'input',
            title: t('settings.translation.retryDelayMultiplier'),
            description: t('settings.translation.retryDelayMultiplierDescription'),
            model: retryDelayMultiplier
        }
    )
    return out
})
</script>
