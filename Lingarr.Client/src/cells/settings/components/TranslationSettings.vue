<template>
    <!-- ═══ 请求与重试行式卡(原型 screen-06 A 组 setlist 形态):批量/超时/
         重试/退避全量保留,自 ServicesPage 平铺卡片迁为行式。 -->
    <div class="border-line bg-secondary rounded-md border" data-test="requests-card">
        <div class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-3">
            <h2 class="text-[13px] font-semibold">{{ t('settings.requests.title') }}</h2>
            <span class="text-secondary-content ml-auto text-[11px]">
                {{ t('settings.translation.description') }}
            </span>
        </div>

        <!-- 行结构 = 原型 .setlist .row:行 13px,b 标题与 12px hint 同行内联,
             控件靠右;不再用「标题上/描述下」的两行堆叠形态。 -->
        <div
            v-for="row in rows"
            :key="row.key"
            class="border-line flex flex-wrap items-center gap-x-3 gap-y-1.5 border-b px-4 py-2.5 text-[13px] last:border-b-0"
            :data-test="`req-row-${row.key}`">
            <b class="flex-none font-semibold">{{ row.title }}</b>
            <span class="text-secondary-content min-w-0 flex-1 text-xs leading-snug">
                {{ row.description }}
            </span>
            <div class="flex flex-none items-center gap-2">
                <ToggleButton v-if="row.kind === 'toggle'" v-model="row.model.value">
                    <span class="text-primary-content text-xs font-medium">
                        {{ row.model.value == 'true' ? t('settings.status.enabled') : t('settings.status.disabled') }}
                    </span>
                </ToggleButton>
                <template v-else>
                    <InputComponent
                        v-model="row.model.value"
                        size="sm"
                        class="w-20"
                        :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                        :min="row.min"
                        :max="row.max" />
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
    /// 数值护栏(R17):失焦钳制边界,undefined 一侧不设限。依据见各字段处。
    min?: number
    max?: number
}

/// 行序 = 原卡片字段序:批量(仅 AI 主服务)→ 批大小(批量开)→ 超时 → 重试 → 退避。
/// 数值边界依据:
/// - batchSize 1–2000:批大小与字幕搜索批次共用 2000 上限先例(P2-2 服务端钳制);
/// - requestTimeout 1–120(分钟):0/负数服务端回退默认 5,故下界 1;单请求 2 小时
///   已是极宽容的天花板,更长意味着任务槽实际死亡;
/// - maxRetries 0–10:0 = 关闭重试(合法);默认 5,指数退避下 10 次已≈17 分钟纯等待;
/// - retryDelay 0–300(秒):0 = 立即重试(合法);初始延迟 5 分钟再乘倍率已属极端;
/// - retryDelayMultiplier 1–10:1 = 恒定退避;<1 会收缩延迟、负数会让
///   Task.Delay 抛 ArgumentOutOfRange(服务端无下界防护,这里补上)。
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
                model: maxBatchSize,
                min: 1,
                max: 2000
            })
        }
    }
    out.push(
        {
            key: 'requestTimeout',
            kind: 'input',
            title: t('settings.translation.requestTimeout'),
            description: t('settings.translation.requestTimeoutDescription'),
            model: requestTimeout,
            min: 1,
            max: 120
        },
        {
            key: 'maxRetries',
            kind: 'input',
            title: t('settings.translation.maxRetries'),
            description: t('settings.translation.maxRetriesDescription'),
            model: maxRetries,
            min: 0,
            max: 10
        },
        {
            key: 'retryDelay',
            kind: 'input',
            title: t('settings.translation.retryDelay'),
            description: t('settings.translation.retryDelayDescription'),
            model: retryDelay,
            min: 0,
            max: 300
        },
        {
            key: 'retryDelayMultiplier',
            kind: 'input',
            title: t('settings.translation.retryDelayMultiplier'),
            description: t('settings.translation.retryDelayMultiplierDescription'),
            model: retryDelayMultiplier,
            min: 1,
            max: 10
        }
    )
    return out
})
</script>
