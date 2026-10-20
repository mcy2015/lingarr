<template>
    <div v-if="configured" class="w-full flex h-full min-h-0 flex-col gap-3.5 overflow-hidden p-4 lg:p-6">
        <!-- ═══ 工具条：类型段选 · 搜索 · 过滤 · 导入 · 全局统计 ═══ -->
        <div class="flex shrink-0 flex-wrap items-center gap-2">
            <div class="border-line seg inline-flex overflow-hidden rounded-md border" role="group" :aria-label="t('library.tabs.group')">
                <button
                    v-for="option in typeOptions"
                    :key="option.value"
                    type="button"
                    class="border-line text-secondary-content hover:text-primary-content cursor-pointer border-r px-3 py-1.5 text-[12.5px] transition-colors last:border-r-0"
                    :class="type === option.value ? 'bg-tertiary text-primary-content' : ''"
                    :aria-pressed="type === option.value"
                    @click="type = option.value">
                    {{ option.label }}
                </button>
            </div>

            <label class="border-line bg-secondary focus-within:border-accent/60 flex min-w-[220px] flex-1 items-center gap-2 rounded-md border px-2.5 py-1.5 sm:max-w-xs">
                <Icon name="search" class="text-secondary-content h-4 w-4 shrink-0" aria-hidden="true" />
                <input
                    v-model="search"
                    type="search"
                    class="placeholder:text-secondary-content/60 w-full bg-transparent text-sm outline-none"
                    :placeholder="t('common.searchMedia')" />
            </label>

            <label class="flex items-center gap-1.5 text-[12.5px]">
                <span class="text-secondary-content">{{ t('library.filter.targetLanguage') }}</span>
                <select
                    v-model="targetFilter"
                    class="border-line bg-secondary text-primary-content cursor-pointer rounded-md border px-2 py-1 text-xs">
                    <option value="">{{ t('library.filter.all') }}</option>
                    <option v-for="lang in targetLanguages" :key="lang.code" :value="lang.code">
                        {{ lang.code.toUpperCase() }}
                    </option>
                </select>
            </label>

            <label class="flex items-center gap-1.5 text-[12.5px]">
                <span class="text-secondary-content">{{ t('library.filter.state') }}</span>
                <select
                    v-model="stateFilter"
                    class="border-line bg-secondary text-primary-content cursor-pointer rounded-md border px-2 py-1 text-xs">
                    <option value="">{{ t('library.filter.all') }}</option>
                    <option v-for="option in stateOptions" :key="option.value" :value="option.value">
                        {{ option.label }}
                    </option>
                </select>
            </label>

            <ButtonComponent size="sm" @click="router.push({ name: 'import' })">
                {{ t('library.toolbar.import') }}
            </ButtonComponent>

            <span class="mono text-secondary-content ml-auto hidden text-xs md:block">
                {{ t('library.toolbar.stats', { items: totalCount, missing: missingCount, inProgress: inProgressCount }) }}
            </span>
        </div>

        <!-- ═══ 矩阵表（弹性行，表体卡内滚动；列表容器滚动位置在离开时
             持久化，返回列表时恢复——配合 router 的 savedPosition） ═══ -->
        <div class="border-line bg-secondary flex min-h-0 flex-1 flex-col rounded-md border">
            <div
                ref="listScrollRef"
                class="shell-scroll min-h-0 flex-1 overflow-auto"
                @scroll="onListScroll">
            <table class="w-full text-[13px]">
                <thead>
                    <tr>
                        <th :class="thClass" style="width: 34px">
                            <input
                                type="checkbox"
                                class="accent-accent cursor-pointer"
                                :checked="allVisibleSelected"
                                :aria-label="t('library.table.selectAll')"
                                :disabled="items.length === 0"
                                @change="toggleSelectAll" />
                        </th>
                        <th :class="thClass">{{ t('library.table.title') }}</th>
                        <th :class="thClass">{{ t('library.table.audio') }}</th>
                        <th :class="thClass">{{ t('library.table.tracks') }}</th>
                        <th :class="thClass">{{ t('library.table.status') }}</th>
                        <th :class="thClass">{{ t('library.table.lastActivity') }}</th>
                        <th :class="thClass" style="width: 168px"></th>
                    </tr>
                </thead>
                <tbody>
                    <tr v-if="items.length === 0">
                        <td :class="tdClass" colspan="7">
                            <span class="text-secondary-content/60 text-xs">{{ loading ? t('common.loading') : t('library.empty') }}</span>
                        </td>
                    </tr>
                    <template v-for="item in items" :key="item.mediaType + ':' + item.id">
                        <tr class="hover:bg-tertiary/50">
                            <td :class="tdClass">
                                <input
                                    type="checkbox"
                                    class="accent-accent cursor-pointer"
                                    :checked="isSelected(item)"
                                    :aria-label="t('library.table.select', { title: item.title })"
                                    @change="toggleSelect(item)" />
                            </td>
                            <td :class="tdClass">
                                <div class="flex min-w-0 items-center gap-2">
                                    <span class="badge" :class="item.mediaType === 'Show' ? 'badge-show' : ''">
                                        {{ item.mediaType === 'Show' ? t('library.badge.show') : t('library.badge.movie') }}
                                    </span>
                                    <a
                                        :href="detailHref(item)"
                                        class="text-primary-content focus-visible:ring-accent hover:underline cursor-pointer truncate rounded-md font-semibold focus-visible:ring-2 focus-visible:outline-none"
                                        :title="item.title"
                                        :aria-label="t('library.table.openDetail', { type: item.mediaType === 'Show' ? t('library.badge.show') : t('library.badge.movie'), title: item.title })"
                                        @click="onTitleClick(item, $event)">
                                        {{ item.title }}
                                    </a>
                                    <span v-if="item.mediaType === 'Show'" class="text-secondary-content hidden shrink-0 text-[11.5px] sm:inline">
                                        {{ t('library.meta.show', { seasons: item.seasonCount, episodes: item.episodeCount }) }}
                                    </span>
                                    <!-- library B2 metadata chips: year · rating ·
                                         quality · size, only values that really
                                         exist render (no static decoration) -->
                                    <span
                                        v-if="metaChips(item)"
                                        class="text-secondary-content hidden shrink-0 text-[11.5px] sm:inline"
                                        :title="metaChips(item)"
                                        data-test="media-meta-chips">
                                        {{ metaChips(item) }}
                                    </span>
                                </div>
                            </td>
                            <td :class="tdClass">
                                <!-- 音轨列:归一化语言码大写点隔;null/缺失=未知
                                     (与详情页音轨行同一约定) -->
                                <span
                                    v-if="item.audioLanguages?.length"
                                    class="mono text-primary-content text-[11.5px]"
                                    :title="t('library.table.audio')"
                                    data-test="audio-languages-cell">
                                    {{ audioCellText(item) }}
                                </span>
                                <span
                                    v-else-if="item.suggestedAudioLanguage"
                                    class="text-secondary-content/60 text-[11.5px]"
                                    :title="t('library.audioSuggestedTitle')"
                                    data-test="audio-suggested-cell">
                                    {{ item.suggestedAudioLanguage.toUpperCase() }}?
                                </span>
                                <span v-else class="text-secondary-content/50 text-[11.5px]">—</span>
                            </td>
                            <td :class="tdClass">
                                <div class="flex max-w-[340px] flex-wrap gap-1">
                                    <span
                                        v-for="track in visibleTracks(item)"
                                        :key="track.language"
                                        :class="chipClass(track.state)"
                                        :title="trackTitle(track)">
                                        {{ trackLabel(track) }}
                                    </span>
                                    <span
                                        v-if="item.tracks.length > visibleTrackCount"
                                        class="mono text-secondary-content/60 inline-flex items-center text-[11px]">
                                        +{{ item.tracks.length - visibleTrackCount }}
                                    </span>
                                    <!-- 需求卡 B(实机走查修订):「无需字幕」豁免徽章已移至
                                         状态列(state-no-subtitle-needed),与本列解耦 —— 勿再
                                         往本列加回;音轨未知渲染「?」弱标记(与 audio-unknown
                                         过滤一一对应,AC10),仅 audioLanguages === null 时渲染,
                                         缺失字段的存量载荷(undefined)不渲染 -->
                                    <span
                                        v-if="item.audioLanguages === null"
                                        class="mono border-line/60 text-secondary-content/50 inline-flex items-center rounded border border-dashed px-1 py-px text-[10px]"
                                        :title="t('library.audioUnknownBadgeTitle')"
                                        data-test="audio-unknown-badge">
                                        ? {{ t('library.audioUnknownBadge') }}
                                    </span>
                                </div>
                            </td>
                            <td :class="tdClass">
                                <!-- 多维度状态组合呈现:翻译流转态与正交的「无需字幕」
                                     豁免态并排渲染,互不遮盖 —— 豁免行的翻译态徽章
                                     (如「未排队」/「✓ 完成」)始终可见 -->
                                <div class="inline-flex max-w-full flex-wrap items-center gap-x-1.5 gap-y-0.5">
                                    <span :class="stateClass(item)">{{ stateLabel(item) }}</span>
                                    <span
                                        v-if="item.needsNoSubtitles"
                                        class="mono border-line/60 text-secondary-content/70 inline-flex items-center whitespace-nowrap rounded border border-dashed px-1 py-px text-[10px]"
                                        :title="t('library.nativeBadgeTitle')"
                                        data-test="state-no-subtitle-needed">
                                        {{ t('library.state.native') }}
                                    </span>
                                </div>
                            </td>
                            <td :class="tdClass">
                                <time class="mono text-secondary-content text-xs">
                                    {{ item.lastActivityAt ? formatTime(item.lastActivityAt) : '—' }}
                                </time>
                            </td>
                            <td :class="tdClass">
                                <div class="flex items-center justify-end gap-1.5 whitespace-nowrap">
                                    <button
                                        type="button"
                                        class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded border px-2 py-0.5 text-xs transition-colors"
                                        @click="goImport(item)">
                                        {{ item.mediaType === 'Show' ? t('library.action.seasonImport') : t('library.action.import') }}
                                    </button>
                                    <button
                                        type="button"
                                        class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded border px-2 py-0.5 text-xs transition-colors"
                                        @click="goDetail(item)">
                                        {{ t('library.action.detail') }}
                                    </button>
                                </div>
                            </td>
                        </tr>
                    </template>
                </tbody>
            </table>
            </div>
        </div>

        <!-- ═══ 批量托盘（吸底） ═══ -->
        <div
            v-if="selection.size > 0"
            class="border-accent bg-secondary z-10 flex shrink-0 flex-wrap items-center gap-3 rounded-md border px-4 py-2.5 shadow-lg">
            <span class="text-[12.5px]">
                {{ t('library.tray.selected', { count: selection.size }) }}
            </span>
            <ContextMenu ref="trayMenu" @select="translateSelection">
                <button
                    type="button"
                    class="bg-accent text-accent-content hover:bg-accent/90 cursor-pointer rounded-md px-3 py-1 text-xs font-semibold transition-colors">
                    {{ t('library.tray.translate') }}
                </button>
            </ContextMenu>
            <button
                type="button"
                class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-3 py-1 text-xs transition-colors"
                @click="clearSelection">
                {{ t('library.tray.clear') }}
            </button>
            <span class="flex-1"></span>
            <span class="text-secondary-content text-xs">
                <template v-if="estimate?.cost != null">
                    {{ t('library.tray.estimate', { cost: formatCost(estimate.cost), chars: formatNumber(estimate.characters) }) }}
                </template>
                <template v-else-if="estimate">
                    {{ t('library.tray.charsOnly', { chars: formatNumber(estimate.characters) }) }}
                </template>
            </span>
        </div>

        <PaginationComponent
            v-if="totalCount > pageSize"
            :model-value="pagination"
            :total-count="totalCount"
            :page-size="pageSize"
            @update:model-value="onPaginationUpdate" />
    </div>
    <NoMediaNotification v-else />
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import axios from 'axios'
import ButtonComponent from '@/cells/kernel/components/ButtonComponent.vue'
import ContextMenu from '@/cells/app/layout/ContextMenu.vue'
import Icon from '@/cells/kernel/components/icons/Icon.vue'
import NoMediaNotification from '@/cells/library/components/NoMediaNotification.vue'
import PaginationComponent from '@/cells/kernel/components/PaginationComponent.vue'
import { mediaService } from '@/cells/app/api/mediaService'
import { statisticsService } from '@/cells/app/api/statisticsService'
import { translationRequestService } from '@/cells/app/api/translationRequestService'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useTranslateStore } from '@/cells/app/stores/translate'
import { useUiStore } from '@/cells/app/stores/ui'
import { useToastStore } from '@/cells/app/stores/toast'
import {
    IBulkTranslateSummary,
    IFilter,
    ILanguage,
    IMediaLibraryItem,
    IPagedResult,
    ISubtitleTrackSummary,
    ITranslationEstimate,
    MEDIA_TRANSLATION_STATE,
    MEDIA_TYPE,
    SETTINGS
} from '@/types'

const VISIBLE_TRACKS = 4

const statisticsApi = statisticsService(axios)
const translationRequestApi = translationRequestService(axios)
const mediaApi = mediaService(axios)

const { t, locale } = useI18n()
const route = useRoute()
const router = useRouter()
const settingStore = useSettingStore()
const translateStore = useTranslateStore()
const uiStore = useUiStore()
const toastStore = useToastStore()
const trayMenu = ref<InstanceType<typeof ContextMenu> | null>(null)

const toastTip = (): void => {
    toastStore.push({ message: t('library.tray.needSelection'), type: 'info' })
}

const configured = computed(() => {
    const sonarr = settingStore.getSetting(SETTINGS.SONARR_SETTINGS_COMPLETED)
    const radarr = settingStore.getSetting(SETTINGS.RADARR_SETTINGS_COMPLETED)
    return sonarr === 'true' || radarr === 'true'
})

type LibraryType = 'all' | 'show' | 'movie'
// Filter/query sync (type is the precedent): every filter lives in the
// route query so a detail drill-down and back restores the exact view, and
// a shared URL carries the filter set. Defaults drop out of the query the
// same way `type` does (undefined removes the key).
const queryString = (value: unknown): string =>
    typeof value === 'string' ? value : ''
const type = ref<LibraryType>(normalizeType(route.query.type))
const search = ref(queryString(route.query.q))
const stateFilter = ref(queryString(route.query.state))
const targetFilter = ref(queryString(route.query.target))
const pagination = reactive({
    // restored from the query so a history back re-enters the page the
    // user left, not page 1
    pageNumber: Math.max(1, Number.parseInt(String(route.query.page ?? '1'), 10) || 1),
    searchQuery: '',
    sortBy: 'Title',
    isAscending: true
})
const pageSize = 20

const items = ref<IMediaLibraryItem[]>([])
const totalCount = ref(0)
const loading = ref(false)
const missingCount = ref(0)
const inProgressCount = ref(0)

// ── list scroll persistence ───────────────────────────────────
// The matrix scrolls inside its card container (the window itself never
// scrolls on this screen), so router-level savedPosition alone cannot
// restore it: the container's scrollTop persists in sessionStorage on every
// scroll (thus also on route leave) and re-applies once, after the first
// fetch renders the rows again.
const LIST_SCROLL_KEY = 'library-list-scroll-top'
const listScrollRef = ref<HTMLElement | null>(null)
let scrollRestorePending = true

function onListScroll(): void {
    const container = listScrollRef.value
    if (!container) return
    sessionStorage.setItem(LIST_SCROLL_KEY, String(container.scrollTop))
}

function restoreListScroll(): void {
    const saved = sessionStorage.getItem(LIST_SCROLL_KEY)
    if (saved === null || !listScrollRef.value) return
    listScrollRef.value.scrollTop = Number(saved) || 0
}

const selection = reactive(new Map<string, IMediaLibraryItem>())
const estimate = ref<ITranslationEstimate | null>(null)

const typeOptions = computed(() => [
    { label: t('library.tabs.all'), value: 'all' as LibraryType },
    { label: t('library.tabs.shows'), value: 'show' as LibraryType },
    { label: t('library.tabs.movies'), value: 'movie' as LibraryType }
])

/// Requirement card B (1st batch): two audio-derived states join the filter —
///「无需字幕」(native, exempt via target-covering audio or manual mark) and
///「音轨未知」(audio-unknown). Both pass straight through as the state param.
const stateOptions = computed(() => [
    { label: t('library.state.inProgress'), value: 'InProgress' },
    { label: t('library.state.queued'), value: 'Queued' },
    { label: t('library.state.completed'), value: 'Completed' },
    { label: t('library.state.failed'), value: 'Failed' },
    { label: t('library.state.none'), value: 'None' },
    { label: t('library.state.native'), value: 'native' },
    { label: t('library.state.audioUnknown'), value: 'audio-unknown' }
])

const targetLanguages = computed(() => {
    const settings = settingStore.getSettings
    return (settings.target_languages ?? []) as ILanguage[]
})

/// Deep link from the overview pipeline (screen-01「缺字幕检测」node,
/// ?missing=1): activate the missing-subtitles view. Expressed with the
/// existing server-side target-language filter — "items whose primary
/// target track is not complete" is exactly the missing view, so no new
/// query semantics are needed. Only fills an empty filter; a user-chosen
/// target is never clobbered.
watch(
    () => route.query.missing,
    (value) => {
        if (value === '1' && !targetFilter.value) {
            const primary = targetLanguages.value[0]
            if (primary) {
                targetFilter.value = primary.code
            }
        }
    },
    { immediate: true }
)

function normalizeType(value: unknown): LibraryType {
    return value === 'show' || value === 'movie' ? value : 'all'
}

/// Query writes serialize through one chain: two filters changing in the
/// same tick would otherwise each spread a stale route.query and drop the
/// other's key. Reading route.query at execution time (after the previous
/// replace settled) keeps every key.
let queryWrite: Promise<unknown> = Promise.resolve()
function syncQuery(patch: Record<string, string | undefined>): void {
    queryWrite = queryWrite.then(() =>
        router.replace({ query: { ...route.query, ...patch } })
    ).catch(() => undefined)
}

watch(type, () => {
    pagination.pageNumber = 1
    syncQuery({ type: type.value === 'all' ? undefined : type.value })
})

// state/target filters follow the type precedent: written into the query on
// change (router.replace keeps history clean), dropped when back to default
watch(stateFilter, () => {
    syncQuery({ state: stateFilter.value || undefined })
})
watch(targetFilter, () => {
    syncQuery({ target: targetFilter.value || undefined })
})

// Filter changes return to the first page: page 3 of the previous list is
// usually past the end of the narrowed one, which used to render as an
// empty matrix. When the reset actually changes the page number, this
// watcher re-fires and the fetch below then runs once with page 1 — only
// the page number changing on its own (pagination controls) fetches
// directly. The current page also lands in the query so a history back
// from a detail page restores the exact page the user left.
watch(
    [type, stateFilter, targetFilter, () => pagination.pageNumber],
    ([newType, newState, newTarget], [oldType, oldState, oldTarget]) => {
        const filterChanged =
            newType !== oldType || newState !== oldState || newTarget !== oldTarget
        if (filterChanged && pagination.pageNumber !== 1) {
            pagination.pageNumber = 1
            return
        }
        syncQuery({ page: pagination.pageNumber > 1 ? String(pagination.pageNumber) : undefined })
        void fetchLibrary()
    }
)

/// v-model would reassign the whole filter object, which both warns on the
/// const reactive binding and drops the watch's reactivity — write the page
/// back in place instead.
function onPaginationUpdate(value: IFilter): void {
    Object.assign(pagination, value)
}

let searchTimer: ReturnType<typeof setTimeout> | undefined
watch(search, () => {
    clearTimeout(searchTimer)
    searchTimer = setTimeout(() => {
        pagination.pageNumber = 1
        // the debounced input also lands in the query, so a back-and-forth
        // restores the typed keyword together with the other filters
        syncQuery({ q: search.value || undefined })
        void fetchLibrary()
    }, 300)
})

async function fetchLibrary(): Promise<void> {
    loading.value = true
    try {
        const page = await mediaApi.library<IPagedResult<IMediaLibraryItem>>({
            pageNumber: pagination.pageNumber,
            searchQuery: search.value,
            sortBy: 'Title',
            ascending: true,
            mediaType: type.value === 'all' ? null : MEDIA_TYPE[type.value === 'show' ? 'SHOW' : 'MOVIE'],
            state: stateFilter.value || null,
            targetLanguage: targetFilter.value || null
        })
        items.value = page.items
        totalCount.value = page.totalCount
        uiStore.setSubOverride({
            key: 'topbar.sub.libraryLive',
            params: { count: totalCount.value }
        })
        pruneSelection()
    } finally {
        loading.value = false
        // restore the persisted scroll offset once, after the initial page
        // of rows rendered — later fetches (paging/filtering) must not yank
        // the user's scroll position back
        if (scrollRestorePending) {
            scrollRestorePending = false
            await nextTick()
            restoreListScroll()
        }
    }
}

// Topbar CTA (prototype): 翻译所选 opens the tray's language menu —
// selecting nothing gets a hint instead.
onMounted(() => {
    uiStore.registerCta({ key: 'topbar.cta.translateSelected' }, () => {
        if (selection.size === 0) {
            toastTip()
            return
        }
        trayMenu.value?.open()
    })
})

onUnmounted(() => {
    uiStore.clearCta()
    uiStore.setSubOverride('')
    // final save on route leave (every scroll already saved; this covers a
    // leave without a trailing scroll event)
    if (listScrollRef.value) {
        sessionStorage.setItem(LIST_SCROLL_KEY, String(listScrollRef.value.scrollTop))
    }
})

// ⌘K from anywhere: focus this screen's search box
watch(
    () => uiStore.searchFocusTick,
    () => {
        if (typeof document !== 'undefined') {
            document.querySelector<HTMLInputElement>('main input[type="search"]')?.focus()
        }
    }
)

onMounted(async () => {
    void fetchLibrary()
    // toolbar stats: pipeline missing count + live in-progress count
    const [pipeline, counts] = await Promise.allSettled([
        statisticsApi.getPipelineStatistics(),
        translationRequestApi.statusCounts()
    ])
    if (pipeline.status === 'fulfilled') missingCount.value = pipeline.value.missingSubtitles
    if (counts.status === 'fulfilled') inProgressCount.value = counts.value['InProgress'] ?? 0
})

// ── selection + tray estimate ──────────────────────────────────
const allVisibleSelected = computed(
    () => items.value.length > 0 && items.value.every((item) => selection.has(rowKey(item)))
)

function rowKey(item: IMediaLibraryItem): string {
    return item.mediaType + ':' + item.id
}

function isSelected(item: IMediaLibraryItem): boolean {
    return selection.has(rowKey(item))
}

function toggleSelect(item: IMediaLibraryItem): void {
    const key = rowKey(item)
    if (selection.has(key)) {
        selection.delete(key)
    } else {
        selection.set(key, item)
    }
    scheduleEstimate()
}

function toggleSelectAll(): void {
    if (allVisibleSelected.value) {
        items.value.forEach((item) => selection.delete(rowKey(item)))
    } else {
        items.value.forEach((item) => selection.set(rowKey(item), item))
    }
    scheduleEstimate()
}

function clearSelection(): void {
    selection.clear()
    estimate.value = null
}

/// drop selections of the media type the current tab no longer shows
function pruneSelection(): void {
    if (type.value === 'all') return
    const keep = type.value === 'show' ? 'Show' : 'Movie'
    for (const item of selection.values()) {
        if (item.mediaType !== keep) {
            selection.delete(item.mediaType + ':' + item.id)
        }
    }
    scheduleEstimate()
}

let estimateTimer: ReturnType<typeof setTimeout> | undefined
function scheduleEstimate(): void {
    clearTimeout(estimateTimer)
    if (selection.size === 0) {
        estimate.value = null
        return
    }
    estimateTimer = setTimeout(refreshEstimate, 400)
}

async function refreshEstimate(): Promise<void> {
    if (selection.size === 0 || !targetLanguages.value[0]) return
    try {
        estimate.value = await mediaApi.estimate<ITranslationEstimate>(
            [...selection.values()].map((item) => ({
                mediaType: item.mediaType,
                id: item.id
            })),
            targetLanguages.value[0].code
        )
    } catch {
        estimate.value = null
    }
}

async function translateSelection(language: ILanguage): Promise<void> {
    const showIds = [...selection.values()].filter((i) => i.mediaType === 'Show').map((i) => i.id)
    const movieIds = [...selection.values()].filter((i) => i.mediaType === 'Movie').map((i) => i.id)
    // R11: bulk used to end with no feedback at all — skips (no path / no
    // source subtitle) were server-side logs only. Both calls are accumulated
    // first so the user gets one created count and one skip count, not two
    // competing toasts per media type.
    let created = 0
    const skipped: IBulkTranslateSummary['skipped'] = []
    if (showIds.length > 0) {
        const summary = await translateStore.bulkTranslate(showIds, language.code, MEDIA_TYPE.SHOW)
        created += summary.created
        skipped.push(...summary.skipped)
    }
    if (movieIds.length > 0) {
        const summary = await translateStore.bulkTranslate(movieIds, language.code, MEDIA_TYPE.MOVIE)
        created += summary.created
        skipped.push(...summary.skipped)
    }
    toastStore.push({ message: t('library.bulk.created', { count: created }), type: 'success' })
    if (skipped.length > 0) {
        toastStore.push({ message: t('library.bulk.skipped', { count: skipped.length }), type: 'info' })
    }
    clearSelection()
}

// ── rows ───────────────────────────────────────────────────────
const thClass =
    'text-secondary-content border-line whitespace-nowrap border-b px-3 py-2 text-left text-xs font-medium'
const tdClass = 'border-line/50 align-middle border-b px-3 py-2'

const visibleTrackCount = VISIBLE_TRACKS

function visibleTracks(item: IMediaLibraryItem): ISubtitleTrackSummary[] {
    return item.tracks.slice(0, VISIBLE_TRACKS)
}

function trackLabel(track: ISubtitleTrackSummary): string {
    const lang = track.language.toUpperCase()
    if (track.state === 'complete') return `${lang} ✓`
    if (track.state === 'partial') return `${lang} ◐ ${track.have}/${track.total}`
    return `${lang} ·`
}

/// 音轨列文本:归一化码大写、「·」点隔("zh" → "ZH · EN"),与详情页
/// 音轨行的 join(' · ') 同一形态
function audioCellText(item: IMediaLibraryItem): string {
    return (item.audioLanguages ?? [])
        .map((code) => code.toUpperCase())
        .join(' · ')
}

function trackTitle(track: ISubtitleTrackSummary): string {
    if (track.state === 'complete') return t('library.track.complete', { lang: track.language })
    if (track.state === 'partial') return t('library.track.partial', { lang: track.language, have: track.have, total: track.total })
    return t('library.track.none', { lang: track.language })
}

/// prototype chip vocabulary: ok = full track, part = partial, dim = absent
function chipClass(state: string): string {
    const base =
        'mono bg-tertiary border-line text-secondary-content inline-flex items-center gap-1 whitespace-nowrap rounded border px-1.5 py-px text-[11px]'
    if (state === 'complete') return base + ' text-success border-success/40'
    if (state === 'partial') return base + ' text-accent border-accent/45'
    return base + ' opacity-55'
}

function stateClass(item: IMediaLibraryItem): string {
    switch (item.state) {
        case MEDIA_TRANSLATION_STATE.IN_PROGRESS:
            return 'text-accent text-xs'
        case MEDIA_TRANSLATION_STATE.COMPLETED:
            return 'text-success text-xs'
        case MEDIA_TRANSLATION_STATE.FAILED:
            return 'text-danger text-xs'
        case MEDIA_TRANSLATION_STATE.QUEUED:
            return 'text-info text-xs'
        default:
            return 'text-secondary-content/60 text-xs'
    }
}

function stateLabel(item: IMediaLibraryItem): string {
    switch (item.state) {
        case MEDIA_TRANSLATION_STATE.IN_PROGRESS:
            return t('library.state.inProgress')
        case MEDIA_TRANSLATION_STATE.QUEUED:
            return t('library.state.queued')
        case MEDIA_TRANSLATION_STATE.COMPLETED:
            return t('library.state.completedRatio', { done: item.completedCount, total: item.episodeCount })
        case MEDIA_TRANSLATION_STATE.FAILED:
            return item.failedCount > 0
                ? t('library.state.failedCount', { count: item.failedCount })
                : t('library.state.failed')
        default:
            return t('library.state.none')
    }
}


function detailRoute(item: IMediaLibraryItem): { name: string; params: { id: number } } {
    return {
        name: item.mediaType === 'Show' ? 'media-detail-show' : 'media-detail-movie',
        params: { id: item.id }
    }
}

function goDetail(item: IMediaLibraryItem): void {
    router.push(detailRoute(item))
}

/// Requirement card C — the title text doubles as a detail link. The href
/// is resolved through the router so it stays in sync with goDetail's named
/// routes (right-click → open in new tab works natively). Plain left clicks
/// are intercepted for SPA navigation; modified clicks (cmd/ctrl/shift/alt)
/// fall through to the browser so they keep their native meaning.
function detailHref(item: IMediaLibraryItem): string {
    return router.resolve(detailRoute(item)).href
}

function onTitleClick(item: IMediaLibraryItem, event: MouseEvent): void {
    if (event.defaultPrevented || event.button !== 0) return
    if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return
    event.preventDefault()
    goDetail(item)
}

function goImport(item: IMediaLibraryItem): void {
    router.push({
        name: 'import',
        query: item.mediaType === 'Show' ? { tab: 'season', show: item.id } : {}
    })
}



// ── formatting ─────────────────────────────────────────────────
/// Library B2 metadata chips: year · ★rating · quality · size — every
/// fragment is bound to a real field, absent fields contribute nothing,
/// and the whole chip stays hidden when the row carries none of them.
function metaChips(item: IMediaLibraryItem): string {
    const parts: string[] = []
    if (item.year != null) parts.push(String(item.year))
    if (item.imdbRating != null) parts.push(`★${Number(item.imdbRating.toFixed(1))}`)
    if (item.quality) parts.push(item.quality)
    if (item.sizeBytes != null && item.sizeBytes > 0) parts.push(formatSize(item.sizeBytes))
    return parts.join(' · ')
}

/// Byte ladder with a GB tier — the matrix chips quote movie files in GB.
function formatSize(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
    if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
    return `${(bytes / (1024 * 1024 * 1024)).toFixed(1)} GB`
}

/// prototype shows activity times as HH:MM in the matrix column
function formatTime(value: string): string {
    return new Date(value).toLocaleTimeString(locale.value, {
        hour: '2-digit',
        minute: '2-digit'
    })
}

function formatCost(value: number): string {
    return new Intl.NumberFormat(locale.value, {
        style: 'currency',
        currency: 'USD',
        maximumFractionDigits: 2
    }).format(value)
}

function formatNumber(value: number): string {
    return new Intl.NumberFormat(locale.value).format(value)
}
</script>

<style scoped>
.badge {
    font-size: 11px;
    border: 1px solid var(--line);
    border-radius: 3px;
    padding: 0 6px;
    color: var(--secondary-content);
    white-space: nowrap;
    flex: none;
}

.badge-show {
    color: var(--info);
    border-color: color-mix(in srgb, var(--info) 40%, var(--line));
}
</style>
