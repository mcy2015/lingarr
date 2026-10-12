<template>
    <div class="w-full">
        <!-- Status filter tabs (per prototype queue screen) -->
        <div class="mb-2 flex flex-wrap items-center gap-2">
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

        <div class="w-full px-4">
            <div class="border-line hidden border-b font-bold md:grid md:grid-cols-12">
                <div class="col-span-4 px-4 py-2">{{ t('translations.columns.title') }}</div>
                <div class="col-span-1 px-4 py-2">{{ t('translations.columns.source') }}</div>
                <div class="col-span-1 px-4 py-2">{{ t('translations.columns.target') }}</div>
                <div class="col-span-1 px-4 py-2">{{ t('translations.columns.status') }}</div>
                <div class="px-4 py-2" :class="isSelectMode ? 'col-span-2' : 'col-span-3'">
                    {{ t('translations.columns.progress') }}
                </div>
                <div class="col-span-1 px-4 py-2">{{ t('translations.columns.completed') }}</div>
                <div class="col-span-1"></div>
                <div
                    v-if="isSelectMode"
                    class="col-span-1 flex items-center justify-center px-4 py-2">
                    <CheckboxComponent :model-value="selectAll" @change="toggleSelectAll()" />
                </div>
            </div>
            <div
                v-for="item in translationRequests.items"
                :key="item.id"
                class="border-line hover:bg-accent/5 flex flex-wrap items-center gap-x-3 gap-y-2 border-b py-3 transition-colors md:grid md:grid-cols-12 md:gap-0 md:py-0">
                <div class="flex w-full items-center gap-2 md:col-span-4 md:w-auto md:px-4 md:py-2">
                    <span
                        v-if="item.mediaType === MEDIA_TYPE.EPISODE"
                        v-show-title
                        class="block min-w-0 flex-1 cursor-help md:flex-none"
                        :title="item.title">
                        {{ item.title }}
                    </span>
                    <span
                        v-if="item.source === 'Import'"
                        class="border-line text-secondary-content rounded border px-1.5 py-0.5 text-xs">
                        {{ t('translations.badges.import') }}
                    </span>
                    <span v-else class="min-w-0 flex-1 md:flex-none">
                        {{ item.title }}
                    </span>
                    <span class="ml-auto flex flex-none items-center gap-2 md:hidden">
                        <TranslationAction
                            :item="item"
                            :on-action="(action) => handleAction(item, action)" />
                        <CheckboxComponent
                            v-if="isSelectMode"
                            :model-value="isSelected(item)"
                            @change="toggleSelect(item)" />
                    </span>
                </div>
                <div class="flex items-center md:col-span-1 md:px-4 md:py-2">
                    <BadgeComponent classes="font-mono text-secondary-content border-line bg-tertiary">
                        {{ item.sourceLanguage.toUpperCase() }}
                    </BadgeComponent>
                </div>
                <div class="flex items-center gap-2 md:col-span-1 md:px-4 md:py-2">
                    <span class="text-primary-content/50 md:hidden">→</span>
                    <BadgeComponent classes="font-mono text-secondary-content border-line bg-tertiary">
                        {{ item.targetLanguage.toUpperCase() }}
                    </BadgeComponent>
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
                </div>
                <div class="flex items-center md:col-span-1 md:px-4 md:py-2">
                    <TranslationStatus :translation-status="item.status" />
                </div>
                <div
                    class="items-center md:flex md:px-4 md:py-2"
                    :class="[
                        isSelectMode ? 'md:col-span-2' : 'md:col-span-3',
                        item.status === TRANSLATION_STATUS.INPROGRESS && item.progress
                            ? 'flex w-full md:w-auto'
                            : 'hidden'
                    ]">
                    <div
                        v-if="item.status === TRANSLATION_STATUS.INPROGRESS && item.progress"
                        class="w-full">
                        <TranslationProgress :progress="item.progress" />
                    </div>
                </div>
                <div
                    :class="item.completedAt ? 'flex' : 'hidden md:flex'"
                    class="items-center gap-2 md:col-span-1 md:px-4 md:py-2">
                    <TranslationCompletedAt
                        v-if="item.completedAt"
                        :completed-at="item.completedAt" />
                    <span
                        class="text-primary-content/50 text-xs font-semibold tracking-wide uppercase md:hidden">
                        {{ t('translations.columns.completed') }}
                    </span>
                </div>
                <div
                    class="hidden items-center justify-between md:col-span-1 md:flex md:justify-end md:py-2">
                    <div class="flex items-center justify-center gap-1">
                        <TranslationAction
                            :item="item"
                            :on-action="(action) => handleAction(item, action)" />
                    </div>
                </div>
                <div
                    v-if="isSelectMode"
                    class="hidden items-center justify-center py-2 md:col-span-1 md:flex md:px-4">
                    <CheckboxComponent
                        :model-value="isSelected(item)"
                        @change="toggleSelect(item)" />
                </div>
            </div>
        </div>
        <PaginationComponent
            v-if="translationRequests.totalCount"
            v-model="filter"
            :total-count="translationRequests.totalCount"
            :page-size="translationRequests.pageSize" />
    </div>
</template>

<script setup lang="ts">
import { computed, ref, onMounted, onUnmounted } from 'vue'
import { useI18n } from 'vue-i18n'
import {
    Hub,
    ITranslationRequest,
    MEDIA_TYPE,
    TRANSLATION_ACTIONS,
    TRANSLATION_STATUS
} from '@/types'
import useTranslationRequestStore from '@/store/translationRequest'
import services from '@/services'
import { useSignalR } from '@/composables/useSignalR'
import { useSelection } from '@/composables/useSelection'
import { usePagedList } from '@/composables/usePagedList'
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

// computed so tab labels re-resolve when the locale changes
const statusTabs = computed(() => [
    { label: t('translations.queueTabs.all'), value: null, count: totalAll() },
    { label: t('translations.queueTabs.inProgress'), value: TRANSLATION_STATUS.INPROGRESS, count: statusCount(TRANSLATION_STATUS.INPROGRESS) },
    { label: t('translations.queueTabs.pending'), value: TRANSLATION_STATUS.PENDING, count: statusCount(TRANSLATION_STATUS.PENDING) },
    { label: t('translations.queueTabs.completed'), value: TRANSLATION_STATUS.COMPLETED, count: statusCount(TRANSLATION_STATUS.COMPLETED) },
    { label: t('translations.queueTabs.failed'), value: TRANSLATION_STATUS.FAILED, count: statusCount(TRANSLATION_STATUS.FAILED) }
])

const statusCount = (status: string): number =>
    translationRequestStore.getStatusCounts[status] ?? 0

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
        default:
            console.error('unknown translation request action: ' + action)
    }
}

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
