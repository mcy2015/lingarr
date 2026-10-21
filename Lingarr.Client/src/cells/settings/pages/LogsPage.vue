<template>
    <!-- ═══ 日志下钻屏(原型 screen-11):pb-head(级别过滤 chips + 文本搜索 +
         自动滚动 switch + 清空次级动作)+ 日志卡(滚动收进卡内)。L5:搜索
         覆盖消息/类别/关联键;条目按 seq 去重、上限 5000;堆栈默认折叠;
         纯文本渲染(v-html 与彩色标记一并退役)。 -->
    <DrillDownPage
        :back-to="{ name: 'settings-system' }"
        :back-label="t('settings.menu.groups.system')"
        :title="t('settings.logs.heading')"
        :sub="t('settings.logs.subLine')">
        <template #actions>
            <div
                class="border-line bg-secondary inline-flex overflow-hidden rounded-md border"
                role="group"
                :aria-label="t('settings.logs.levelFilter')">
                <button
                    v-for="chip in levelChips"
                    :key="chip.value"
                    type="button"
                    class="mono cursor-pointer border-r border-line px-2 py-1 text-[11px] transition-colors last:border-r-0"
                    :class="
                        filterOptions.logLevel === chip.value
                            ? 'bg-accent text-accent-content'
                            : 'text-secondary-content hover:bg-tertiary hover:text-primary-content'
                    "
                    :aria-pressed="filterOptions.logLevel === chip.value"
                    :data-test="`log-chip-${chip.value}`"
                    @click="filterOptions.logLevel = chip.value">
                    {{ chip.label }} {{ chip.count }}
                </button>
            </div>
            <input
                v-model="searchQuery"
                type="search"
                data-test="log-search"
                class="border-line bg-secondary text-primary-content placeholder:text-secondary-content/50 focus-visible:ring-accent h-[26px] w-52 rounded-md border px-2 text-xs transition-colors focus-visible:ring-2 focus-visible:outline-none"
                :placeholder="t('settings.logs.searchPlaceholder')"
                :aria-label="t('settings.logs.searchPlaceholder')" />
            <label
                class="text-secondary-content ml-1 flex cursor-pointer items-center gap-2 text-[12.5px]">
                {{ t('settings.logs.autoScrollLabel') }}
                <ToggleButton v-model="autoScroll" size="small" />
            </label>
            <button
                type="button"
                class="border-line text-primary-content hover:bg-tertiary focus-visible:ring-accent cursor-pointer rounded-md border px-2.5 py-1 text-xs font-medium transition-colors focus-visible:ring-2 focus-visible:outline-none"
                data-test="log-copy"
                @click="copyLogs">
                {{ t('settings.logs.copy') }}
            </button>
            <button
                type="button"
                class="border-line text-primary-content hover:bg-tertiary focus-visible:ring-accent cursor-pointer rounded-md border px-2.5 py-1 text-xs font-medium transition-colors focus-visible:ring-2 focus-visible:outline-none"
                data-test="log-clear"
                @click="clearLogs">
                {{ t('settings.logs.clear') }}
            </button>
        </template>

        <div
            class="border-line bg-secondary flex min-h-0 flex-1 flex-col overflow-hidden rounded-md border">
            <div
                ref="logContainer"
                class="shell-scroll mono min-h-0 flex-1 overflow-y-auto text-[11.5px] leading-relaxed"
                data-test="log-list"
                @scroll="onContainerScroll">
                <div
                    v-if="filteredLogs.length === 0"
                    class="text-secondary-content/60 flex h-full items-center justify-center p-6 text-center">
                    {{ t('settings.logs.waiting') }}
                </div>

                <!-- Log entries: three mono columns (time / level / message),
                     single-line ellipsis on the message; warn/err rows tint
                     the whole message cell (prototype .warnrow/.errrow). -->
                <div v-for="(log, index) in filteredLogs" :key="index" class="log-entry">
                    <div
                        class="hover:bg-tertiary/50 grid grid-cols-[70px_46px_1fr] items-baseline gap-x-2 border-b border-line/60 py-1 pr-3 pl-2 transition-colors"
                        :data-lv="levelKey(log.logLevel)">
                        <time class="text-secondary-content/60 shrink-0 tabular-nums">
                            {{ formatTime(log.timestamp) }}
                        </time>
                        <span class="shrink-0 font-semibold" :class="levelClass(log.logLevel)">
                            {{ levelShort(log.logLevel) }}
                        </span>
                        <span
                            class="min-w-0 truncate"
                            :class="rowClass(log.logLevel)"
                            :title="log.message">
                            <span
                                v-if="correlationOf(log)"
                                class="text-secondary-content/60 mr-1.5"
                                data-test="log-correlation">
                                {{ correlationOf(log) }}
                            </span>
                            <span
                                v-if="log.formattedSource"
                                class="text-secondary-content/70 mr-1.5">
                                {{ log.formattedSource }}
                            </span>
                            <span>{{ log.message }}</span>
                        </span>
                    </div>

                    <!-- L5: the stack rides its own field (L1b) collapsed by
                         default — one click to expand, no page-level state. -->
                    <details
                        v-if="log.stackTrace"
                        class="border-line/60 bg-danger/5 border-b py-1 pr-3 pl-2 text-xs text-danger/90"
                        data-test="log-stack">
                        <summary class="cursor-pointer select-none opacity-80">
                            {{ t('settings.logs.stackTraceToggle') }}
                        </summary>
                        <pre class="mt-1 whitespace-pre-wrap">{{ log.stackTrace }}</pre>
                    </details>
                </div>
            </div>
        </div>
    </DrillDownPage>
</template>

<script setup lang="ts">
import { ref, onMounted, onUnmounted, nextTick, computed, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ILogEntry, IFilterOptions } from '@/types'
import { logsService } from '@/cells/settings/services/logsService'
import { formatDate, formatTime } from '@/cells/app/utils/date'
import { writeClipboard } from '@/cells/app/utils/clipboard'
import DrillDownPage from '@/cells/app/components/DrillDownPage.vue'
import ToggleButton from '@/cells/kernel/components/ToggleButton.vue'
import { useUiStore } from '@/cells/app/stores/ui'
import { useToastStore } from '@/cells/app/stores/toast'

const logsApi = logsService()

const { t } = useI18n()
const uiStore = useUiStore()
const toastStore = useToastStore()
const logs = ref<ILogEntry[]>([])
const autoScroll = ref(true)
const logContainer = ref<HTMLElement | null>(null)
const searchQuery = ref('')
const filterOptions = ref<IFilterOptions>({
    logLevel: 'all'
})
let eventSource: EventSource | null = null

/// L5 hard cap: the live stream is unbounded server-side; without a cap a
/// tab left open through a 10w-media round grows the array (and the DOM)
/// without end. Oldest entries drop off.
const MaxEntries = 5000

/// L1b sequence high-water mark — the server replays the last 100 entries
/// on reconnect, anything at or below this is a duplicate.
let lastSeq = 0

const levelKey = (level: string): string => {
    const lower = level.toLowerCase()
    if (lower.startsWith('err')) return 'error'
    if (lower.startsWith('warn')) return 'warning'
    if (lower.startsWith('inf')) return 'information'
    return lower
}

const levelCount = (level: string): number =>
    logs.value.filter((log) => levelKey(log.logLevel) === level).length

/// Level chips (prototype .chips): counts are computed client-side from the
/// streamed entries; entries outside the three buckets only count in "all".
const levelChips = computed(() => [
    {
        value: 'all',
        label: t('settings.logs.chipAll'),
        count: logs.value.length
    },
    { value: 'information', label: 'INFO', count: levelCount('information') },
    { value: 'warning', label: 'WARN', count: levelCount('warning') },
    { value: 'error', label: 'ERROR', count: levelCount('error') }
])

const filteredLogs = computed(() => {
    const query = searchQuery.value.trim().toLowerCase()
    return logs.value.filter((log) => {
        if (filterOptions.value.logLevel !== 'all') {
            if (levelKey(log.logLevel) !== filterOptions.value.logLevel) {
                return false
            }
        }
        if (query) {
            const haystack =
                `${log.message} ${log.category} ${log.formattedSource} ${correlationOf(log)}`.toLowerCase()
            if (!haystack.includes(query)) {
                return false
            }
        }
        return true
    })
})

/// L2 correlation keys as one compact muted prefix — visible and
/// searchable (paste a requestId/jobId to pull the whole chain).
const correlationOf = (log: ILogEntry): string => {
    const parts: string[] = []
    if (log.jobId) parts.push(`job:${log.jobId}`)
    if (log.requestId) parts.push(`req:${log.requestId}`)
    if (log.searchSessionId != null) parts.push(`sess:${log.searchSessionId}`)
    if (log.mediaKey) parts.push(`media:${log.mediaKey}`)
    if (log.provider) parts.push(`src:${log.provider}`)
    return parts.join(' ')
}

/// 行内级别与筛选徽章同一套写法(批E E5):INFO/WARN/ERROR,等宽网格
/// 的 46px 级别列足够容纳最长形态,不换行。
const levelShort = (level: string): string => {
    const key = levelKey(level)
    if (key === 'error') return 'ERROR'
    if (key === 'warning') return 'WARN'
    if (key === 'information') return 'INFO'
    return level.slice(0, 5).toUpperCase()
}

const levelClass = (level: string): string => {
    const key = levelKey(level)
    if (key === 'error') return 'text-danger'
    if (key === 'warning') return 'text-warning'
    if (key === 'information') return 'text-info'
    return 'text-secondary-content'
}

const rowClass = (level: string): string => {
    const key = levelKey(level)
    if (key === 'error') return 'text-danger'
    if (key === 'warning') return 'text-warning'
    return 'text-primary-content'
}

const scrollToBottom = async () => {
    if (autoScroll.value && logContainer.value) {
        await nextTick()
        logContainer.value.scrollTop = logContainer.value.scrollHeight
    }
}

/// Prototype screen-11: while auto-scroll is on, the view follows new
/// entries; scrolling up to read pauses the follow (the switch reflects it).
const onContainerScroll = () => {
    if (!autoScroll.value || !logContainer.value) return
    const distanceFromBottom =
        logContainer.value.scrollHeight -
        logContainer.value.scrollTop -
        logContainer.value.clientHeight
    if (distanceFromBottom > 40) {
        autoScroll.value = false
    }
}

const clearLogs = () => {
    logs.value = []
}

/// Export and copy share the same text (frozen semantics: they act on what
/// is on screen under the current level filter, not the hidden rows).
const buildLogText = (): string => {
    let content = `${t('settings.logs.exportTitle')}\n`
    content += `${t('settings.logs.exportGenerated', { datetime: new Date().toLocaleString() })}\n`
    content += `${t('settings.logs.exportTotalEntries', { count: filteredLogs.value.length })}\n`
    content += `${'='.repeat(80)}\n\n`

    filteredLogs.value.forEach((log) => {
        content += `[${formatDate(log.timestamp)} ${formatTime(log.timestamp)}] [${log.logLevel}] [${log.category}] ${log.message}\n`

        // Include stack trace
        if (log.stackTrace) {
            content += `${t('settings.logs.exportStackTrace')}\n${log.stackTrace}\n`
        }

        content += `\n`
    })

    return content
}

const exportLogs = () => {
    const timestamp = new Date().toISOString().replace(/[:.]/g, '-')
    const filename = `system-logs-${timestamp}.txt`

    const blob = new Blob([buildLogText()], { type: 'text/plain' })
    const url = window.URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = filename
    document.body.appendChild(link)
    link.click()
    document.body.removeChild(link)
    window.URL.revokeObjectURL(url)
}

const copyLogs = async () => {
    try {
        await writeClipboard(buildLogText())
        toastStore.push({ message: t('settings.logs.copied'), type: 'success' })
    } catch {
        toastStore.push({ message: t('settings.logs.copyFailed'), type: 'error' })
    }
}

watch(
    filterOptions,
    () => {
        scrollToBottom()
    },
    { deep: true }
)

/// Single append path: the MaxEntries hard cap applies on every entry.
const appendLog = (entry: ILogEntry) => {
    logs.value.push(entry)
    if (logs.value.length > MaxEntries) {
        logs.value.splice(0, logs.value.length - MaxEntries)
    }
}

/// Creates the SSE connection with its handlers attached — the one entry
/// point the first mount and every manual reconnect both go through (the
/// old inline reconnect never re-attached handlers, so the page went
/// silent after one dropped connection).
const connectStream = () => {
    eventSource = logsApi.getStream()

    eventSource.onmessage = (event) => {
        try {
            const logData = JSON.parse(event.data) as ILogEntry
            // L1b: seq is the reconnect dedupe key — the server replays the
            // last 100 entries after a drop; anything at or below the last
            // seen seq is a duplicate.
            if (typeof logData.seq === 'number') {
                if (logData.seq <= lastSeq) {
                    return
                }
                lastSeq = logData.seq
            }
            appendLog(logData)
            scrollToBottom()
        } catch (error) {
            console.error('Error processing log entry:', error)
            console.error('Problematic data:', event.data)

            appendLog({
                logLevel: 'Error',
                message: t('settings.logs.failedToProcessData', {
                    data:
                        typeof event.data === 'string'
                            ? event.data.substring(0, 100) + '...'
                            : 'Invalid format'
                }),
                timestamp: new Date().toISOString(),
                formattedSource: 'System',
                category: 'System',
                stackTrace: error instanceof Error ? error.stack : undefined
            })
        }
    }

    eventSource.onerror = () => {
        appendLog({
            logLevel: 'error',
            message: t('settings.logs.connectionError'),
            timestamp: new Date().toISOString(),
            formattedSource: 'System',
            category: 'System'
        })
        // Native EventSource retries transient drops itself (the server
        // resumes from Last-Event-ID; overlap is deduped via lastSeq
        // above). Only a terminally closed source needs the manual rebuild.
        if (eventSource && eventSource.readyState === EventSource.CLOSED) {
            eventSource.close()
            setTimeout(connectStream, 5000)
        }
    }
}

onMounted(() => {
    // topbar CTA (prototype act table): ⇩ export of the filtered view
    uiStore.registerCta({ key: 'settings.logs.exportCta' }, exportLogs)
    uiStore.setSubOverride({ key: 'settings.logs.topbarSub' })

    connectStream()
})

onUnmounted(() => {
    uiStore.clearCta()
    uiStore.setSubOverride('')
    if (eventSource) {
        eventSource.close()
        eventSource = null
    }
})
</script>
