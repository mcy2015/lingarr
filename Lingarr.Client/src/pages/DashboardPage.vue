<template>
    <!-- Viewport-fit layout: pipeline/KPI rows keep fixed height, the two
         content rows split the remaining space and scroll inside their cards. -->
    <div class="w-full flex h-full min-h-0 flex-col gap-5 overflow-hidden p-4 lg:p-6">
        <!-- ═══ 管线条带：媒体库 → 缺字幕 → 队列(hot) → 服务 → 写回 ═══ -->
        <div class="flex flex-wrap items-stretch gap-2 shrink-0">
            <template v-for="(node, index) in pipelineNodes" :key="node.key">
                <div v-if="index > 0" class="link hidden self-center md:block"></div>
                <div
                    class="min-w-[150px] flex-1 rounded-md border bg-secondary px-3.5 py-3"
                    :class="node.hot ? 'border-accent/55' : 'border-line'">
                    <div class="text-secondary-content text-[11.5px]">{{ node.label }}</div>
                    <div class="mono text-[22px] font-semibold leading-tight" :class="node.hot ? 'text-accent' : ''">
                        {{ node.value
                        }}<small class="text-secondary-content ml-1 text-xs font-normal">{{ node.small }}</small>
                    </div>
                </div>
            </template>
        </div>

        <!-- ═══ KPI 条 ═══ -->
        <div class="grid grid-cols-2 gap-2.5 shrink-0 lg:grid-cols-4">
            <div v-for="kpi in kpis" :key="kpi.label" class="rounded-md border border-line bg-secondary px-4 py-3">
                <div class="mono text-2xl font-semibold" :class="kpi.cls ?? ''">{{ kpi.value }}</div>
                <div class="text-secondary-content mt-0.5 text-xs">{{ kpi.label }}</div>
            </div>
        </div>

        <!-- ═══ 活跃任务 + 服务健康（弹性行，卡内滚动） ═══ -->
        <div class="grid min-h-[170px] flex-[1.4] gap-3.5 lg:grid-cols-[1.6fr_1fr]">
            <div class="rounded-md border border-line bg-secondary flex min-h-0 flex-col">
                <div class="flex shrink-0 items-center gap-2.5 border-b border-line px-4 py-3">
                    <h2 class="text-[13px] font-semibold">{{ t('overview.active.title') }}</h2>
                    <span class="mono text-secondary-content ml-auto text-xs">
                        {{ t('overview.active.live') }}
                    </span>
                </div>
                <div class="shell-scroll flex min-h-0 flex-1 flex-col overflow-y-auto">
                    <p
                        v-if="activeItems.length === 0"
                        class="text-secondary-content/60 my-auto px-4 py-6 text-center text-sm">
                        {{ t('overview.active.empty') }}
                    </p>
                    <div
                        v-for="item in activeItems"
                        :key="item.id"
                        class="grid grid-cols-[minmax(0,1fr)_130px_74px] items-center gap-3.5 border-b border-line px-4 py-2.5 last:border-b-0">
                        <div class="min-w-0">
                            <b class="block truncate text-[13px] font-semibold" :title="item.title">{{ item.title }}</b>
                            <span class="text-secondary-content block truncate text-[11.5px]">
                                {{ item.sourceLanguage.toUpperCase() }} → {{ item.targetLanguage.toUpperCase() }}
                                <template v-if="item.serviceType"> · {{ item.serviceType }}</template>
                            </span>
                        </div>
                        <TranslationProgress :progress="item.progress" :status="item.status" />
                        <div class="mono text-secondary-content text-right text-xs" :title="t('overview.active.etaHint')">
                            {{ item.progress }}% · {{ remainingEta(item) }}
                        </div>
                    </div>
                </div>
            </div>

            <div class="rounded-md border border-line bg-secondary flex min-h-0 flex-col">
                <div class="flex shrink-0 items-center gap-2.5 border-b border-line px-4 py-3">
                    <h2 class="text-[13px] font-semibold">{{ t('overview.health.title') }}</h2>
                    <span class="mono text-secondary-content ml-auto text-xs">
                        {{ t('overview.health.configuredCount', { count: configuredCount }) }}
                    </span>
                </div>
                <div class="shell-scroll min-h-0 flex-1 overflow-y-auto">
                    <div
                        v-for="provider in health"
                        :key="provider.provider"
                        class="flex items-center gap-2.5 border-b border-line px-4 py-2.5 text-[13px] last:border-b-0">
                        <span class="h-[7px] w-[7px] shrink-0 rounded-full" :class="healthDot(provider).cls"></span>
                        <span>{{ provider.displayName }}</span>
                        <span
                            class="mono text-secondary-content ml-auto max-w-[45%] truncate text-[11.5px]"
                            :title="healthDot(provider).title">
                            {{ healthDot(provider).text }}
                        </span>
                    </div>
                </div>
            </div>
        </div>

        <!-- ═══ 最近动态（弹性行，卡内滚动） ═══ -->
        <div class="rounded-md border border-line bg-secondary flex min-h-[150px] flex-1 flex-col">
            <div class="flex shrink-0 items-center gap-2.5 border-b border-line px-4 py-3">
                <h2 class="text-[13px] font-semibold">{{ t('overview.feed.title') }}</h2>
                <span class="mono text-secondary-content ml-auto text-xs">
                    {{ t('overview.feed.count', { count: events.length }) }}
                </span>
            </div>
            <div class="shell-scroll min-h-0 flex-1 overflow-y-auto">
                <p
                    v-if="events.length === 0"
                    class="text-secondary-content/60 my-auto px-4 py-6 text-center text-sm">
                    {{ t('overview.feed.empty') }}
                </p>
                <div
                    v-for="event in events"
                    :key="event.id"
                    class="flex gap-3 border-b border-line px-4 py-2 text-[12.5px] last:border-b-0">
                    <time class="mono text-secondary-content shrink-0 pt-0.5 text-[11px]">
                        {{ formatTime(event.createdAt) }}
                    </time>
                    <span class="shrink-0" :class="feedIcon(event.status).cls">{{ feedIcon(event.status).icon }}</span>
                    <span class="min-w-0">
                        <b>{{ event.requestTitle }}</b>
                        <span class="text-secondary-content"> — {{ event.message ?? statusLabel(event.status) }}</span>
                    </span>
                </div>
            </div>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import TranslationProgress from '@/components/common/TranslationProgress.vue'
import services from '@/services'
import { useSignalR } from '@/composables/useSignalR'
import {
    IPagedResult,
    IPipelineStatistics,
    IRequestProgress,
    ITranslationRequest,
    ITranslationRequestEventSummary,
    ITranslationServiceHealth,
    TRANSLATION_STATUS
} from '@/types'

const REFRESH_INTERVAL_MS = 30_000

const { t, locale } = useI18n()
const signalR = useSignalR()

const pipeline = ref<IPipelineStatistics | null>(null)
const counts = ref<Record<string, number>>({})
const activeItems = ref<ITranslationRequest[]>([])
const health = ref<ITranslationServiceHealth[]>([])
const events = ref<ITranslationRequestEventSummary[]>([])

const configuredCount = computed(() => health.value.filter((provider) => provider.configured).length)
const inProgressCount = computed(() => counts.value[TRANSLATION_STATUS.INPROGRESS] ?? 0)
const pendingCount = computed(() => counts.value[TRANSLATION_STATUS.PENDING] ?? 0)

const pipelineNodes = computed(() => [
    {
        key: 'library',
        label: t('overview.pipe.library'),
        value: pipeline.value?.mediaCount ?? '—',
        small: t('overview.pipe.mediaUnit')
    },
    {
        key: 'missing',
        label: t('overview.pipe.missing'),
        value: pipeline.value?.missingSubtitles ?? '—',
        small: t('overview.pipe.trackUnit')
    },
    {
        key: 'queue',
        label: t('overview.pipe.queue'),
        value: pipeline.value?.queueDepth ?? '—',
        small: t('overview.pipe.queueUnit', {
            running: inProgressCount.value,
            waiting: pendingCount.value
        }),
        hot: true
    },
    {
        key: 'services',
        label: t('overview.pipe.services'),
        value: pipeline.value?.servicesOnline ?? '—',
        small: t('overview.pipe.onlineUnit')
    },
    {
        key: 'written',
        label: t('overview.pipe.written'),
        value: pipeline.value?.writtenThisMonth ?? '—',
        small: t('overview.pipe.writtenUnit')
    }
])

const kpis = computed(() => [
    { label: t('overview.kpi.inProgress'), value: inProgressCount.value },
    { label: t('overview.kpi.queued'), value: pendingCount.value },
    {
        label: t('overview.kpi.cost'),
        value:
            pipeline.value?.monthlyCost != null
                ? new Intl.NumberFormat(locale.value, { style: 'currency', currency: 'USD' }).format(
                      pipeline.value.monthlyCost
                  )
                : '—',
        cls: 'text-secondary-content/60'
    },
    {
        label: t('overview.kpi.successRate'),
        value: pipeline.value?.successRate30Days != null ? `${pipeline.value.successRate30Days}%` : '—',
        cls:
            pipeline.value?.successRate30Days != null && pipeline.value.successRate30Days < 90
                ? 'text-warning'
                : ''
    }
])

const healthDot = (provider: ITranslationServiceHealth): { cls: string; text: string; title: string } => {
    if (!provider.configured) {
        return { cls: 'bg-line', text: t('overview.health.offline'), title: t('overview.health.offlineHint') }
    }
    const rate = provider.successRate
    if (rate == null) {
        return {
            cls: 'bg-success',
            text: t('overview.health.noTraffic'),
            title: t('overview.health.noTrafficHint')
        }
    }
    if (rate >= 0.8) {
        const latency = provider.latencyMs != null ? `${Math.round(provider.latencyMs)} ms` : '—'
        return {
            cls: 'bg-success',
            text:
                provider.monthlyCost != null
                    ? `${latency} · ${new Intl.NumberFormat(locale.value, {
                          style: 'currency',
                          currency: 'USD',
                          maximumFractionDigits: 2
                      }).format(provider.monthlyCost)}`
                    : latency,
            title: t('overview.health.healthy', { rate: Math.round(rate * 100) })
        }
    }
    if (rate >= 0.5) {
        return {
            cls: 'bg-warning',
            text: provider.lastError ?? `${Math.round(rate * 100)}%`,
            title: t('overview.health.degraded', { rate: Math.round(rate * 100) })
        }
    }
    return {
        cls: 'bg-danger',
        text: provider.lastError ?? `${Math.round(rate * 100)}%`,
        title: t('overview.health.failing', { rate: Math.round(rate * 100) })
    }
}

const feedIcon = (status: string): { icon: string; cls: string } => {
    switch (status) {
        case TRANSLATION_STATUS.COMPLETED:
            return { icon: '✓', cls: 'text-success' }
        case TRANSLATION_STATUS.FAILED:
            return { icon: '✗', cls: 'text-danger' }
        case TRANSLATION_STATUS.INPROGRESS:
            return { icon: '▸', cls: 'text-accent' }
        case TRANSLATION_STATUS.PAUSED:
            return { icon: '⏸', cls: 'text-warning' }
        default:
            return { icon: '·', cls: 'text-secondary-content' }
    }
}

const statusLabel = (status: string): string => {
    const camel = status.charAt(0).toLowerCase() + status.slice(1)
    return t(`common.translationStatus.${camel}`)
}

const formatTime = (createdAt: string): string =>
    new Date(createdAt).toLocaleTimeString(locale.value, { hour: '2-digit', minute: '2-digit' })

/// Same linear estimate as the queue rows: startedAt + progress extrapolated
/// to 100%; "—" until the job actually starts producing progress.
const remainingEta = (item: ITranslationRequest): string => {
    if (item.startedAt && item.progress > 0) {
        const elapsed = Date.now() - new Date(item.startedAt).getTime()
        const remaining = (elapsed / item.progress) * (100 - item.progress)
        const minutes = Math.floor(remaining / 60_000)
        const seconds = Math.floor((remaining % 60_000) / 1000)
        return `${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`
    }
    return '—'
}

const fetchOverview = async (): Promise<void> => {
    const [pipelineResult, countsResult, activeResult, healthResult, eventsResult] = await Promise.allSettled([
        services.statistics.getPipelineStatistics(),
        services.translationRequest.statusCounts(),
        services.translationRequest.requests<IPagedResult<ITranslationRequest>>(
            1,
            '',
            'CreatedAt',
            false,
            'InProgress'
        ),
        services.translate.getHealth(),
        services.translationRequest.getRecentEvents(20)
    ])
    if (pipelineResult.status === 'fulfilled') pipeline.value = pipelineResult.value
    if (countsResult.status === 'fulfilled') counts.value = countsResult.value
    if (activeResult.status === 'fulfilled') activeItems.value = activeResult.value.items
    if (healthResult.status === 'fulfilled') health.value = healthResult.value
    if (eventsResult.status === 'fulfilled') events.value = eventsResult.value
}

let refreshTimer: ReturnType<typeof setInterval> | undefined
let hubConnection: Awaited<ReturnType<typeof signalR.connect>> | undefined

onMounted(async () => {
    await fetchOverview()
    refreshTimer = setInterval(fetchOverview, REFRESH_INTERVAL_MS)

    const connection = await signalR.connect('TranslationRequests', '/signalr/TranslationRequests')
    await connection.joinGroup({ group: 'TranslationRequests' })
    hubConnection = connection
    connection.on('RequestProgress', onProgress)
})

onUnmounted(() => {
    if (refreshTimer) clearInterval(refreshTimer)
    hubConnection?.off('RequestProgress', onProgress)
})

/// Live progress for the active rows; terminal transitions also refresh the
/// feed/counts so completions land without waiting for the polling interval.
const onProgress = (progress: IRequestProgress): void => {
    const row = activeItems.value.find((item) => item.id === progress.id)
    if (row) {
        row.progress = progress.progress
        row.status = progress.status
    }
    if (
        ([
            TRANSLATION_STATUS.COMPLETED,
            TRANSLATION_STATUS.FAILED,
            TRANSLATION_STATUS.CANCELLED,
            TRANSLATION_STATUS.INTERRUPTED
        ] as string[]).includes(progress.status)
    ) {
        void fetchOverview()
    }
}
</script>

<style scoped>
/* pipeline arrow between nodes (prototype .pipe-link) */
.link {
    width: 26px;
    height: 1px;
    background: var(--line);
    position: relative;
    flex: none;
}
.link::after {
    content: '';
    position: absolute;
    right: 0;
    top: -3px;
    border: 4px solid transparent;
    border-left: 5px solid var(--line);
    border-right: 0;
}
</style>
