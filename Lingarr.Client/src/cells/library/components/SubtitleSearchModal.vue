<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import axios from 'axios'
import { useToastStore } from '@/cells/app/stores/toast'
import { subtitleSearchService } from '@/cells/app/api/subtitleSearchService'
import ButtonComponent from '@/cells/kernel/components/ButtonComponent.vue'
import type { ISubtitleCandidate } from '@/types/services'
import type { ISubtitleProviderManifest } from '@/types/plugin'

/// mediaType is the wire form the backend accepts ("movie" | "episode") —
/// anything else (e.g. "Show") is rejected with 400.
const props = defineProps<{
    mediaType: 'movie' | 'episode'
    mediaId: number
    title: string
    episode?: string
}>()

const emit = defineEmits<{
    (e: 'close'): void
    (e: 'downloaded', file: string): void
}>()

const { t } = useI18n()
const toast = useToastStore()

// ── search state ─────────────────────────────────────────────
const query = ref('')
const languages = ref<string[]>(['zh', 'en'])
const searching = ref(false)
const providersLoading = ref(true)
const providersFailed = ref(false)
const downloadBusy = ref<Set<string>>(new Set())

/// One merged list across sources. Each row keeps the cacheKey of the
/// search round's response it came from and its index within THAT
/// response — the download endpoint resolves cacheKey to one source's
/// candidate list, so a merged-list index would address the wrong file.
interface CandidateRow {
    candidate: ISubtitleCandidate
    cacheKey: string
    index: number
}
const candidates = ref<CandidateRow[]>([])

interface SourceStatus {
    key: string
    label: string
    status: 'searching' | 'ok' | 'skipped' | 'error'
    message?: string
    count?: number
}
const sourceStatuses = ref<Map<string, SourceStatus>>(new Map())
const sourceList = computed<SourceStatus[]>(() => [...sourceStatuses.value.values()])

const sourcesTotal = ref(0)
const settledCount = ref(0)
const searchElapsed = ref(0)

/// MatchScore-descending view; insertion happens in mergeCandidates.
const sortedCandidates = computed<CandidateRow[]>(() => candidates.value)

const metaSources = computed(() => `${settledCount.value}/${sourcesTotal.value}`)

const targetLang = computed(() =>
    languages.value.find(l => l === 'zh') ? 'zh' : languages.value[0] ?? 'en'
)

// ── request lifecycle ────────────────────────────────────────
/// One controller per search round backs every per-source request; each
/// download gets its own. teardown() aborts everything so a closed modal
/// stops the network and never toasts into a dead surface.
let searchController: AbortController | null = null
const downloadControllers = new Map<string, AbortController>()
let round = 0
let disposed = false

function serverMessage(error: unknown): string | null {
    if (axios.isAxiosError(error)) {
        const data = error.response?.data as { message?: string } | undefined
        return data?.message ?? error.message
    }
    if (error instanceof Error && error.message) return error.message
    return null
}

function cancelled(error: unknown): boolean {
    return (searchController?.signal.aborted ?? false) || axios.isCancel(error)
}

function setStatus(
    provider: ISubtitleProviderManifest,
    status: SourceStatus['status'],
    extra: { message?: string; count?: number } = {}
): void {
    sourceStatuses.value.set(provider.provider, {
        key: provider.provider,
        label: provider.displayName || provider.provider,
        status,
        ...extra
    })
}

/// Stable score-descending merge: arriving rows slot in below every
/// already-rendered row of an equal score (no reshuffle of what the user
/// is looking at), above lower scores.
function mergeCandidates(rows: CandidateRow[]): void {
    candidates.value = [...candidates.value, ...rows].sort(
        (a, b) => b.candidate.matchScore - a.candidate.matchScore
    )
}

// ── search fan-out ───────────────────────────────────────────
async function runSearch(): Promise<void> {
    if (disposed) return
    searchController?.abort()
    searchController = new AbortController()
    const signal = searchController.signal
    const myRound = ++round

    candidates.value = []
    sourceStatuses.value = new Map()
    settledCount.value = 0
    sourcesTotal.value = 0
    searchElapsed.value = 0
    providersLoading.value = true
    providersFailed.value = false
    const start = performance.now()

    let providers: ISubtitleProviderManifest[]
    try {
        providers = await subtitleSearchService.getProviders()
    } catch (error) {
        if (disposed || myRound !== round) return
        providersLoading.value = false
        providersFailed.value = true
        searching.value = false
        toast.push({ message: serverMessage(error) ?? t('subSearch.failed'), type: 'error' })
        return
    }
    if (disposed || myRound !== round) return
    providersLoading.value = false

    const unconfigured = (p: ISubtitleProviderManifest) => p.requiresAuth && !p.credentialsConfigured
    const searchable = providers.filter((p) => !unconfigured(p))
    for (const provider of providers) {
        setStatus(provider, unconfigured(provider) ? 'skipped' : 'searching')
    }
    sourcesTotal.value = searchable.length
    if (searchable.length === 0) {
        searching.value = false
        return
    }
    searching.value = true

    let okCount = 0
    let errorCount = 0
    let firstFailure: string | null = null
    await Promise.allSettled(
        searchable.map(async (provider) => {
            try {
                const keyword = query.value.trim()
                const result = await subtitleSearchService.search(
                    {
                        provider: provider.provider,
                        mediaType: props.mediaType,
                        mediaId: props.mediaId,
                        languages: languages.value,
                        ...(keyword ? { query: keyword } : {})
                    },
                    signal
                )
                if (disposed || myRound !== round) return
                okCount += 1
                setStatus(provider, 'ok', { count: result.candidates.length })
                mergeCandidates(
                    result.candidates.map((candidate, index) => ({
                        candidate,
                        cacheKey: result.cacheKey,
                        index
                    }))
                )
            } catch (error) {
                if (disposed || myRound !== round || cancelled(error)) return
                errorCount += 1
                const message = serverMessage(error)
                firstFailure ??= message ?? t('subSearch.failed')
                setStatus(provider, 'error', { message: message ?? '' })
            } finally {
                if (!disposed && myRound === round) {
                    settledCount.value += 1
                    searchElapsed.value = (performance.now() - start) / 1000
                    if (settledCount.value >= sourcesTotal.value) {
                        searching.value = false
                        // every source failed — surface the server's own
                        // message, not a generic one (a source answering
                        // zero results is not a failure)
                        if (okCount === 0 && errorCount > 0 && firstFailure !== null) {
                            toast.push({ message: firstFailure, type: 'error' })
                        }
                    }
                }
            }
        })
    )
}

// ── download ────────────────────────────────────────────────
/// skipTranslation carries the button's real semantics: true = only write
/// the file ("仅下载" / target-language rows), false = let the server's
/// translation adjudication run ("下载并翻译").
async function download(row: CandidateRow, skipTranslation: boolean): Promise<void> {
    const key = row.candidate.provider + ':' + row.candidate.externalId
    if (downloadBusy.value.has(key)) return
    downloadBusy.value.add(key)
    const controller = new AbortController()
    downloadControllers.set(key, controller)

    try {
        const result = await subtitleSearchService.download(
            {
                cacheKey: row.cacheKey,
                candidateIndex: row.index,
                mediaType: props.mediaType,
                mediaId: props.mediaId,
                source: 'Manual',
                skipTranslation
            },
            controller.signal
        )
        if (disposed) return
        if (result.filePath) {
            if (result.translationDuplicate) {
                toast.push({ message: t('subSearch.translationDuplicate'), type: 'info' })
            }
            toast.push({
                message: result.translationRequestId
                    ? t('subSearch.downloadedAndQueued', { file: result.filePath })
                    : t('subSearch.downloaded', { file: result.filePath }),
                type: 'success'
            })
            emit('downloaded', result.filePath)
        } else if (result.error) {
            toast.push({
                message: t('subSearch.downloadRejected', {
                    result: result.result,
                    error: result.error
                }),
                type: 'error'
            })
        } else {
            toast.push({ message: t('subSearch.downloadFailed'), type: 'error' })
        }
    } catch (error) {
        if (disposed || controller.signal.aborted || axios.isCancel(error)) return
        toast.push({ message: serverMessage(error) ?? t('subSearch.downloadFailed'), type: 'error' })
    } finally {
        downloadBusy.value.delete(key)
        downloadControllers.delete(key)
    }
}

function isTargetLang(candidate: ISubtitleCandidate): boolean {
    return candidate.language.toLowerCase() === targetLang.value
}

function isBusy(candidate: ISubtitleCandidate): boolean {
    return downloadBusy.value.has(candidate.provider + ':' + candidate.externalId)
}

function releaseTitle(candidate: ISubtitleCandidate): string {
    return candidate.releaseInfo ?? `${candidate.provider}:${candidate.externalId}`
}

/// Same byte formatting as the media detail page's subtitle rows.
function formatBytes(bytes: number | null): string {
    if (bytes === null || !Number.isFinite(bytes) || bytes <= 0) return '—'
    if (bytes < 1024) return `${bytes} B`
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

// ── close / focus / a11y ─────────────────────────────────────
const dialogRef = ref<HTMLElement | null>(null)

function teardown(): void {
    if (disposed) return
    disposed = true
    searchController?.abort()
    searchController = null
    for (const controller of downloadControllers.values()) controller.abort()
    downloadControllers.clear()
    window.removeEventListener('keydown', onWindowKeydown)
}

function close(): void {
    teardown()
    emit('close')
}

/// Escape closes from anywhere; Tab cycles inside the dialog (a simple
/// trap: shift-Tab on the first focusable wraps to the last, Tab on the
/// last wraps to the first, focus outside is pulled back in).
function onWindowKeydown(event: KeyboardEvent): void {
    if (disposed) return
    if (event.key === 'Escape') {
        event.preventDefault()
        close()
        return
    }
    if (event.key !== 'Tab') return
    const dialog = dialogRef.value
    if (!dialog) return
    const focusables = [
        ...dialog.querySelectorAll<HTMLElement>(
            'a[href], button:not([disabled]), input:not([disabled]), select, textarea, [tabindex]:not([tabindex="-1"])'
        )
    ]
    if (focusables.length === 0) {
        event.preventDefault()
        return
    }
    const first = focusables[0]
    const last = focusables[focusables.length - 1]
    const active = document.activeElement as HTMLElement | null
    const inside = active !== null && dialog.contains(active)
    if (event.shiftKey) {
        if (!inside || active === first) {
            event.preventDefault()
            last.focus()
        }
    } else if (!inside || active === last) {
        event.preventDefault()
        first.focus()
    }
}

onMounted(() => {
    query.value = props.title + (props.episode ? ` ${props.episode}` : '')
    window.addEventListener('keydown', onWindowKeydown)
    dialogRef.value?.focus()
    void runSearch()
})

onBeforeUnmount(teardown)
</script>

<template>
    <Teleport to="body">
        <div
            class="fixed inset-0 z-50 flex items-start justify-center bg-primary/72 pt-[12vh]"
            data-test="subtitle-search-modal"
            @click.self="close()">
            <div
                ref="dialogRef"
                class="border-line bg-secondary flex max-h-[76vh] w-[min(780px,94vw)] flex-col overflow-hidden rounded-lg shadow-2xl"
                role="dialog"
                aria-modal="true"
                tabindex="-1"
                :aria-label="t('subSearch.title', { title, episode: episode ?? '' })">
                <!-- header -->
                <div class="border-line flex items-center gap-3 border-b px-4 py-3">
                    <span class="text-sm font-semibold">
                        {{ t('subSearch.title', { title, episode: episode ?? '' }) }}
                    </span>
                    <span class="text-secondary-content mono ml-auto text-[11px]">
                        {{ t('common.escape') }}
                    </span>
                </div>

                <!-- query bar -->
                <div class="border-line flex flex-wrap items-center gap-2 border-b px-4 py-3">
                    <input
                        v-model="query"
                        class="border-line bg-primary text-primary-content mono min-w-[200px] flex-1 rounded border px-2.5 py-1.5 text-xs outline-none focus:border-accent"
                        :aria-label="t('subSearch.queryLabel')"
                        data-test="subsearch-query-input"
                        @keydown.enter="runSearch()">
                    <span class="flex gap-1">
                        <button
                            v-for="lang in ['zh', 'en']"
                            :key="lang"
                            class="mono rounded border px-2 py-0.5 text-[11px] transition-colors"
                            :class="languages.includes(lang)
                                ? 'border-accent bg-accent/15 text-accent'
                                : 'border-line text-secondary-content hover:border-accent'"
                            :aria-pressed="languages.includes(lang)"
                            @click="languages = languages.includes(lang)
                                ? languages.filter(l => l !== lang)
                                : [...languages, lang]">
                            {{ lang.toUpperCase() }}
                        </button>
                    </span>
                    <ButtonComponent variant="primary" size="sm" @click="runSearch()">
                        {{ t('subSearch.searchAction') }}
                    </ButtonComponent>
                </div>

                <!-- results header: meta + per-source status strip -->
                <div
                    class="border-line text-secondary-content flex flex-wrap items-center gap-x-3 gap-y-1 border-b px-4 py-2 text-xs"
                    :aria-label="t('subSearch.sourcesLabel')">
                    <span class="mono" data-test="search-meta">
                        {{ t('subSearch.meta', {
                            count: sortedCandidates.length,
                            sources: metaSources,
                            seconds: searchElapsed.toFixed(1),
                        }) }}
                    </span>
                    <span
                        v-for="source in sourceList"
                        :key="source.key"
                        class="mono rounded border px-1.5 py-px text-[10px]"
                        :class="{
                            'border-line text-secondary-content': source.status === 'searching',
                            'border-success/40 text-success': source.status === 'ok',
                            'border-line text-secondary-content/60': source.status === 'skipped',
                            'border-danger/40 text-danger': source.status === 'error'
                        }"
                        :data-test="'source-status-' + source.key">
                        {{ source.label }} ·
                        {{ source.status === 'searching'
                            ? t('subSearch.sourceSearching')
                            : source.status === 'ok'
                              ? t('subSearch.sourceOk', { count: source.count ?? 0 })
                              : source.status === 'skipped'
                                ? t('subSearch.sourceSkipped')
                                : t('subSearch.sourceError', { message: source.message ?? '' }) }}
                    </span>
                </div>

                <!-- results -->
                <div class="shell-scroll min-h-[120px] flex-1 overflow-y-auto">
                    <div
                        v-for="row in sortedCandidates"
                        :key="row.candidate.provider + ':' + row.candidate.externalId"
                        class="border-line hover:bg-tertiary/50 flex items-center gap-3 border-b px-4 py-2.5 transition-colors"
                        data-test="candidate-row">
                        <span
                            class="w-0.5 shrink-0 self-stretch rounded"
                            :class="isTargetLang(row.candidate) ? 'bg-success' : 'bg-info'" />
                        <div class="min-w-0 flex-1">
                            <div
                                class="text-primary-content mono truncate text-xs font-medium"
                                :title="releaseTitle(row.candidate)">
                                {{ releaseTitle(row.candidate) }}
                            </div>
                            <div class="text-secondary-content text-[11px]">
                                {{ row.candidate.uploader ?? '—' }} · {{ row.candidate.format }} · {{ formatBytes(row.candidate.size) }}
                            </div>
                        </div>
                        <span class="flex flex-wrap gap-1">
                            <span
                                class="mono rounded px-1.5 py-px text-[10px]"
                                :class="isTargetLang(row.candidate)
                                    ? 'border-success/40 text-success border'
                                    : 'border-line text-secondary-content border'">
                                {{ isTargetLang(row.candidate) ? row.candidate.language.toUpperCase() + ' · ' + t('subSearch.noTranslate') : row.candidate.language.toUpperCase() }}
                            </span>
                            <span v-if="row.candidate.hashMatch" class="mono border-success/40 text-success rounded border px-1.5 py-px text-[10px]">
                                {{ t('subSearch.hashMatch') }}
                            </span>
                            <span v-if="row.candidate.forced" class="mono border-line text-secondary-content rounded border px-1.5 py-px text-[10px]">
                                {{ t('subSearch.forced') }}
                            </span>
                            <span v-if="row.candidate.hearingImpaired" class="mono border-line text-secondary-content rounded border px-1.5 py-px text-[10px]">
                                {{ t('subSearch.hearingImpaired') }}
                            </span>
                        </span>
                        <span
                            class="mono w-8 text-right text-xs"
                            :class="row.candidate.matchScore < 75 ? 'text-warning' : row.candidate.hashMatch ? 'text-success' : ''"
                            :title="row.candidate.matchScore < 75 ? t('subSearch.lowConfidence') : ''">
                            {{ row.candidate.matchScore }}
                        </span>
                        <span class="mono border-line text-secondary-content rounded border px-1.5 py-px text-[11px]">
                            {{ row.candidate.provider }}
                        </span>
                        <span class="flex justify-end gap-1.5">
                            <ButtonComponent
                                v-if="isTargetLang(row.candidate)"
                                variant="primary"
                                size="sm"
                                :disabled="isBusy(row.candidate)"
                                @click="download(row, true)">
                                {{ isBusy(row.candidate) ? '…' : t('subSearch.download') }}
                            </ButtonComponent>
                            <template v-else>
                                <ButtonComponent
                                    variant="secondary"
                                    size="sm"
                                    :disabled="isBusy(row.candidate)"
                                    @click="download(row, true)">
                                    {{ t('subSearch.downloadOnly') }}
                                </ButtonComponent>
                                <ButtonComponent
                                    variant="primary"
                                    size="sm"
                                    :disabled="isBusy(row.candidate)"
                                    @click="download(row, false)">
                                    {{ t('subSearch.downloadAndTranslate') }}
                                </ButtonComponent>
                            </template>
                        </span>
                    </div>

                    <!-- states -->
                    <div v-if="searching || providersLoading" class="text-secondary-content flex items-center gap-2 px-4 py-6 text-sm">
                        <span class="animate-pulse">●</span> {{ t('subSearch.searching') }}
                    </div>
                    <div v-else-if="providersFailed" class="text-secondary-content px-4 py-6 text-center text-sm">
                        {{ t('subSearch.failed') }}
                    </div>
                    <div v-else-if="sourcesTotal === 0" class="text-secondary-content px-4 py-6 text-center text-sm">
                        {{ t('subSearch.noSources') }}
                    </div>
                    <div v-else-if="!searching && sortedCandidates.length === 0" class="text-secondary-content px-4 py-6 text-center text-sm">
                        {{ t('subSearch.noResults') }}
                    </div>
                </div>
            </div>
        </div>
    </Teleport>
</template>
