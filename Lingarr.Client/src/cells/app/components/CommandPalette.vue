<template>
    <!-- ═══ ⌘K 全局搜索(原型 #cmdk,global-interactions §3):遮罩 + 560px
         盒 + mhead(搜索图标 + 输入 + ESC 键帽)+ mres 分组行。三组结果 —
         媒体(mediaService 搜索,前 4)/ 任务(translationRequest 搜索,前 3)/
         设置(静态组列表,中英文关键词过滤)。选中跳转并关闭模态。 -->
    <Teleport to="body">
        <Transition
            enter-active-class="transition-opacity duration-150 ease-in-out"
            leave-active-class="transition-opacity duration-150 ease-in-out"
            enter-from-class="opacity-0"
            leave-to-class="opacity-0">
            <div
                v-if="isOpen"
                class="fixed inset-0 z-[60] flex items-start justify-center bg-black/55 pt-[12vh]"
                data-test="cmdk-overlay"
                @click.self="close">
                <div
                    class="bg-secondary border-line w-[min(560px,92vw)] overflow-hidden rounded-lg border shadow-[0_18px_48px_rgba(0,0,0,0.45)]"
                    role="dialog"
                    aria-modal="true"
                    :aria-label="t('cmdk.ariaLabel')"
                    data-test="cmdk-box">
                    <div class="border-line flex items-center gap-2 border-b px-3.5 py-2.5">
                        <Icon name="search" class="h-4 w-4 shrink-0" aria-hidden="true" />
                        <input
                            ref="inputEl"
                            v-model="query"
                            type="text"
                            class="text-primary-content placeholder:text-secondary-content/60 min-w-0 flex-1 border-0 bg-transparent text-sm outline-none"
                            :placeholder="t('cmdk.placeholder')"
                            :aria-label="t('cmdk.ariaLabel')"
                            data-test="cmdk-input"
                            @keydown.down.prevent="focusNext"
                            @keydown.up.prevent="focusPrev"
                            @keydown.enter.prevent="activateSelected" />
                        <span
                            class="mono text-secondary-content flex-none rounded border border-line px-1.5 py-px text-[11px]">
                            ESC
                        </span>
                    </div>
                    <div
                        class="shell-scroll max-h-[46vh] overflow-y-auto py-1.5"
                        data-test="cmdk-res">
                        <template v-for="group in visibleGroups" :key="group.key">
                            <div class="text-secondary-content px-3.5 pt-2 pb-1 text-[11px] font-semibold">
                                {{ group.label }}
                            </div>
                            <button
                                v-for="item in group.items"
                                :key="group.key + item.key"
                                type="button"
                                class="text-primary-content hover:bg-tertiary flex w-full cursor-pointer items-center gap-2.5 px-3.5 py-2 text-left text-[13px]"
                                :data-test="`cmdk-item-${item.key}`"
                                @click="select(item)">
                                    <span class="min-w-0 flex-1 truncate">
                                        {{ item.title }}
                                    </span>
                                    <span
                                        class="mono text-secondary-content max-w-[45%] flex-none truncate text-[11px] whitespace-nowrap">
                                        {{ item.sub }}
                                    </span>
                                </button>
                        </template>
                        <p
                            v-if="visibleGroups.length === 0 && !loading"
                            class="text-secondary-content/60 px-3.5 py-6 text-center text-sm"
                            data-test="cmdk-empty">
                            {{ t('cmdk.noResults') }}
                        </p>
                    </div>
                </div>
            </div>
        </Transition>
    </Teleport>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter, type RouteLocationRaw } from 'vue-router'
import axios from 'axios'
import Icon from '@/cells/kernel/components/icons/Icon.vue'
import { mediaService } from '@/cells/app/api/mediaService'
import { translationRequestService } from '@/cells/app/api/translationRequestService'
import {
    IPagedResult,
    IMediaLibraryItem,
    ITranslationRequest,
    MEDIA_TYPE
} from '@/types'

/// One selectable row of any group.
interface PaletteItem {
    key: string
    title: string
    sub: string
    route: RouteLocationRaw
}

const SEARCH_DEBOUNCE_MS = 300
const MEDIA_LIMIT = 4
const TASK_LIMIT = 3

const { t } = useI18n()
const router = useRouter()
const mediaApi = mediaService(axios)
const translationRequestApi = translationRequestService(axios)

const isOpen = ref(false)
const query = ref('')
const loading = ref(false)
const inputEl = ref<HTMLInputElement | null>(null)
const mediaItems = ref<IMediaLibraryItem[]>([])
const taskItems = ref<ITranslationRequest[]>([])

// ── 设置组:静态列表,中英文关键词过滤(原型 data-k 语义) ─────────
interface SettingsEntry {
    key: string
    title: string
    keywords: string
    route: RouteLocationRaw
}

const settingsEntries = computed<SettingsEntry[]>(() => [
    {
        key: 'settings-providers',
        title: t('settings.menu.groups.providers'),
        keywords: 'provider service fallback 翻译服务 回退 顺序 translation',
        route: { name: 'settings-providers' }
    },
    {
        key: 'settings-sources',
        title: t('settings.menu.groups.sources'),
        keywords: 'media automation sonarr radarr source 媒体 自动化 来源',
        route: { name: 'settings-sources' }
    },
    {
        key: 'settings-quality',
        title: t('settings.menu.groups.quality'),
        keywords: 'subtitle quality glossary language policy 字幕 质量 术语 语言策略',
        route: { name: 'settings-quality' }
    },
    {
        key: 'settings-system',
        title: t('settings.menu.groups.system'),
        keywords: 'system 系统 面板',
        route: { name: 'settings-system' }
    },
    {
        key: 'logs',
        title: t('settings.logs.heading'),
        keywords: 'logs log 日志 sse',
        route: { name: 'logs' }
    },
    {
        key: 'schedule',
        title: t('settings.schedule.heading'),
        keywords: 'schedule hangfire cron 定时任务 调度',
        route: { name: 'schedule' }
    }
])

const normalizedQuery = computed(() => query.value.trim().toLowerCase())

const filteredSettings = computed<SettingsEntry[]>(() => {
    const q = normalizedQuery.value
    if (!q) {
        return settingsEntries.value
    }
    return settingsEntries.value.filter((entry) =>
        `${entry.keywords} ${entry.title}`.toLowerCase().includes(q)
    )
})

const mediaStateLabel = (state: string): string => {
    const camel = state.charAt(0).toLowerCase() + state.slice(1)
    const key = `library.state.${camel}`
    const label = t(key)
    // a missing mapping returns the key itself — fall back to the raw state
    return label === key ? state : label
}

const mediaGroup = computed<PaletteItem[]>(() =>
    mediaItems.value.slice(0, MEDIA_LIMIT).map((item) => ({
        key: `media-${item.mediaType}-${item.id}`,
        title: item.title,
        sub: `${item.mediaType === MEDIA_TYPE.SHOW ? t('library.tabs.shows') : t('library.tabs.movies')} · ${mediaStateLabel(item.state)}`,
        route:
            item.mediaType === MEDIA_TYPE.SHOW
                ? { name: 'media-detail-show', params: { id: item.id } }
                : { name: 'media-detail-movie', params: { id: item.id } }
    }))
)

const taskGroup = computed<PaletteItem[]>(() =>
    taskItems.value.slice(0, TASK_LIMIT).map((item) => ({
        key: `task-${item.id}`,
        title: `#${item.id} · ${item.title}`,
        sub:
            item.progress > 0
                ? `${statusLabel(item.status)} · ${Math.round(item.progress)}%`
                : statusLabel(item.status),
        route: { name: 'queue-detail', params: { id: item.id } }
    }))
)

const settingsGroup = computed<PaletteItem[]>(() =>
    filteredSettings.value.map((entry) => ({
        // entry keys already carry the settings- prefix for the four panel
        // routes; logs/schedule are top-level drills — normalize both
        key: `settings-${entry.key.replace(/^settings-/, '')}`,
        title: entry.title,
        sub: '',
        route: entry.route
    }))
)

/// Groups with no visible items hide their header entirely (prototype
/// filterK group-hiding rule).
const visibleGroups = computed(() =>
    [
        { key: 'media', label: t('cmdk.group.media'), items: mediaGroup.value },
        { key: 'tasks', label: t('cmdk.group.tasks'), items: taskGroup.value },
        { key: 'settings', label: t('cmdk.group.settings'), items: settingsGroup.value }
    ].filter((group) => group.items.length > 0)
)

const allItems = computed<PaletteItem[]>(() =>
    visibleGroups.value.flatMap((group) => group.items)
)
const selectedIndex = ref(0)

function statusLabel(status: string): string {
    const camel = status.charAt(0).toLowerCase() + status.slice(1)
    return t(`common.translationStatus.${camel}`)
}

// ── 拉取:300ms debounce,媒体/任务走服务端搜索 ───────────────────
let fetchHandle: ReturnType<typeof setTimeout> | undefined
const runFetch = async (search: string): Promise<void> => {
    loading.value = true
    const [mediaResult, taskResult] = await Promise.allSettled([
        mediaApi.library<IPagedResult<IMediaLibraryItem>>({
            pageNumber: 1,
            searchQuery: search,
            sortBy: 'Title',
            ascending: true
        }),
        translationRequestApi.requests<IPagedResult<ITranslationRequest>>(
            1,
            search,
            'CreatedAt',
            false
        )
    ])
    if (mediaResult.status === 'fulfilled') {
        mediaItems.value = mediaResult.value.items
    }
    if (taskResult.status === 'fulfilled') {
        taskItems.value = taskResult.value.items
    }
    loading.value = false
    selectedIndex.value = 0
}
const scheduleFetch = (): void => {
    if (fetchHandle) {
        clearTimeout(fetchHandle)
    }
    fetchHandle = setTimeout(() => {
        void runFetch(normalizedQuery.value)
    }, SEARCH_DEBOUNCE_MS)
}

// ── 开关:⌘K 切换 / ESC·遮罩·选中后关闭 ─────────────────────────
function open(): void {
    isOpen.value = true
    query.value = ''
    mediaItems.value = []
    taskItems.value = []
    selectedIndex.value = 0
    void nextTick(() => inputEl.value?.focus())
    scheduleFetch()
}

function close(): void {
    isOpen.value = false
}

function toggle(): void {
    if (isOpen.value) {
        close()
    } else {
        open()
    }
}

function select(item: PaletteItem): void {
    close()
    void router.push(item.route)
}

// typing re-runs the server searches on a 300ms debounce
watch(query, scheduleFetch)

// ── 键盘:上下选择 + Enter 激活(结果行本身仍可点击) ─────────────
function focusNext(): void {
    if (allItems.value.length === 0) return
    selectedIndex.value = (selectedIndex.value + 1) % allItems.value.length
    highlightSelected()
}

function focusPrev(): void {
    if (allItems.value.length === 0) return
    selectedIndex.value =
        (selectedIndex.value - 1 + allItems.value.length) % allItems.value.length
    highlightSelected()
}

function activateSelected(): void {
    const item = allItems.value[selectedIndex.value]
    if (item) {
        select(item)
    }
}

function highlightSelected(): void {
    const buttons = document.querySelectorAll<HTMLButtonElement>('[data-test^="cmdk-item-"]')
    buttons[selectedIndex.value]?.scrollIntoView({ block: 'nearest' })
}

const onGlobalKeydown = (event: KeyboardEvent): void => {
    if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault()
        toggle()
        return
    }
    if (event.key === 'Escape' && isOpen.value) {
        event.preventDefault()
        close()
    }
}

onMounted(() => {
    window.addEventListener('keydown', onGlobalKeydown)
})

onUnmounted(() => {
    window.removeEventListener('keydown', onGlobalKeydown)
    if (fetchHandle) {
        clearTimeout(fetchHandle)
    }
})

defineExpose({ open, close, toggle })
</script>
