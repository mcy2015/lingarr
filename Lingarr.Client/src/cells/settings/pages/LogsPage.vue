<template>
    <!-- ═══ 日志下钻屏(原型 screen-11):pb-head(级别过滤 chips + 自动滚动
         switch + 清空次级动作)+ 日志卡(滚动收进卡内)。全部逻辑(SSE 流、
         级别过滤、导出、清空、自动滚动)沿用原有实现,仅模板层重排。 -->
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
            <label
                class="text-secondary-content ml-1 flex cursor-pointer items-center gap-2 text-[12.5px]">
                {{ t('settings.logs.autoScrollLabel') }}
                <ToggleButton v-model="autoScroll" size="small" />
            </label>
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
                                v-if="log.formattedSource"
                                class="text-secondary-content/70 mr-1.5">
                                {{ log.formattedSource }}
                            </span>
                            <span v-html="formatLogMessage(log.message)"></span>
                        </span>
                    </div>

                    <div
                        v-if="log.stackTrace"
                        class="border-line/60 bg-danger/5 border-b py-1 pr-3 pl-2 text-xs text-danger/90">
                        <pre class="whitespace-pre-wrap">{{ log.stackTrace }}</pre>
                    </div>
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
import DrillDownPage from '@/cells/app/components/DrillDownPage.vue'
import ToggleButton from '@/cells/kernel/components/ToggleButton.vue'
import { useUiStore } from '@/cells/app/stores/ui'

const logsApi = logsService()

const { t } = useI18n()
const uiStore = useUiStore()
const logs = ref<ILogEntry[]>([])
const autoScroll = ref(true)
const logContainer = ref<HTMLElement | null>(null)
const filterOptions = ref<IFilterOptions>({
    logLevel: 'all'
})
let eventSource: EventSource | null = null

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
    return logs.value.filter((log) => {
        if (filterOptions.value.logLevel !== 'all') {
            if (levelKey(log.logLevel) !== filterOptions.value.logLevel) {
                return false
            }
        }
        return true
    })
})

const levelShort = (level: string): string => {
    const key = levelKey(level)
    if (key === 'error') return 'ERR'
    if (key === 'warning') return 'WRN'
    if (key === 'information') return 'INF'
    return level.slice(0, 3).toUpperCase()
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

const formatLogMessage = (message: string): string => {
    // Replace color tags
    let formattedMessage = message
        .replace(/\|Green\|([^|]+)\|\/Green\|/g, '<span class="text-success">$1</span>')
        .replace(/\|Red\|([^|]+)\|\/Red\|/g, '<span class="text-danger">$1</span>')
        .replace(/\|Orange\|([^|]+)\|\/Orange\|/g, '<span class="text-warning">$1</span>')

    // Highlight environment variables
    formattedMessage = formattedMessage.replace(
        /'([A-Z_]+)'/g,
        '<span class="text-accent">\'$1\'</span>'
    )

    return formattedMessage
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

/// Export follows the current level filter (frozen semantics: the CTA
/// downloads what is on screen, not the hidden rows).
const exportLogs = () => {
    const timestamp = new Date().toISOString().replace(/[:.]/g, '-')
    const filename = `system-logs-${timestamp}.txt`

    let exportContent = `${t('settings.logs.exportTitle')}\n`
    exportContent += `${t('settings.logs.exportGenerated', { datetime: new Date().toLocaleString() })}\n`
    exportContent += `${t('settings.logs.exportTotalEntries', { count: filteredLogs.value.length })}\n`
    exportContent += `${'='.repeat(80)}\n\n`

    filteredLogs.value.forEach((log) => {
        exportContent += `[${formatDate(log.timestamp)} ${formatTime(log.timestamp)}] [${log.logLevel}] [${log.category}] ${log.message}\n`

        // Include stack trace
        if (log.stackTrace) {
            exportContent += `${t('settings.logs.exportStackTrace')}\n${log.stackTrace}\n`
        }

        exportContent += `\n`
    })

    const blob = new Blob([exportContent], { type: 'text/plain' })
    const url = window.URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = filename
    document.body.appendChild(link)
    link.click()
    document.body.removeChild(link)
    window.URL.revokeObjectURL(url)
}

watch(
    filterOptions,
    () => {
        scrollToBottom()
    },
    { deep: true }
)

onMounted(() => {
    // topbar CTA (prototype act table): ⇩ export of the filtered view
    uiStore.registerCta(t('settings.logs.exportCta'), exportLogs)
    uiStore.setSubOverride(t('settings.logs.topbarSub'))

    eventSource = logsApi.getStream()

    eventSource.onmessage = (event) => {
        try {
            const logData = JSON.parse(event.data)
            logs.value = [...logs.value, logData]
            scrollToBottom()
        } catch (error) {
            console.error('Error processing log entry:', error)
            console.error('Problematic data:', event.data)

            const fallbackEntry: ILogEntry = {
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
            }

            logs.value = [...logs.value, fallbackEntry]
        }
    }

    eventSource.onerror = (error) => {
        console.error('EventSource error:', error)
        logs.value.push({
            logLevel: 'error',
            message: t('settings.logs.connectionError'),
            timestamp: new Date().toISOString(),
            formattedSource: 'System',
            category: 'System'
        })
        // reconnect
        if (eventSource) {
            eventSource.close()
            setTimeout(() => {
                eventSource = logsApi.getStream()
            }, 5000)
        }
    }
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
