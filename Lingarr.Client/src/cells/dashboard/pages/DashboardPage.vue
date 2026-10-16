<template>
    <!-- Viewport-fit layout: pipeline/KPI rows keep fixed height, the two
         content rows split the remaining space and scroll inside their cards. -->
    <div class="flex h-full min-h-0 w-full flex-col gap-5 overflow-hidden p-4 lg:p-6">
        <!-- ═══ 管线条带：媒体库 → 缺字幕 → 队列(hot) → 服务 → 写回 ═══ -->
        <div class="flex shrink-0 flex-wrap items-stretch gap-2">
            <template v-for="(node, index) in pipelineNodes" :key="node.key">
                <div v-if="index > 0" class="link hidden self-center md:block"></div>
                <button
                    type="button"
                    class="border-line hover:border-accent/60 bg-secondary min-w-[150px] flex-1 cursor-pointer rounded-md border px-3.5 py-3 text-left transition-colors"
                    :class="node.hot ? 'border-accent/55' : 'border-line'"
                    :title="node.label"
                    @click="router.push({ name: node.route, query: node.query })">
                    <div class="text-secondary-content text-[11.5px]">{{ node.label }}</div>
                    <div
                        class="mono text-[22px] leading-tight font-semibold"
                        :class="node.hot ? 'text-accent' : ''">
                        {{ node.value }}
                        <small class="text-secondary-content ml-1 text-xs font-normal">
                            {{ node.small }}
                        </small>
                    </div>
                </button>
            </template>
        </div>

        <!-- ═══ KPI 条(第 5 格:字幕补缺——积压主数 + 置顶/提升/下轮副行,无配额)═══ -->
        <div class="grid shrink-0 grid-cols-2 gap-2.5 lg:grid-cols-5">
            <button
                v-for="(kpi, index) in kpis"
                :key="kpi.label"
                type="button"
                class="border-line hover:border-accent/60 bg-secondary cursor-pointer rounded-md border px-4 py-3 text-left transition-colors"
                :title="kpi.label"
                :data-test="`kpi-cell-${index}`"
                @click="router.push({ name: kpi.route, query: kpi.query })">
                <div class="mono text-2xl font-semibold" :class="kpi.cls ?? ''">
                    {{ kpi.value }}
                </div>
                <div class="text-secondary-content mt-0.5 text-xs">{{ kpi.label }}</div>
                <div v-if="kpi.sub" class="text-secondary-content/70 mt-0.5 text-[10.5px]">
                    {{ kpi.sub }}
                </div>
            </button>
        </div>

        <!-- ═══ 活跃任务 + 服务健康（弹性行，卡内滚动） ═══ -->
        <div class="grid min-h-[170px] flex-[1.4] gap-3.5 lg:grid-cols-[1.6fr_1fr]">
            <div class="border-line bg-secondary flex min-h-0 flex-col rounded-md border">
                <div class="border-line flex shrink-0 items-center gap-2.5 border-b px-4 py-3">
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
                    <JobRow v-for="item in activeItems" :key="item.id" :request="item" />
                </div>
            </div>

            <div class="border-line bg-secondary flex min-h-0 flex-col rounded-md border">
                <div class="border-line flex shrink-0 items-center gap-2.5 border-b px-4 py-3">
                    <h2 class="text-[13px] font-semibold">{{ t('overview.health.title') }}</h2>
                    <span class="mono text-secondary-content ml-auto text-xs">
                        {{ t('overview.health.configuredCount', { count: configuredCount }) }}
                    </span>
                </div>
                <!-- 状态完备(screen-01):每个服务都离线/故障时卡内给出提示,
                     而不是一行行重复灰点。 -->
                <p
                    v-if="allServicesDown"
                    class="border-line bg-danger/10 text-danger border-b px-4 py-2 text-[12.5px]"
                    data-test="health-all-offline">
                    {{ t('overview.health.allOffline') }}
                </p>
                <div class="shell-scroll min-h-0 flex-1 overflow-y-auto">
                    <router-link
                        v-for="provider in health"
                        :key="provider.provider"
                        :to="{ name: 'settings-providers' }"
                        class="hover:bg-tertiary/50 border-line flex items-center gap-2.5 border-b px-4 py-2.5 text-[13px] last:border-b-0">
                        <span
                            class="h-[7px] w-[7px] shrink-0 rounded-full"
                            :class="healthDot(provider).cls"></span>
                        <span>{{ provider.displayName }}</span>
                        <span
                            class="mono text-secondary-content ml-auto max-w-[45%] truncate text-[11.5px]"
                            :title="healthDot(provider).title">
                            {{ healthDot(provider).text }}
                        </span>
                    </router-link>
                </div>
            </div>
        </div>

        <!-- ═══ 最近动态（弹性行，卡内滚动） ═══ -->
        <div class="border-line bg-secondary flex min-h-[150px] flex-1 flex-col rounded-md border">
            <div class="border-line flex shrink-0 items-center gap-2.5 border-b px-4 py-3">
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
                    class="border-line flex gap-3 border-b px-4 py-2 text-[12.5px] last:border-b-0">
                    <time class="mono text-secondary-content shrink-0 pt-0.5 text-[11px]">
                        {{ formatTime(event.createdAt) }}
                    </time>
                    <span class="shrink-0" :class="feedIcon(event).cls">
                        {{ feedIcon(event).icon }}
                    </span>
                    <span class="min-w-0">
                        <b>{{ event.requestTitle }}</b>
                        <span class="text-secondary-content">— {{ feedText(event) }}</span>
                    </span>
                </div>
            </div>
        </div>

        <!-- ═══ 产量与成本 · 近 14 天（原型 .trend，P1-1） ═══ -->
        <TrendCard class="shrink-0" :trend="trend" :month="trendMonth" />
    </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import axios from 'axios'
import JobRow from '@/cells/app/components/JobRow.vue'
import TrendCard from '@/cells/dashboard/components/TrendCard.vue'
import { statisticsService } from '@/cells/app/api/statisticsService'
import { subtitleSearchService } from '@/cells/app/api/subtitleSearchService'
import { translateService } from '@/cells/app/api/translateService'
import { translationRequestService } from '@/cells/app/api/translationRequestService'
import { formatDegradationEdge } from '@/cells/app/utils/degradationEdge'
import { useUiStore } from '@/cells/app/stores/ui'
import { useSignalR } from '@/cells/app/composables/useSignalR'
import { useRequestGroups } from '@/cells/app/composables/useRequestGroups'
import {
    Hub,
    IDailyTrendStatistics,
    IPagedResult,
    IPipelineStatistics,
    IRequestProgress,
    ISubtitleBacklog,
    ITranslationRequest,
    ITranslationRequestEventSummary,
    ITranslationServiceHealth,
    TRANSLATION_EVENT_TYPE,
    TRANSLATION_STATUS
} from '@/types'

const REFRESH_INTERVAL_MS = 30_000

const statisticsApi = statisticsService(axios)
const translationRequestApi = translationRequestService(axios)
const translateApi = translateService(axios)

const { t, locale } = useI18n()
const signalR = useSignalR()
const router = useRouter()
const uiStore = useUiStore()

const pipeline = ref<IPipelineStatistics | null>(null)
const counts = ref<Record<string, number>>({})
const activeItems = ref<ITranslationRequest[]>([])
const health = ref<ITranslationServiceHealth[]>([])
const events = ref<ITranslationRequestEventSummary[]>([])
const trend = ref<IDailyTrendStatistics | null>(null)
/// Subtitle-search backlog (5th KPI, observation batch): four real counts
/// from the queue aggregate — backlog / pinned / webhook boosts / next
/// round. Null until the first fetch (value renders '—' like the cost KPI).
/// No quota figures by red line.
const subtitleBacklog = ref<ISubtitleBacklog | null>(null)

const configuredCount = computed(
    () => health.value.filter((provider) => provider.configured).length
)
const inProgressCount = computed(() => counts.value[TRANSLATION_STATUS.INPROGRESS] ?? 0)
const pendingCount = computed(() => counts.value[TRANSLATION_STATUS.PENDING] ?? 0)

/// State completeness (screen-01): every provider is offline (unconfigured)
/// or failing — the card shows a hint instead of a wall of dead dots.
const allServicesDown = computed(() => {
    if (health.value.length === 0) {
        return false
    }
    return health.value.every(
        (provider) =>
            !provider.configured || (provider.successRate != null && provider.successRate < 0.5)
    )
})

/// Month-to-date meta for the trend card (QA P3-3), assembled from the
/// pipeline statistics the page already fetches; null until they load.
const trendMonth = computed(() =>
    pipeline.value
        ? { written: pipeline.value.writtenThisMonth, cost: pipeline.value.monthlyCost }
        : null
)

/// Deep-link targets (screen-01 interaction 1): nodes and KPIs jump with a
/// route query so the destination screen can pre-activate the matching
/// filter — the missing-subtitle node lands on the library's missing view,
/// the queue KPIs on the corresponding status tab.
const pipelineNodes = computed(() => [
    {
        key: 'library',
        route: 'library',
        query: undefined as Record<string, string> | undefined,
        label: t('overview.pipe.library'),
        value: pipeline.value?.mediaCount ?? '—',
        small: t('overview.pipe.mediaUnit')
    },
    {
        key: 'missing',
        route: 'library',
        query: { missing: '1' },
        label: t('overview.pipe.missing'),
        value: pipeline.value?.missingSubtitles ?? '—',
        small: t('overview.pipe.trackUnit')
    },
    {
        key: 'queue',
        route: 'queue',
        query: undefined,
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
        route: 'settings-providers',
        query: undefined,
        label: t('overview.pipe.services'),
        value: pipeline.value?.servicesOnline ?? '—',
        small: t('overview.pipe.onlineUnit')
    },
    {
        key: 'written',
        route: 'queue',
        query: { status: 'Completed' },
        label: t('overview.pipe.written'),
        value: pipeline.value?.writtenThisMonth ?? '—',
        small: t('overview.pipe.writtenUnit')
    }
])

const kpis = computed(() => [
    {
        label: t('overview.kpi.inProgress'),
        value: inProgressCount.value,
        route: 'queue',
        query: { status: 'InProgress' } as Record<string, string> | undefined,
        sub: '' as string
    },
    {
        label: t('overview.kpi.queued'),
        value: pendingCount.value,
        route: 'queue',
        query: { status: 'Pending' },
        sub: ''
    },
    {
        label: t('overview.kpi.cost'),
        value:
            pipeline.value?.monthlyCost != null
                ? new Intl.NumberFormat(locale.value, {
                      style: 'currency',
                      currency: 'USD'
                  }).format(pipeline.value.monthlyCost)
                : '—',
        cls: 'text-secondary-content/60',
        route: 'settings-providers',
        query: undefined,
        sub: ''
    },
    {
        label: t('overview.kpi.successRate'),
        value:
            pipeline.value?.successRate30Days != null
                ? `${pipeline.value.successRate30Days}%`
                : '—',
        cls:
            pipeline.value?.successRate30Days != null && pipeline.value.successRate30Days < 90
                ? 'text-warning'
                : '',
        route: 'queue',
        query: { status: 'Failed' },
        sub: ''
    },
    {
        // 5th cell (observation batch): subtitle backlog with the pinned /
        // webhook-boost / next-round breakdown — every number from the
        // backlog aggregate, nothing about quotas (red line).
        label: t('overview.kpi.subtitleBacklog'),
        value: subtitleBacklog.value?.backlog ?? '—',
        route: 'subtitle-history',
        query: undefined,
        sub: subtitleBacklog.value
            ? t('dashboard.subtitles.subline', {
                  pinned: subtitleBacklog.value.pinnedCount,
                  boosted: subtitleBacklog.value.webhookBoostedCount,
                  next: subtitleBacklog.value.nextRoundCount
              })
            : ''
    }
])

const healthDot = (
    provider: ITranslationServiceHealth
): { cls: string; text: string; title: string } => {
    if (!provider.configured) {
        return {
            cls: 'bg-line',
            text: t('overview.health.offline'),
            title: t('overview.health.offlineHint')
        }
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

const feedIcon = (event: ITranslationRequestEventSummary): { icon: string; cls: string } => {
    // typed timeline events (P1-2) render their finer-grained glyph first
    switch (event.eventType) {
        case TRANSLATION_EVENT_TYPE.PROVIDER_FAILED:
            return { icon: '✗', cls: 'text-danger' }
        case TRANSLATION_EVENT_TYPE.DEGRADED:
            return { icon: '△', cls: 'text-warning' }
        case TRANSLATION_EVENT_TYPE.WRITEBACK:
            return { icon: '✓', cls: 'text-success' }
    }
    switch (event.status) {
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

const feedText = (event: ITranslationRequestEventSummary): string => {
    if (event.eventType === TRANSLATION_EVENT_TYPE.DEGRADED && event.message) {
        // same typographic arrow as the queue event timeline (QA P3-4)
        return t('overview.feed.degraded', { edge: formatDegradationEdge(event.message) })
    }
    if (event.eventType === TRANSLATION_EVENT_TYPE.PROVIDER_FAILED && event.provider) {
        return t('overview.feed.providerFailed', { provider: event.provider })
    }
    return event.message ?? statusLabel(event.status)
}

const statusLabel = (status: string): string => {
    const camel = status.charAt(0).toLowerCase() + status.slice(1)
    return t(`common.translationStatus.${camel}`)
}

const formatTime = (createdAt: string): string =>
    new Date(createdAt).toLocaleTimeString(locale.value, { hour: '2-digit', minute: '2-digit' })

// live topbar sub: pipeline state + seconds since the last refresh.
// State completeness (screen-01): when the live hub is down (reconnecting
// or closed) the sub degrades to「离线 · 重连中」instead of stale counters.
const lastFetchAt = ref(Date.now())
const subTimer = ref<ReturnType<typeof setInterval> | undefined>(undefined)

const isHubDegraded = (): boolean => {
    const hubState = signalR.state.hubs['TranslationRequests']
    return hubState != null && !hubState.isConnected
}

const updateSub = (): void => {
    if (isHubDegraded()) {
        uiStore.setSubOverride(t('topbar.sub.offlineReconnecting'))
        return
    }
    const running = inProgressCount.value > 0
    uiStore.setSubOverride(
        t('topbar.sub.dashboardLive', {
            state: running ? t('topbar.sub.pipelineRunning') : t('topbar.sub.pipelineIdle'),
            seconds: Math.max(0, Math.round((Date.now() - lastFetchAt.value) / 1000))
        })
    )
}
const fetchOverview = async (): Promise<void> => {
    const [
        pipelineResult,
        countsResult,
        activeResult,
        healthResult,
        eventsResult,
        trendResult,
        backlogResult
    ] = await Promise.allSettled([
        statisticsApi.getPipelineStatistics(),
        translationRequestApi.statusCounts(),
        translationRequestApi.requests<IPagedResult<ITranslationRequest>>(
            1,
            '',
            'CreatedAt',
            false,
            'InProgress'
        ),
        translateApi.getHealth(),
        translationRequestApi.getRecentEvents(20),
        statisticsApi.getDailyTrendStatistics(14),
        subtitleSearchService.backlog()
    ])
    if (pipelineResult.status === 'fulfilled') pipeline.value = pipelineResult.value
    if (countsResult.status === 'fulfilled') counts.value = countsResult.value
    if (activeResult.status === 'fulfilled') activeItems.value = activeResult.value.items
    if (healthResult.status === 'fulfilled') health.value = healthResult.value
    if (eventsResult.status === 'fulfilled') events.value = eventsResult.value
    if (trendResult.status === 'fulfilled') trend.value = trendResult.value
    // A failing backlog read leaves the KPI at '—' — honest absence, never
    // a stale or made-up number (the allSettled contract of this page).
    if (backlogResult.status === 'fulfilled') subtitleBacklog.value = backlogResult.value
    lastFetchAt.value = Date.now()
    updateSub()
}

let refreshTimer: ReturnType<typeof setInterval> | undefined
const hubConnection = ref<Hub | undefined>(undefined)

// topbar CTA (prototype): global import entry
onMounted(() => {
    uiStore.registerCta(t('library.toolbar.import'), () => router.push({ name: 'import' }))
    updateSub()
    subTimer.value = setInterval(updateSub, 1000)
})
onUnmounted(() => {
    clearInterval(subTimer.value)
    uiStore.clearCta()
    uiStore.setSubOverride('')
})

onMounted(async () => {
    await fetchOverview()
    refreshTimer = setInterval(fetchOverview, REFRESH_INTERVAL_MS)

    // Per-request traffic lives on the request's own group since the
    // broadcast collapse — the overview tracks the live rows by joining
    // exactly their groups (re-synced whenever the active set refreshes).
    hubConnection.value = await signalR.connect(
        'TranslationRequests',
        '/signalr/TranslationRequests'
    )
    hubConnection.value.on('RequestProgress', onProgress)
})

// joins the active rows' per-request groups; re-syncs on every overview
// refresh (completions swap the active set) and leaves all on unmount
useRequestGroups(
    hubConnection,
    computed(() => activeItems.value.map((item) => item.id))
)

onUnmounted(() => {
    if (refreshTimer) clearInterval(refreshTimer)
    hubConnection.value?.off('RequestProgress', onProgress)
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
        (
            [
                TRANSLATION_STATUS.COMPLETED,
                TRANSLATION_STATUS.FAILED,
                TRANSLATION_STATUS.CANCELLED,
                TRANSLATION_STATUS.INTERRUPTED
            ] as string[]
        ).includes(progress.status)
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
