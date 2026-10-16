<template>
    <!-- ═══ 第 13 屏:字幕下载历史。GET /api/subtitle-search/history 的 keyset
         分页消费方——筛选条(类型/字幕源/结果)+ 表格(时间/媒体标题[链接进详情]/
         源/语言/来源/结果徽章/文件名)+「加载更多」按 nextCursor 续页。
         全部文案走 i18n,全部数字来自真实响应,无静态装饰句。 -->
    <div class="w-full flex h-full min-h-0 flex-col p-4 lg:p-6">
        <div
            class="border-line bg-secondary rounded-md border flex min-h-0 flex-1 flex-col"
            data-test="subtitle-history-card">
            <!-- 卡头:标题 + 已载入行数(keyset 没有总数,只报真实载入数) -->
            <div class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-3">
                <h2 class="text-[13px] font-semibold">{{ t('subHistory.title') }}</h2>
                <span
                    v-if="loaded && items.length > 0"
                    class="mono text-secondary-content ml-auto text-[11px]"
                    data-test="subtitle-history-count">
                    {{ t('subHistory.meta', { count: items.length }) }}
                </span>
            </div>

            <!-- 筛选条:三个下拉任一变化即整体重置回第一页 -->
            <div
                class="border-line flex flex-wrap items-center gap-x-4 gap-y-2 border-b px-4 py-2.5 text-[12.5px]"
                data-test="subtitle-history-filters">
                <label class="flex items-center gap-1.5">
                    <span class="text-secondary-content">{{ t('subHistory.filterMediaType') }}</span>
                    <select
                        v-model="filters.mediaType"
                        class="border-line bg-primary focus-visible:ring-accent cursor-pointer rounded border px-1.5 py-1 text-xs focus-visible:ring-2 focus-visible:outline-none"
                        data-test="sub-history-filter-media-type">
                        <option value="">{{ t('subHistory.filterAll') }}</option>
                        <option value="movie">{{ t('subHistory.filterMovie') }}</option>
                        <option value="episode">{{ t('subHistory.filterEpisode') }}</option>
                    </select>
                </label>
                <label class="flex items-center gap-1.5">
                    <span class="text-secondary-content">{{ t('subHistory.filterProvider') }}</span>
                    <select
                        v-model="filters.provider"
                        class="border-line bg-primary focus-visible:ring-accent cursor-pointer rounded border px-1.5 py-1 text-xs focus-visible:ring-2 focus-visible:outline-none"
                        data-test="sub-history-filter-provider">
                        <option value="">{{ t('subHistory.filterAll') }}</option>
                        <option v-for="provider in providerOptions" :key="provider" :value="provider">
                            {{ provider }}
                        </option>
                    </select>
                </label>
                <label class="flex items-center gap-1.5">
                    <span class="text-secondary-content">{{ t('subHistory.filterResult') }}</span>
                    <select
                        v-model="filters.result"
                        class="border-line bg-primary focus-visible:ring-accent cursor-pointer rounded border px-1.5 py-1 text-xs focus-visible:ring-2 focus-visible:outline-none"
                        data-test="sub-history-filter-result">
                        <option value="">{{ t('subHistory.filterAll') }}</option>
                        <option v-for="option in RESULT_KEYS" :key="option" :value="option">
                            {{ resultLabel(option) }}
                        </option>
                    </select>
                </label>
            </div>

            <!-- 诚实状态行:加载失败(可重试)/加载中/空态 -->
            <div
                v-if="loadFailed && items.length === 0"
                class="flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-3 text-[13px]"
                data-test="subtitle-history-error">
                <span class="text-danger">{{ t('subHistory.loadFailed') }}</span>
                <button
                    type="button"
                    class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                    data-test="subtitle-history-retry"
                    @click="fetchPage">
                    {{ t('subHistory.retry') }}
                </button>
            </div>
            <div
                v-else-if="!loaded"
                class="text-secondary-content px-4 py-3 text-[13px]"
                data-test="subtitle-history-loading">
                {{ t('subHistory.loading') }}
            </div>
            <div
                v-else-if="items.length === 0"
                class="text-secondary-content px-4 py-3 text-[13px]"
                data-test="subtitle-history-empty">
                {{ t('subHistory.empty') }}
            </div>

            <!-- 表格:卡内滚动 -->
            <div v-if="loaded && items.length > 0" class="shell-scroll min-h-0 flex-1 overflow-y-auto">
                <table class="w-full text-left text-[12.5px]">
                    <thead class="text-secondary-content bg-secondary sticky top-0 text-[11px]">
                        <tr>
                            <th class="px-4 py-2 font-medium">{{ t('subHistory.column.time') }}</th>
                            <th class="px-3 py-2 font-medium">{{ t('subHistory.column.media') }}</th>
                            <th class="px-3 py-2 font-medium">{{ t('subHistory.column.provider') }}</th>
                            <th class="px-3 py-2 font-medium">{{ t('subHistory.column.language') }}</th>
                            <th class="px-3 py-2 font-medium">{{ t('subHistory.column.source') }}</th>
                            <th class="px-3 py-2 font-medium">{{ t('subHistory.column.result') }}</th>
                            <th class="px-3 py-2 pr-4 font-medium">{{ t('subHistory.column.fileName') }}</th>
                        </tr>
                    </thead>
                    <tbody>
                        <tr
                            v-for="item in items"
                            :key="item.id"
                            class="border-line border-b last:border-b-0"
                            :data-test="`sub-history-row-${item.id}`">
                            <td class="text-secondary-content px-4 py-2 whitespace-nowrap">
                                <time class="mono text-[11px]">{{ formatDateTime(item.createdAt) }}</time>
                            </td>
                            <!-- 标题即详情链接(库页 detailHref 同款语义);媒体已删除的
                                 孤儿行退化为纯文本,不伪装成链接 -->
                            <td class="max-w-[300px] truncate px-3 py-2">
                                <router-link
                                    v-if="item.mediaTitle"
                                    :to="detailRoute(item)"
                                    class="hover:text-accent"
                                    :data-test="`sub-history-media-${item.id}`"
                                    :title="item.mediaTitle">
                                    {{ item.mediaTitle }}
                                </router-link>
                                <span v-else class="text-secondary-content/60">
                                    {{ t('subHistory.mediaRemoved') }}
                                </span>
                            </td>
                            <td class="px-3 py-2 whitespace-nowrap">{{ item.provider }}</td>
                            <td class="mono px-3 py-2">{{ item.language }}</td>
                            <td class="px-3 py-2 whitespace-nowrap">{{ sourceLabel(item.source) }}</td>
                            <td class="px-3 py-2">
                                <span
                                    class="mono inline-flex items-center rounded border px-1 py-px text-[10px] whitespace-nowrap"
                                    :class="resultClass(item.result)"
                                    :title="item.error ?? resultLabel(item.result)"
                                    :data-test="`sub-history-result-${item.id}`">
                                    {{ resultLabel(item.result) }}
                                </span>
                            </td>
                            <td class="mono max-w-[200px] truncate px-3 py-2 pr-4" :title="item.fileName ?? ''">
                                {{ item.fileName ?? '—' }}
                            </td>
                        </tr>
                    </tbody>
                </table>
            </div>

            <!-- 续页:keyset 没有「第 N 页」,只有下一页游标 -->
            <div
                v-if="nextCursor"
                class="border-line flex items-center justify-center border-t px-4 py-2.5"
                data-test="subtitle-history-footer">
                <button
                    type="button"
                    class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-3 py-1.5 text-xs transition-colors disabled:cursor-not-allowed disabled:opacity-40"
                    :disabled="loadingMore"
                    data-test="sub-history-load-more"
                    @click="loadMore">
                    {{ loadingMore ? t('subHistory.loadingMore') : t('subHistory.loadMore') }}
                </button>
            </div>
        </div>
    </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { subtitleSearchService } from '@/cells/app/api/subtitleSearchService'
import { useToastStore } from '@/cells/app/stores/toast'
import { formatDateTime } from '@/cells/app/utils/date'
import {
    ISubtitleHistoryItem,
    SUBTITLE_HISTORY_RESULT,
    SUBTITLE_HISTORY_SOURCE
} from '@/types'

const PAGE_SIZE = 50

const { t } = useI18n()
const toastStore = useToastStore()

// ── 数据装载 ────────────────────────────────────────────────────
const items = ref<ISubtitleHistoryItem[]>([])
const nextCursor = ref<string | null>(null)
const loaded = ref(false)
const loadingMore = ref(false)
const loadFailed = ref(false)

// ── 筛选(任一变化即重置回第一页)──────────────────────────────
const filters = reactive({ mediaType: '', provider: '', result: '' })

/// 字幕源筛选项数据驱动:来自 providers 端点的注册源名单;读取失败时
/// 筛选只保留「全部」,历史表本身不受影响。
const providerOptions = ref<string[]>([])

/// 静态映射表(不经字符串拼接构造 i18n 键):结果/来源枚举名 → 键,
/// 未知枚举值(更新的服务端)原样回显,绝不吐键名。
const RESULT_KEYS: SUBTITLE_HISTORY_RESULT[] = [
    'Succeeded',
    'Failed',
    'LowConfidenceSkipped',
    'AlreadyExistsSkipped'
]

const RESULT_LABELS: Record<string, string> = {
    Succeeded: 'subHistory.result.Succeeded',
    Failed: 'subHistory.result.Failed',
    LowConfidenceSkipped: 'subHistory.result.LowConfidenceSkipped',
    AlreadyExistsSkipped: 'subHistory.result.AlreadyExistsSkipped'
}

const RESULT_CLASSES: Record<string, string> = {
    Succeeded: 'text-success-content border-success/40 bg-success/10',
    Failed: 'text-danger border-danger/40 bg-danger/10',
    LowConfidenceSkipped: 'text-warning-content border-warning/40 bg-warning/10',
    AlreadyExistsSkipped: 'border-line/60 text-secondary-content/70 border-dashed'
}

const SOURCE_LABELS: Record<string, string> = {
    Manual: 'subHistory.source.Manual',
    Auto: 'subHistory.source.Auto',
    Webhook: 'subHistory.source.Webhook'
}

function resultLabel(result: string): string {
    const key = RESULT_LABELS[result]
    return key ? t(key) : result
}

function resultClass(result: string): string {
    return RESULT_CLASSES[result] ?? 'border-line/60 text-secondary-content/70'
}

function sourceLabel(source: SUBTITLE_HISTORY_SOURCE): string {
    const key = SOURCE_LABELS[source]
    return key ? t(key) : source
}

/// 标题链接进媒体详情:episode 行走 show 详情路由(剧集详情以剧为主档)。
function detailRoute(item: ISubtitleHistoryItem): { name: string; params: { id: number } } {
    return {
        name: item.mediaType === 'Episode' ? 'media-detail-show' : 'media-detail-movie',
        params: { id: item.mediaId }
    }
}

// ── 取数:首页(筛选变化后)整体替换,续页追加 ───────────────────
async function fetchPage(): Promise<void> {
    loadFailed.value = false
    try {
        const page = await subtitleSearchService.history({
            limit: PAGE_SIZE,
            mediaType: filters.mediaType || undefined,
            provider: filters.provider || undefined,
            result: filters.result || undefined
        })
        items.value = page.items
        nextCursor.value = page.nextCursor
        loaded.value = true
    } catch {
        // 首屏失败给可重试的错误行;已有数据时的刷新失败不打扰列表
        if (items.value.length === 0) {
            loadFailed.value = true
        } else {
            toastStore.push({ message: t('subHistory.loadFailed'), type: 'error' })
        }
    }
}

async function loadMore(): Promise<void> {
    if (!nextCursor.value || loadingMore.value) return
    loadingMore.value = true
    try {
        const page = await subtitleSearchService.history({
            cursor: nextCursor.value,
            limit: PAGE_SIZE
        })
        items.value = [...items.value, ...page.items]
        nextCursor.value = page.nextCursor
    } catch {
        // 续页失败保留已载入行,游标不动,可再点一次
        toastStore.push({ message: t('subHistory.loadFailed'), type: 'error' })
    } finally {
        loadingMore.value = false
    }
}

async function fetchProviderOptions(): Promise<void> {
    try {
        const providers = await subtitleSearchService.getProviders()
        providerOptions.value = providers.map((source) => source.provider)
    } catch {
        providerOptions.value = []
    }
}

watch(
    filters,
    () => {
        void fetchPage()
    },
    { deep: true }
)

onMounted(() => {
    void fetchProviderOptions()
    void fetchPage()
})
</script>
