<template>
    <!-- ═══ pb-head(原型 screen-09):← 任务队列 + 标题块 + 校对/日志/暂停 ═══ -->
    <DrillDownPage
        :back-to="{ name: 'queue' }"
        :back-label="t('nav.queue')"
        :title="detail?.title ?? t('nav.queue')"
        :sub="subLine">
        <template #actions>
            <ButtonComponent
                v-if="detail?.status === TRANSLATION_STATUS.COMPLETED"
                variant="secondary"
                size="xs"
                :disabled="converting"
                @click="handleConvert">
                {{
                    converting
                        ? t('translations.detail.converting')
                        : detail?.isBilingual
                          ? t('translations.detail.convertToMonolingual')
                          : t('translations.detail.convertToBilingual')
                }}
            </ButtonComponent>
            <router-link
                v-if="detail?.status === TRANSLATION_STATUS.COMPLETED"
                :to="{ name: 'proofread', params: { id } }">
                <ButtonComponent variant="secondary" size="xs">
                    {{ t('translations.detail.proofread') }}
                </ButtonComponent>
            </router-link>
            <router-link :to="{ name: 'logs' }">
                <ButtonComponent variant="secondary" size="xs" data-test="detail-logs">
                    {{ t('translations.detail.logs') }}
                </ButtonComponent>
            </router-link>
            <ButtonComponent
                v-if="canPause"
                variant="primary"
                size="xs"
                :loading="pauseBusy"
                data-test="detail-pause"
                @click="togglePause">
                {{
                    isPaused
                        ? t('translations.detail.resumeAction')
                        : t('translations.detail.pauseAction')
                }}
            </ButtonComponent>
        </template>

        <div v-if="loading" class="flex justify-center py-12">
            <Icon name="loaderCircle" class="text-primary-content/50 h-8 w-8 animate-spin" />
        </div>

        <div v-else-if="detail" class="flex min-h-0 flex-1 flex-col gap-4 overflow-hidden">
            <!-- ═══ 统计卡(固定):主进度 + qd-stats 真实数据格 + 执行链 ═══ -->
            <CardComponent class="shrink-0">
                <template #content>
                    <div v-if="showProgress" class="flex items-center gap-3">
                        <TranslationProgress :progress="displayProgress" />
                        <span class="text-primary-content shrink-0 text-sm font-medium">
                            {{ displayProgress }}%
                        </span>
                    </div>

                    <!-- qd-stats:每格仅展示可从 detail DTO / 事件流推导的真实数据;
                         成本格只在事件 message 携带金额时出现。 -->
                    <div
                        v-if="statCells.length > 0"
                        class="grid grid-cols-2 gap-x-6 gap-y-3 sm:grid-cols-3 lg:grid-cols-4"
                        data-test="qd-stats">
                        <div v-for="cell in statCells" :key="cell.label">
                            <div
                                class="mono text-base font-semibold"
                                :class="cell.accent ? 'text-accent' : 'text-primary-content'">
                                {{ cell.value }}
                                <small
                                    v-if="cell.unit"
                                    class="text-secondary-content ml-1 text-[11.5px] font-normal">
                                    {{ cell.unit }}
                                </small>
                            </div>
                            <div class="text-secondary-content text-[11.5px]">{{ cell.label }}</div>
                        </div>
                    </div>

                    <!-- ═══ 执行链(P1-2):service_type 链 chips,状态从事件推导 ═══ -->
                    <ExecutionChain :entries="chainEntries" />
                </template>
            </CardComponent>

            <div class="shell-scroll flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto pr-1">
                <!-- ═══ 现有信息卡收纳(状态/语言对/媒体/错误与堆栈) ═══ -->
                <CardComponent>
                    <template #content>
                        <div class="grid grid-cols-2 gap-x-6 gap-y-3 md:grid-cols-4">
                            <div>
                                <span class="font-semibold">
                                    {{ t('translations.detail.status') }}
                                </span>
                                <div class="mt-1">
                                    <TranslationStatus :translation-status="detail.status" />
                                </div>
                            </div>
                            <div>
                                <span class="font-semibold">
                                    {{ t('translations.detail.source') }}
                                </span>
                                <div class="mt-1">
                                    <BadgeComponent
                                        classes="font-mono text-secondary-content border-line bg-tertiary">
                                        {{ detail.sourceLanguage.toUpperCase() }}
                                    </BadgeComponent>
                                </div>
                            </div>
                            <div>
                                <span class="font-semibold">
                                    {{ t('translations.detail.target') }}
                                </span>
                                <div class="mt-1">
                                    <BadgeComponent
                                        classes="font-mono text-secondary-content border-line bg-tertiary">
                                        {{ detail.targetLanguage.toUpperCase() }}
                                    </BadgeComponent>
                                </div>
                            </div>
                            <div v-if="detail.status === TRANSLATION_STATUS.COMPLETED">
                                <span class="font-semibold">
                                    {{ t('translations.detail.outputForm') }}
                                </span>
                                <div class="mt-1">
                                    <BadgeComponent
                                        :classes="
                                            detail.isBilingual
                                                ? 'text-success-content border-success/40 bg-success/50'
                                                : 'text-primary-content/70 border-line bg-secondary'
                                        ">
                                        {{
                                            detail.isBilingual
                                                ? t('translations.bilingual')
                                                : t('translations.monolingual')
                                        }}
                                    </BadgeComponent>
                                </div>
                            </div>
                            <div>
                                <span class="font-semibold">
                                    {{ t('translations.detail.mediaType') }}
                                </span>
                                <div class="mt-1 text-sm">{{ detail.mediaType }}</div>
                            </div>
                            <div v-if="detail.createdAt">
                                <span class="font-semibold">
                                    {{ t('translations.detail.createdAt') }}
                                </span>
                                <div class="mt-1 text-sm">
                                    {{ formatDateTime(detail.createdAt) }}
                                </div>
                            </div>
                            <div v-if="detail.completedAt">
                                <span class="font-semibold">
                                    {{ t('translations.detail.completed') }}
                                </span>
                                <div class="mt-1 text-sm">
                                    {{ formatDateTime(detail.completedAt) }}
                                </div>
                            </div>
                            <div v-if="detail.subtitleToTranslate" class="col-span-2">
                                <span class="font-semibold">
                                    {{ t('translations.detail.subtitlePath') }}
                                </span>
                                <div class="mt-1 text-sm break-all">
                                    {{ detail.subtitleToTranslate }}
                                </div>
                            </div>
                        </div>
                        <div
                            v-if="detail.errorMessage"
                            class="border-danger/40 bg-danger/15 rounded-md border p-4">
                            <div class="text-danger text-sm">{{ detail.errorMessage }}</div>
                            <details v-if="detail.stackTrace" class="mt-3">
                                <summary
                                    class="text-danger/70 hover:text-danger cursor-pointer text-sm">
                                    {{ t('translations.detail.stackTrace') }}
                                </summary>
                                <pre
                                    class="text-danger/80 mt-2 rounded bg-black/30 p-3 text-xs break-all whitespace-pre-wrap"
                                    >{{ detail.stackTrace }}</pre>
                            </details>
                        </div>
                    </template>
                </CardComponent>

                <!-- ═══ 事件时间线(P1-2):落库事件按时间序 ═══ -->
                <EventTimeline :events="timelineEvents" @refresh="fetchEvents" />

                <CardComponent
                    v-if="reversedLines.length > 0"
                    :title="t('translations.detail.translatedLines')">
                    <template #content>
                        <div class="border-line hidden border-b font-bold md:grid md:grid-cols-12">
                            <div class="col-span-1 px-4 py-2 text-sm">#</div>
                            <div class="col-span-4 px-4 py-2 text-sm">
                                {{ t('translations.detail.source') }}
                            </div>
                            <div class="col-span-5 px-4 py-2 text-sm">
                                {{ t('translations.detail.target') }}
                            </div>
                            <div class="col-span-2 px-4 py-2 text-sm">
                                {{ t('translations.detail.service') }}
                            </div>
                        </div>
                        <div class="max-h-96 overflow-y-auto">
                            <div
                                v-for="line in reversedLines"
                                :key="line.position"
                                class="border-line grid grid-cols-1 gap-1 border-b transition-colors duration-500 last:border-b-0 md:grid-cols-12 md:gap-0"
                                :class="
                                    line.position === latestPosition
                                        ? 'bg-accent/10'
                                        : line.position === latestPosition - 1
                                          ? 'bg-accent/5'
                                          : ''
                                ">
                                <div
                                    class="text-secondary-content/60 px-4 py-2 font-mono text-xs tabular-nums md:col-span-1">
                                    {{ line.position }}
                                </div>
                                <div class="text-primary-content px-4 py-2 text-sm md:col-span-4">
                                    {{ line.source }}
                                </div>
                                <div class="text-primary-content px-4 py-2 text-sm md:col-span-5">
                                    {{ line.target }}
                                </div>
                                <LineProofreadPanel
                                    :line="line"
                                    :request-id="detail.id"
                                    :source-language="detail.sourceLanguage"
                                    :target-language="detail.targetLanguage"
                                    :editable="editable"
                                    :proofreadable="proofreadable"
                                    :close-signal="closeSignals[line.position] ?? 0"
                                    @applied="(target: string) => (line.target = target)" />
                            </div>
                        </div>
                    </template>
                </CardComponent>
            </div>
        </div>

        <div v-else class="text-primary-content/50 py-12 text-center">
            {{ t('translations.detail.notFound') }}
        </div>
    </DrillDownPage>
</template>

<script setup lang="ts">
import { reactive, ref, computed, onMounted, onUnmounted, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
    ITranslationRequestDetail,
    IRequestProgress,
    ILineTranslated,
    ILineProofread,
    ITranslationRequestEvent,
    SETTINGS,
    TRANSLATION_EVENT_TYPE,
    TRANSLATION_STATUS
} from '@/types'
import axios from 'axios'
import { useTranslationDetailHub } from '@/cells/queue/composables/useTranslationDetailHub'
import { formatDateTime } from '@/cells/app/utils/date'
import { elapsedMs, formatClock, remainingMs } from '@/cells/app/utils/eta'
import { translationRequestService } from '@/cells/app/api/translationRequestService'
import {
    deriveChainState,
    parseServiceChain,
    ChainEntry
} from '@/cells/queue/services/executionChain'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useUiStore } from '@/cells/app/stores/ui'
import { useToastStore } from '@/cells/app/stores/toast'
import useTranslationRequestStore from '@/cells/app/stores/translationRequest'
import DrillDownPage from '@/cells/app/components/DrillDownPage.vue'
import TranslationStatus from '@/cells/queue/components/TranslationStatus.vue'
import TranslationProgress from '@/cells/app/components/TranslationProgress.vue'
import BadgeComponent from '@/cells/kernel/components/BadgeComponent.vue'
import ButtonComponent from '@/cells/kernel/components/ButtonComponent.vue'
import CardComponent from '@/cells/kernel/components/CardComponent.vue'
import ExecutionChain from '@/cells/queue/components/ExecutionChain.vue'
import EventTimeline from '@/cells/queue/components/EventTimeline.vue'
import LineProofreadPanel from '@/cells/queue/components/translation/LineProofreadPanel.vue'
import Icon from '@/cells/kernel/components/icons/Icon.vue'

const translationRequestApi = translationRequestService(axios)

const props = defineProps<{
    id: string
}>()

const translationRequestStore = useTranslationRequestStore()
const settingStore = useSettingStore()
const uiStore = useUiStore()
const toastStore = useToastStore()
const { t } = useI18n()

const detail = ref<ITranslationRequestDetail | null>(null)
const loading = ref(true)
const progress = ref(0)
const latestPosition = ref<number>(0)
const timelineEvents = ref<ITranslationRequestEvent[]>([])
// Per-line counters; bumping one tells that line's LineProofreadPanel to close
// (replaces the old delete proofreadState[position] on hub events).
const closeSignals = reactive<Record<number, number>>({})

const reversedLines = computed(() => (detail.value ? [...detail.value.lines].reverse() : []))

const editable = computed(
    () =>
        detail.value?.status === TRANSLATION_STATUS.COMPLETED && !!detail.value?.translatedSubtitle
)

const proofreadable = computed(() => editable.value && translationRequestStore.proofreadSupported)

const converting = ref(false)
const handleConvert = async () => {
    if (!detail.value || converting.value) return
    converting.value = true
    try {
        await translationRequestApi.convert(detail.value.id, !detail.value.isBilingual)
        detail.value = await translationRequestApi.get<ITranslationRequestDetail>(Number(props.id))
    } catch (error) {
        console.error('Conversion failed:', error)
    } finally {
        converting.value = false
    }
}

const showProgress = computed(() => {
    if (!detail.value) {
        return false
    }
    const status = detail.value.status
    return (
        (status === TRANSLATION_STATUS.INPROGRESS && progress.value > 0) ||
        status === TRANSLATION_STATUS.COMPLETED
    )
})

const displayProgress = computed(() =>
    detail.value?.status === TRANSLATION_STATUS.COMPLETED ? 100 : progress.value
)

// ── 执行链 + 时间线(P1-2)────────────────────────────────────────
// 链来自 service_type 设置;chip 状态从已落库事件推导。当前服务优先级
// (deriveChainState):最近一条 Degraded 事件的 provider(降级后接管者,
// 事件优先于 request.serviceType,兼容旧数据把主服务落库的行)>
// 持久化的 serviceType > 最近一条带 service 的翻译行(SignalR 逐行推送)。
const serviceChain = computed(() =>
    parseServiceChain(settingStore.getSetting(SETTINGS.SERVICE_TYPE) as string)
)

const currentService = computed(() => {
    if (!detail.value) return null
    if (detail.value.serviceType) return detail.value.serviceType
    const lastServicedLine = [...detail.value.lines].reverse().find((line) => line.service)
    return lastServicedLine?.service ?? null
})

const chainEntries = computed<ChainEntry[]>(() =>
    deriveChainState(serviceChain.value, timelineEvents.value, currentService.value)
)

async function fetchEvents(): Promise<void> {
    const id = Number(props.id)
    if (!Number.isFinite(id) || id <= 0) return
    try {
        timelineEvents.value = await translationRequestApi.getEvents(id)
    } catch {
        // the card keeps its last known timeline; the reload button retries
    }
}

const handleProgress = (requestProgress: IRequestProgress) => {
    if (detail.value && requestProgress.id === detail.value.id) {
        const statusChanged = requestProgress.status !== detail.value.status
        detail.value.status = requestProgress.status
        progress.value = requestProgress.progress
        if (requestProgress.completedAt) {
            detail.value.completedAt = requestProgress.completedAt
        }
        if (requestProgress.errorMessage) {
            detail.value.errorMessage = requestProgress.errorMessage
        }
        if (requestProgress.stackTrace) {
            detail.value.stackTrace = requestProgress.stackTrace
        }
        // v1 刷新模型(未做事件级 SignalR 推送):状态翻转时拉取时间线,
        // 让降级/失败/完成事件落地;批内进度靠手动刷新按钮。
        if (statusChanged) {
            void fetchEvents()
        }
    }
}

const handleLineTranslated = (line: ILineTranslated) => {
    if (detail.value && line.id === detail.value.id) {
        detail.value.lines.push({
            position: line.position,
            source: line.source,
            target: line.target,
            service: line.service ?? null
        })
        latestPosition.value = line.position
    }
}

const handleLineProofread = (line: ILineProofread) => {
    if (detail.value && line.id === detail.value.id) {
        const target = detail.value.lines.find((entry) => entry.position === line.position)
        if (target) {
            target.target = line.target
            if (line.service) {
                target.service = line.service
            }
        }
        closeSignals[line.position] = (closeSignals[line.position] ?? 0) + 1
    }
}

// ── 暂停/恢复接线(P0,原型 screen-09 交互 1)──────────────────────
// pb-head 主按钮与顶栏 CTA 共用同一状态机:toggle 后两处文案同步切换,
// toast 语义 = 批循环检查点挂起/已恢复(与队列行 ⏸ 一致)。
const pauseBusy = ref(false)

const isPaused = computed(() => detail.value?.status === TRANSLATION_STATUS.PAUSED)

const canPause = computed(() => {
    const status = detail.value?.status
    return (
        status === TRANSLATION_STATUS.INPROGRESS ||
        status === TRANSLATION_STATUS.PENDING ||
        status === TRANSLATION_STATUS.PAUSED
    )
})

const togglePause = async () => {
    if (!detail.value || pauseBusy.value || !canPause.value) return
    pauseBusy.value = true
    const requestId = detail.value.id
    try {
        if (isPaused.value) {
            await translationRequestStore.resumePaused(detail.value)
            detail.value.status = TRANSLATION_STATUS.INPROGRESS
            toastStore.push({
                message: t('translations.detail.resumeToast', { id: requestId }),
                type: 'success'
            })
        } else {
            await translationRequestStore.pause(detail.value)
            detail.value.status = TRANSLATION_STATUS.PAUSED
            toastStore.push({
                message: t('translations.detail.pauseToast', { id: requestId }),
                type: 'info'
            })
        }
    } catch (error) {
        console.error('Pause/resume failed:', error)
    } finally {
        pauseBusy.value = false
    }
}

// ── mono 副行 + 统计格(真实数据)─────────────────────────────────
// 副行仅取 detail DTO 实际携带的字段:请求 #/语言对/已译行数/已译字符数
// (来源字段存在则附加)。字符数 = 已落库行 source 之和。
const subLine = computed(() => {
    if (!detail.value) return ''
    const parts = [
        `#${detail.value.id}`,
        `${detail.value.sourceLanguage.toUpperCase()} → ${detail.value.targetLanguage.toUpperCase()}`
    ]
    if (detail.value.lines.length > 0) {
        parts.push(t('translations.detail.subLines', { count: detail.value.lines.length }))
        const chars = detail.value.lines.reduce((sum, line) => sum + line.source.length, 0)
        parts.push(t('translations.detail.subChars', { count: chars.toLocaleString() }))
    }
    if (detail.value.source) {
        parts.push(detail.value.source)
    }
    return parts.join(' · ')
})

/// 任务开始时间:detail DTO 优先,缺省回落到时间线的 Started 事件。
const startedAt = computed<string | null>(() => {
    if (detail.value?.startedAt) return detail.value.startedAt
    const started = timelineEvents.value.find(
        (event) => event.eventType === TRANSLATION_EVENT_TYPE.STARTED
    )
    return started?.createdAt ?? null
})

/// 批次进度:从 BatchCompleted 事件 message(`batch 2/4 · …`)解析。
const batchInfo = computed<{ index: number; total: number } | null>(() => {
    let index = 0
    let total = 0
    for (const event of timelineEvents.value) {
        if (event.eventType !== TRANSLATION_EVENT_TYPE.BATCH_COMPLETED || !event.message) {
            continue
        }
        const match = event.message.match(/batch (\d+)\/(\d+)/)
        if (match) {
            index = Math.max(index, Number(match[1]))
            total = Math.max(total, Number(match[2]))
        }
    }
    return total > 0 ? { index, total } : null
})

/// 成本:仅当事件 message 携带金额(当前服务端不写,格因此不出现)。
const eventCost = computed<string | null>(() => {
    for (const event of [...timelineEvents.value].reverse()) {
        if (!event.message) continue
        const match = event.message.match(/\$([0-9]+(?:\.[0-9]+)?)/)
        if (match) return `$${match[1]}`
    }
    return null
})

/// 总行数:completed 时 = 已译行数;进行中按当前进度线性外推(~)。
const totalLinesEstimate = computed<number | null>(() => {
    const lines = detail.value?.lines.length ?? 0
    if (lines === 0) return null
    const percent = displayProgress.value
    if (percent >= 100) return lines
    if (percent > 0) return Math.round(lines / (percent / 100))
    return null
})

/// 已运行/预计剩余每秒刷新(剩余复用队列页同款线性外推,共享 app util)。
const nowTick = ref(Date.now())
let tickTimer: ReturnType<typeof setInterval> | null = null

const statCells = computed<{ label: string; value: string; unit?: string; accent?: boolean }[]>(
    () => {
        if (!detail.value) return []
        const cells: { label: string; value: string; unit?: string; accent?: boolean }[] = []

        if (detail.value.lines.length > 0) {
            cells.push({
                label: t('translations.detail.statTranslated'),
                value: String(detail.value.lines.length),
                unit: totalLinesEstimate.value ? `/ ~${totalLinesEstimate.value}` : undefined
            })
        }
        if (batchInfo.value) {
            const current =
                detail.value.status === TRANSLATION_STATUS.INPROGRESS
                    ? Math.min(batchInfo.value.index + 1, batchInfo.value.total)
                    : batchInfo.value.index
            cells.push({
                label: t('translations.detail.statCurrentBatch'),
                value: `${current}/${batchInfo.value.total}`
            })
        }
        if (eventCost.value) {
            cells.push({ label: t('translations.detail.statCost'), value: eventCost.value })
        }
        const elapsed = elapsedMs(startedAt.value, detail.value.completedAt, nowTick.value)
        if (elapsed !== null) {
            cells.push({
                label: t('translations.detail.statElapsed'),
                value: formatClock(elapsed)
            })
        }
        const remaining =
            detail.value.status === TRANSLATION_STATUS.INPROGRESS
                ? remainingMs(startedAt.value, displayProgress.value, nowTick.value)
                : null
        if (remaining !== null) {
            cells.push({
                label: t('translations.detail.statRemaining'),
                value: formatClock(remaining),
                accent: true
            })
        }
        return cells
    }
)

// ── 顶栏:CTA 与副行随状态机同步(与 pb-head 同一 togglePause)────
watch(
    () => [detail.value?.status, isPaused.value] as const,
    () => {
        if (canPause.value && detail.value) {
            uiStore.registerCta(
                isPaused.value
                    ? t('translations.detail.resumeAction')
                    : t('translations.detail.pauseAction'),
                () => void togglePause()
            )
        } else {
            uiStore.clearCta()
        }

        const remaining =
            detail.value?.status === TRANSLATION_STATUS.INPROGRESS
                ? remainingMs(startedAt.value, displayProgress.value, nowTick.value)
                : null
        uiStore.setSubOverride(
            detail.value && remaining !== null
                ? t('translations.detail.topbarSub', {
                      id: detail.value.id,
                      progress: displayProgress.value,
                      eta: formatClock(remaining)
                  })
                : ''
        )
    },
    { immediate: true }
)

const { connect } = useTranslationDetailHub()

onMounted(async () => {
    tickTimer = setInterval(() => {
        nowTick.value = Date.now()
    }, 1000)

    try {
        detail.value = await translationRequestApi.get<ITranslationRequestDetail>(Number(props.id))
        progress.value = detail.value.progress ?? 0
        if (!detail.value.lines) {
            detail.value.lines = []
        }
        if (detail.value.events) {
            timelineEvents.value = detail.value.events
        }
    } catch {
        detail.value = null
    } finally {
        loading.value = false
    }

    // the dedicated events endpoint is the source of truth — the detail
    // payload's embedded copy may predate late fallback events
    await fetchEvents()

    await translationRequestStore.fetchProofreadStatus()

    // the detail hub joins exactly this request's own group (per-request
    // traffic never touches the global group any more)
    await connect(Number(props.id), {
        onRequestProgress: handleProgress,
        onLineTranslated: handleLineTranslated,
        onLineProofread: handleLineProofread
    })
})

onUnmounted(() => {
    if (tickTimer) {
        clearInterval(tickTimer)
        tickTimer = null
    }
    uiStore.clearCta()
    uiStore.setSubOverride('')
})
</script>
