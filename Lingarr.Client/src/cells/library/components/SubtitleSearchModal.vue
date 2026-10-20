<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import axios from 'axios'
import { useToastStore } from '@/cells/app/stores/toast'
import { useSettingStore } from '@/cells/app/stores/setting'
import { settingService } from '@/cells/app/api/settingService'
import { subtitleSearchService } from '@/cells/app/api/subtitleSearchService'
import ButtonComponent from '@/cells/kernel/components/ButtonComponent.vue'
import type { ISubtitleCandidate } from '@/types/services'
import type { ILanguage } from '@/types'
import type { ISubtitleProviderManifest } from '@/types/plugin'

/// The auto-download threshold setting key (SettingKeys.SubtitleSearch.AutoScore
/// on the server); seeded "75" by M0008.
const AUTO_SCORE_SETTING = 'subtitle_search_auto_score'
const AUTO_SCORE_DEFAULT = 75

/// mediaType is the wire form the backend accepts ("movie" | "episode") —
/// anything else (e.g. "Show") is rejected with 400.
const props = defineProps<{
    mediaType: 'movie' | 'episode'
    mediaId: number
    title: string
    episode?: string
    /// Normalized audio languages of the media's container (null = unknown).
    /// When the audio already covers one of the searched target languages a
    /// one-time info row points that out (the search stays available).
    audioLanguages?: string[] | null
    /// Languages the media already carries subtitles in (external files +
    /// embedded tracks, scoped to the searched unit). Candidates in those
    /// languages render weakened with an「已存在」badge — manual re-download
    /// stays legal, so the buttons remain enabled.
    existingLanguages?: string[]
}>()

const emit = defineEmits<{
    (e: 'close'): void
    (e: 'downloaded', file: string): void
}>()

const { t } = useI18n()
const toast = useToastStore()
const settingStore = useSettingStore()
const settingApi = settingService(axios)

// ── search state ─────────────────────────────────────────────
const query = ref('')

/// Fallback when the target_languages setting is missing or empty — the
/// previously hardcoded pair, kept so a search is always possible.
const FALLBACK_LANGUAGES = ['zh', 'en']

/// The toggle row derives from the global target_languages setting: the
/// server's「no subtitle needed」exemption judges against that same list,
/// so the manual dialog must not offer a different language set.
const languageOptions = computed<string[]>(() => {
    // getSettings parses both the raw JSON-string and array forms; anything
    // unexpected (missing key, non-array) falls through to the fallback.
    const configured = settingStore.getSettings.target_languages as ILanguage[]
    const codes = (Array.isArray(configured) ? configured : [])
        .map((language) => String(language?.code ?? '').toLowerCase())
        .filter((code) => code !== '')
    return codes.length > 0 ? codes : FALLBACK_LANGUAGES
})

/// Every derived option is checked by default (previous behaviour).
const languages = ref<string[]>([...languageOptions.value])
const searching = ref(false)
const providersLoading = ref(true)
const providersFailed = ref(false)
const downloadBusy = ref<Set<string>>(new Set())

/// Auto-download threshold read from subtitle_search_auto_score on open
/// (default 75 when the setting is absent or unparsable). Drives the
/// low-confidence warning colour/text so the modal's score line always
/// mirrors the configured line, never a hardcoded one.
const autoScore = ref(AUTO_SCORE_DEFAULT)

/// Rows whose download already landed in this modal session: the buttons
/// disable and flip to「✓ 已下载」 until the next search round resets them.
const downloadedKeys = ref<Set<string>>(new Set())

/// One-time native-audio hint: closed once, stays closed for this modal
/// session (component lifetime); a fresh open of the modal shows it again.
const nativeAudioHintDismissed = ref(false)

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

/// Manifests of the current round, keyed by provider — the per-source retry
/// re-fires with the same request shape the round used.
const providerByKey = new Map<string, ISubtitleProviderManifest>()

const sourcesTotal = ref(0)
const settledCount = ref(0)
const searchElapsed = ref(0)

/// MatchScore-descending view; insertion happens in mergeCandidates.
const sortedCandidates = computed<CandidateRow[]>(() => candidates.value)

const metaSources = computed(() => `${settledCount.value}/${sourcesTotal.value}`)

/// Primary target language (rows in it get the single「下载」button): the
/// settings' first target language while it stays checked, else the first
/// checked one, else the first option.
const targetLang = computed(() =>
    languages.value.find(l => l === languageOptions.value[0])
        ?? languages.value[0]
        ?? languageOptions.value[0]
)

/// Audio hint condition — the backend's「任一目标」exemption semantics: known
/// audio covering ANY of the currently searched target languages means the
/// media usually needs no subtitles (manual search stays available anyway).
const audioCoversTarget = computed<boolean>(() => {
    const audio = props.audioLanguages
    if (!audio || audio.length === 0) return false
    const targets = languages.value.map((lang) => lang.toLowerCase())
    return audio.some((code) => targets.includes(code.toLowerCase()))
})
const showNativeAudioHint = computed(
    () => audioCoversTarget.value && !nativeAudioHintDismissed.value
)

function isExistingLanguage(candidate: ISubtitleCandidate): boolean {
    return (props.existingLanguages ?? []).some(
        (lang) => lang.toLowerCase() === candidate.language.toLowerCase()
    )
}

function isDownloaded(candidate: ISubtitleCandidate): boolean {
    return downloadedKeys.value.has(candidate.provider + ':' + candidate.externalId)
}

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
    providerByKey.clear()
    downloadedKeys.value = new Set()
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
        providerByKey.set(provider.provider, provider)
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

/// Per-source retry for failed (or cooled-down — those answer with an error
/// too) sources: one provider-scoped search re-fire into the CURRENT round.
/// Results merge through the ordinary merge rule, so retried candidates
/// carry their own response's cacheKey and index; the source strip and the
/// settled counter track the re-fire honestly.
async function retrySource(providerKey: string): Promise<void> {
    if (disposed) return
    const provider = providerByKey.get(providerKey)
    if (!provider) return
    const status = sourceStatuses.value.get(providerKey)
    if (!status || status.status !== 'error') return
    const signal = searchController?.signal
    if (!signal) return

    setStatus(provider, 'searching')
    settledCount.value = Math.max(0, settledCount.value - 1)
    const start = performance.now()
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
        if (disposed || signal.aborted) return
        setStatus(provider, 'ok', { count: result.candidates.length })
        mergeCandidates(
            result.candidates.map((candidate, index) => ({
                candidate,
                cacheKey: result.cacheKey,
                index
            }))
        )
    } catch (error) {
        // the captured signal aborts when a new round starts or the modal
        // closes, so a stale retry never writes into the newer round's state
        if (disposed || signal.aborted || axios.isCancel(error)) return
        const message = serverMessage(error)
        setStatus(provider, 'error', { message: message ?? '' })
    } finally {
        if (!disposed) {
            settledCount.value += 1
            searchElapsed.value = (performance.now() - start) / 1000
        }
    }
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
            // row-level ✓ state: the file landed, this row's buttons lock
            // into「已下载」until the next search round clears the set
            downloadedKeys.value.add(key)
            if (result.translationDuplicate) {
                toast.push({ message: t('subSearch.translationDuplicate'), type: 'info' })
            }
            // whole-season archive: one download landed a file next to every
            // episode of the season — the matched-count toast replaces the
            // single-file one
            const landed = result.filesLanded ?? 1
            toast.push({
                message: landed > 1
                    ? t('subSearch.downloadedSeasonPack', { count: landed })
                    : result.translationRequestId
                      ? t('subSearch.downloadedAndQueued', { file: result.filePath })
                      : t('subSearch.downloaded', { file: result.filePath }),
                type: 'success'
            })
            emit('downloaded', result.filePath)
        } else if (result.result === 'CooldownSkipped') {
            // the source sits in its rate-limit window (assrt 493 closure):
            // the server skipped the upstream fetch entirely and answered
            // the retry moment — advisory state, not a download failure
            const time = result.cooldownUntilUtc
                ? formatCooldownTime(result.cooldownUntilUtc)
                : null
            toast.push({
                message:
                    time === null
                        ? t('subSearch.downloadCooldownPending', {
                              provider: row.candidate.provider
                          })
                        : t('subSearch.downloadCooldown', {
                              provider: row.candidate.provider,
                              time
                          }),
                type: 'info'
            })
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

/// Local HH:MM of the UTC retry moment the CooldownSkipped response
/// carries — the toast quotes the concrete moment, not a span (a span goes
/// stale the moment it is read). Null for an unparsable/absent timestamp.
function formatCooldownTime(iso: string): string | null {
    const date = new Date(iso)
    if (Number.isNaN(date.getTime())) return null
    return `${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}`
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
    void loadAutoScore()
})

/// Reads subtitle_search_auto_score so the low-confidence warning mirrors
/// the configured auto-download line. Absent setting (400) or an unparsable
/// / out-of-range value falls back to the default 75.
async function loadAutoScore(): Promise<void> {
    try {
        const raw = await settingApi.getSetting<string>(AUTO_SCORE_SETTING)
        const parsed = Number.parseInt(String(raw), 10)
        autoScore.value =
            Number.isFinite(parsed) && parsed >= 0 && parsed <= 100 ? parsed : AUTO_SCORE_DEFAULT
    } catch {
        autoScore.value = AUTO_SCORE_DEFAULT
    }
}

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

                <!-- native-audio hint (requirement card A surfacing): shown
                     once per modal session when the audio track already
                     covers a searched target language; the search itself
                     stays fully available -->
                <div
                    v-if="showNativeAudioHint"
                    class="border-info/40 bg-info/10 flex items-start gap-2 border-b px-4 py-2 text-xs"
                    data-test="native-audio-hint">
                    <span class="text-info flex-1">
                        {{ t('subSearch.nativeAudioHint') }}
                    </span>
                    <button
                        type="button"
                        class="text-secondary-content hover:text-primary-content cursor-pointer rounded px-1 text-xs"
                        :aria-label="t('common.close')"
                        data-test="native-audio-hint-close"
                        @click="nativeAudioHintDismissed = true">
                        ×
                    </button>
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
                            v-for="lang in languageOptions"
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
                        <!-- failed (or cooled-down — those answer with an
                             error) sources re-fire individually into the
                             current round -->
                        <button
                            v-if="source.status === 'error'"
                            type="button"
                            class="border-line text-secondary-content hover:border-accent hover:text-primary-content ml-1 cursor-pointer rounded border px-1 py-px text-[10px] leading-none"
                            :data-test="'source-retry-' + source.key"
                            @click="retrySource(source.key)">
                            {{ t('subSearch.retrySource') }}
                        </button>
                    </span>
                </div>

                <!-- results -->
                <div class="shell-scroll min-h-[120px] flex-1 overflow-y-auto">
                    <div
                        v-for="row in sortedCandidates"
                        :key="row.candidate.provider + ':' + row.candidate.externalId"
                        class="border-line hover:bg-tertiary/50 flex items-center gap-3 border-b px-4 py-2.5 transition-colors"
                        :class="isExistingLanguage(row.candidate) ? 'opacity-60' : ''"
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
                            <!-- the media already carries this language — weak
                                 row + badge; manual re-download stays legal -->
                            <span
                                v-if="isExistingLanguage(row.candidate)"
                                class="mono border-line text-secondary-content rounded border px-1.5 py-px text-[10px]"
                                data-test="existing-badge">
                                {{ t('subSearch.existing') }}
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
                            :class="row.candidate.matchScore < autoScore ? 'text-warning' : row.candidate.hashMatch ? 'text-success' : ''"
                            :title="row.candidate.matchScore < autoScore
                                ? t('subSearch.lowConfidence', { score: autoScore })
                                : ''">
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
                                :disabled="isBusy(row.candidate) || isDownloaded(row.candidate)"
                                @click="download(row, true)">
                                {{ isBusy(row.candidate)
                                    ? '…'
                                    : isDownloaded(row.candidate)
                                      ? '✓ ' + t('subSearch.downloadedMark')
                                      : t('subSearch.download') }}
                            </ButtonComponent>
                            <template v-else>
                                <ButtonComponent
                                    variant="secondary"
                                    size="sm"
                                    :disabled="isBusy(row.candidate) || isDownloaded(row.candidate)"
                                    @click="download(row, true)">
                                    {{ isDownloaded(row.candidate)
                                        ? '✓ ' + t('subSearch.downloadedMark')
                                        : t('subSearch.downloadOnly') }}
                                </ButtonComponent>
                                <ButtonComponent
                                    variant="primary"
                                    size="sm"
                                    :disabled="isBusy(row.candidate) || isDownloaded(row.candidate)"
                                    @click="download(row, false)">
                                    {{ isDownloaded(row.candidate)
                                        ? '✓ ' + t('subSearch.downloadedMark')
                                        : t('subSearch.downloadAndTranslate') }}
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
