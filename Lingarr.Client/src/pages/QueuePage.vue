<template>
    <div class="w-full flex h-full min-h-0 flex-col overflow-hidden p-4 lg:p-6">
        <!-- Status filter tabs (per prototype queue screen) -->
        <div class="mb-2 flex shrink-0 flex-wrap items-center gap-2">
            <button
                v-for="tab in statusTabs"
                :key="tab.value ?? 'all'"
                type="button"
                class="cursor-pointer border-b-2 px-4 py-1.5 text-sm font-medium transition-colors"
                :class="
                    (filter.status ?? null) === tab.value
                        ? 'border-accent text-primary-content'
                        : 'text-secondary-content/60 hover:text-primary-content border-transparent'
                "
                @click="setStatusFilter(tab.value)">
                {{ tab.label }}
                <span class="mono text-xs opacity-70">{{ tab.count }}</span>
            </button>
        </div>

        <!-- Search and Filters -->
        <ListToolbar
            v-model:filter="filter"
            :sort-options="[
                {
                    label: t('translations.list.sortByAdded'),
                    value: 'CreatedAt'
                },
                {
                    label: t('translations.list.sortByCompleted'),
                    value: 'CompletedAt'
                },
                {
                    label: t('translations.list.sortByTitle'),
                    value: 'Title'
                }
            ]"
            @reload="translationRequestStore.fetch()">
            <template #actions>
                <CheckboxComponent
                    v-if="isSelectMode"
                    class="mr-1"
                    :model-value="selectAll"
                    :aria-label="t('translations.list.select')"
                    @change="toggleSelectAll()" />
                <button
                    v-if="isSelectMode"
                    class="hover:text-primary-content/50 border-line text-primary-content cursor-pointer rounded-md border px-2 py-1 transition-colors"
                    @click="handleDelete">
                    {{ t('translations.list.deleteWithCount', { count: selectedCount }) }}
                </button>
                <button
                    v-if="!isSelectMode"
                    class="hover:text-primary-content/50 border-line text-primary-content cursor-pointer rounded-md border px-2 py-1 transition-colors"
                    @click="handleBatchConvert">
                    {{ t('translations.list.batchConvert') }}
                </button>
                <button
                    class="hover:text-primary-content/50 border-line text-primary-content cursor-pointer rounded-md border px-2 py-1 transition-colors"
                    @click="toggleSelectMode">
                    {{
                        isSelectMode ? t('translations.list.cancel') : t('translations.list.select')
                    }}
                </button>
            </template>
        </ListToolbar>

        <div class="shell-scroll w-full min-h-0 flex-1 space-y-1 overflow-y-auto px-1">
            <div
                v-for="item in translationRequests.items"
                :key="item.id"
                class="border-line hover:bg-accent/5 relative flex flex-wrap items-center gap-x-4 gap-y-2 rounded-md border border-b py-2.5 pl-3 pr-4 transition-colors md:grid md:grid-cols-[minmax(0,1.4fr)_minmax(140px,1fr)_120px_auto] md:gap-4"
                :style="{ borderLeft: '3px solid ' + statusColor(item.status) }">
                <!-- 标题块：标题 + 徽章 + 副行（原型风格：语言对/状态信息内联） -->
                <div class="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1">
                    <CheckboxComponent
                        v-if="isSelectMode"
                        class="shrink-0"
                        :model-value="isSelected(item)"
                        @change="toggleSelect(item)" />
                    <span class="text-primary-content truncate font-medium" :title="item.title">
                        {{ item.title }}
                    </span>
                    <span
                        v-if="item.source === 'Import'"
                        class="border-line text-secondary-content shrink-0 rounded border px-1.5 py-0.5 text-xs">
                        {{ t('translations.badges.import') }}
                    </span>
                    <span
                        v-if="item.serviceType"
                        class="border-line text-secondary-content mono shrink-0 rounded border px-1.5 py-0.5 text-xs lowercase">
                        {{ item.serviceType }}
                    </span>
                    <span class="mono text-secondary-content/70 shrink-0 text-xs">
                        {{ item.sourceLanguage.toUpperCase() }} →
                        {{ item.targetLanguage.toUpperCase() }}
                    </span>
                    <BadgeComponent
                        v-if="item.status === TRANSLATION_STATUS.COMPLETED"
                        :classes="
                            item.isBilingual
                                ? 'text-success-content border-success/40 bg-success/50'
                                : 'text-primary-content/70 border-line bg-secondary'
                        ">
                        {{
                            item.isBilingual
                                ? t('translations.bilingual')
                                : t('translations.monolingual')
                        }}
                    </BadgeComponent>
                    <span
                        v-if="item.status === TRANSLATION_STATUS.FAILED && item.errorMessage"
                        class="text-danger/80 w-full truncate text-xs"
                        :title="item.errorMessage">
                        {{ item.errorMessage }}
                    </span>
                    <span
                        v-else-if="
                            item.status === TRANSLATION_STATUS.COMPLETED && item.translatedSubtitle
                        "
                        class="text-secondary-content/60 w-full truncate text-xs"
                        :title="item.translatedSubtitle">
                        {{ t('translations.wroteBack') }} {{ fileName(item.translatedSubtitle) }}
                    </span>
                    <span
                        v-else-if="
                            item.status === TRANSLATION_STATUS.CANCELLED ||
                            item.status === TRANSLATION_STATUS.INTERRUPTED
                        "
                        class="text-secondary-content/60 text-xs">
                        <TranslationStatus :translation-status="item.status" />
                    </span>
                </div>

                <!-- 轨道条：全状态常显，状态着色 -->
                <div class="min-w-0">
                    <TranslationProgress :progress="item.progress" :status="item.status" />
                </div>

                <!-- ETA 列 -->
                <div class="mono text-right text-xs">
                    <span
                        v-if="item.status === TRANSLATION_STATUS.PAUSED"
                        class="text-warning"
                        :title="t('translations.eta.pausedHint')">
                        ⏸ {{ item.progress }}%
                    </span>
                    <span
                        v-else-if="item.status === TRANSLATION_STATUS.INPROGRESS"
                        class="text-secondary-content"
                        :title="t('translations.eta.remainingHint')">
                        {{ remainingEta(item) }}
                    </span>
                    <span v-else-if="item.status === TRANSLATION_STATUS.FAILED" class="text-danger">
                        ✗ {{ item.progress }}%
                        <span v-if="item.retryCount" class="text-secondary-content/70">
                            · {{ t('translations.eta.retries', { count: item.retryCount }) }}
                        </span>
                    </span>
                    <span
                        v-else-if="
                            item.status === TRANSLATION_STATUS.COMPLETED && item.completedAt
                        "
                        class="text-success flex items-center justify-end gap-1"
                        :title="formatDateTime(item.completedAt)">
                        ✓ <TranslationCompletedAt :completed-at="item.completedAt" />
                    </span>
                    <span v-else class="text-secondary-content/50">—</span>
                </div>

                <!-- 操作 -->
                <div class="flex items-center justify-end gap-1">
                    <TranslationAction
                        :item="item"
                        :on-action="(action) => handleAction(item, action)" />
                </div>
            </div>
        </div>

        <PaginationComponent
            v-if="translationRequests.totalCount"
            v-model="filter"
            :total-count="translationRequests.totalCount"
            :page-size="translationRequests.pageSize" />

        <p v-if="runtime" class="mono text-secondary-content/70 mt-3 shrink-0 px-4 text-xs">
            {{
                t('translations.queueFooterLive', {
                    depth: runtime.queueDepth,
                    workers: runtime.concurrentJobs
                })
            }}
        </p>
    </div>
</template>

<script setup lang="ts">
import { computed, ref, onMounted, onUnmounted, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import {
    Hub,
    IPipelineStatistics,
    ITranslationRequest,
    TRANSLATION_ACTIONS,
    TRANSLATION_STATUS
} from '@/types'
import useTranslationRequestStore from '@/store/translationRequest'
import services from '@/services'
import { useUiStore } from '@/store/ui'
import { useSignalR } from '@/composables/useSignalR'
import { useSelection } from '@/composables/useSelection'
import { usePagedList } from '@/composables/usePagedList'
import { formatDateTime } from '@/utils/date'
import PaginationComponent from '@/components/common/PaginationComponent.vue'
import ListToolbar from '@/components/common/ListToolbar.vue'
import TranslationStatus from '@/components/common/TranslationStatus.vue'
import TranslationProgress from '@/components/common/TranslationProgress.vue'
import TranslationAction from '@/components/common/TranslationAction.vue'
import TranslationCompletedAt from '@/components/common/TranslationCompletedAt.vue'
import BadgeComponent from '@/components/common/BadgeComponent.vue'
import CheckboxComponent from '@/components/common/CheckboxComponent.vue'

const signalR = useSignalR()
const hubConnection = ref<Hub>()
const translationRequestStore = useTranslationRequestStore()
const { t } = useI18n()
const router = useRouter()
const uiStore = useUiStore()

// computed so tab labels re-resolve when the locale changes
const statusTabs = computed(() => [
    { label: t('translations.queueTabs.all'), value: null, count: totalAll() },
    { label: t('translations.queueTabs.inProgress'), value: TRANSLATION_STATUS.INPROGRESS, count: statusCount(TRANSLATION_STATUS.INPROGRESS) },
    { label: t('translations.queueTabs.pending'), value: TRANSLATION_STATUS.PENDING, count: statusCount(TRANSLATION_STATUS.PENDING) },
    { label: t('translations.queueTabs.failed'), value: TRANSLATION_STATUS.FAILED, count: statusCount(TRANSLATION_STATUS.FAILED) },
    { label: t('translations.queueTabs.completed'), value: TRANSLATION_STATUS.COMPLETED, count: statusCount(TRANSLATION_STATUS.COMPLETED) }
])

const statusCount = (status: string): number =>
    translationRequestStore.getStatusCounts[status] ?? 0

const statusColor = (status: string): string => {
    switch (status) {
        case TRANSLATION_STATUS.INPROGRESS:
            return 'var(--accent)'
        case TRANSLATION_STATUS.FAILED:
            return 'var(--danger)'
        case TRANSLATION_STATUS.COMPLETED:
            return 'var(--success)'
        case TRANSLATION_STATUS.PAUSED:
            return 'var(--warning)'
        default:
            return 'var(--line)'
    }
}

// linear ETA: elapsed / progress * remaining; only meaningful with a start
// time and some progress, otherwise falls back to the plain percentage
const remainingEta = (item: ITranslationRequest): string => {
    if (item.startedAt && item.progress > 0) {
        const elapsed = Date.now() - new Date(item.startedAt).getTime()
        const remaining = (elapsed / item.progress) * (100 - item.progress)
        if (remaining > 0 && remaining < 1000 * 60 * 60 * 24) {
            const totalSeconds = Math.round(remaining / 1000)
            const minutes = Math.floor(totalSeconds / 60)
            const seconds = totalSeconds % 60
            return `${item.progress}% · ~${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`
        }
    }
    return `${item.progress}%`
}

const fileName = (path: string): string => path.split('/').pop() ?? path

const totalAll = (): number =>
    Object.values(translationRequestStore.getStatusCounts).reduce((sum, count) => sum + count, 0)

const setStatusFilter = async (status: string | null): Promise<void> => {
    await translationRequestStore.setStatusFilter(status)
}

const { items: translationRequests, filter } = usePagedList<ITranslationRequest>({
    items: () => translationRequestStore.getTranslationRequests,
    filter: () => translationRequestStore.getFilter,
    setFilter: (value) => translationRequestStore.setFilter(value),
    fetch: () => translationRequestStore.fetch()
})

const {
    selectedIds,
    selectedCount,
    selectAll,
    isSelected,
    toggleSelect,
    toggleSelectAll,
    clearSelection
} = useSelection<ITranslationRequest>(() => translationRequests.value.items)

async function handleAction(translationRequest: ITranslationRequest, action: TRANSLATION_ACTIONS) {
    switch (action) {
        case TRANSLATION_ACTIONS.CANCEL:
            return await translationRequestStore.cancel(translationRequest)
        case TRANSLATION_ACTIONS.REMOVE:
            return await translationRequestStore.remove(translationRequest)
        case TRANSLATION_ACTIONS.RETRY:
            return await translationRequestStore.retry(translationRequest)
        case TRANSLATION_ACTIONS.RESUME:
            return await translationRequestStore.resume(translationRequest)
        case TRANSLATION_ACTIONS.PROOFREAD:
            return await translationRequestStore.proofread(translationRequest)
        case TRANSLATION_ACTIONS.PAUSE:
            return await translationRequestStore.pause(translationRequest)
        case TRANSLATION_ACTIONS.RESUME_PAUSED:
            return await translationRequestStore.resumePaused(translationRequest)
        default:
            console.error('unknown translation request action: ' + action)
    }
}

// topbar CTA (prototype): new task — translation starts in the library
onMounted(() => {
    uiStore.registerCta(t('topbar.cta.newTask'), () => router.push({ name: 'library' }))
})
onUnmounted(() => {
    uiStore.clearCta()
    uiStore.setSubOverride('')
})

/// queue footer: live Hangfire depth + worker count (was static copy)
const runtime = ref<IPipelineStatistics | null>(null)
onMounted(async () => {
    try {
        runtime.value = await services.statistics.getPipelineStatistics()
    } catch {
        runtime.value = null
    }
})

// live topbar sub: total tasks across the status tabs
watch(
    () => translationRequestStore.getStatusCounts,
    (counts) => {
        const total = Object.values(counts ?? {}).reduce((sum: number, n) => sum + Number(n ?? 0), 0)
        uiStore.setSubOverride(t('topbar.sub.queueLive', { count: total }))
    },
    { immediate: true, deep: true }
)

onMounted(async () => {
    await translationRequestStore.fetchProofreadStatus()
    hubConnection.value = await signalR.connect(
        'TranslationRequests',
        '/signalr/TranslationRequests'
    )

    await hubConnection.value.joinGroup({ group: 'TranslationRequests' })
    hubConnection.value.on('RequestProgress', translationRequestStore.updateProgress)
})

onUnmounted(async () => {
    hubConnection.value?.off('RequestProgress', translationRequestStore.updateProgress)
})

const isSelectMode = ref(false)

const toggleSelectMode = () => {
    isSelectMode.value = !isSelectMode.value
    if (!isSelectMode.value) {
        clearSelection()
    }
}

const handleDelete = async () => {
    const selected = translationRequests.value.items.filter((request) =>
        selectedIds.value.has(request.id)
    )
    for (const request of selected) {
        await translationRequestStore.remove(request)
    }
    clearSelection()
    translationRequestStore.fetch()
}

const handleBatchConvert = async () => {
    if (!window.confirm(t('translations.list.batchConvertConfirm'))) return
    try {
        await services.translationRequest.convertAll(true)
    } catch (error) {
        console.error('Batch conversion failed:', error)
    }
    translationRequestStore.fetch()
}
</script>
