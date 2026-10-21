<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import axios from 'axios'
import { useToastStore } from '@/cells/app/stores/toast'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useTranslateStore } from '@/cells/app/stores/translate'
import { settingService } from '@/cells/app/api/settingService'
import { subtitleSearchService } from '@/cells/app/api/subtitleSearchService'
import ButtonComponent from '@/cells/kernel/components/ButtonComponent.vue'
import type {
    ISubtitleCandidate,
    ISubtitlePreviewResponse,
    ISubtitleProviderHealthState,
    SubtitleWebSearchUrlMap
} from '@/types/services'
import type { ILanguage } from '@/types'
import { MEDIA_TYPE } from '@/types'
import type { IMediaEmbeddedTrack } from '@/types/services'
import { mediaService } from '@/cells/app/api/mediaService'

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
    /// B04: extractable embedded tracks of the searched unit (text tracks
    /// with a recognized language). Matching ones surface as「本地内封轨道」
    /// chips at the top of the results — one click extracts them as
    /// sidecars through the same endpoint as the media page's row action.
    embeddedTracks?: IMediaEmbeddedTrack[]
}>()

const emit = defineEmits<{
    (e: 'close'): void
    (e: 'downloaded', file: string): void
}>()

// ── B04:本地内封轨道 chips(结果区顶部)════════════════════════
const mediaApi = mediaService(axios)
const embeddedBusyKey = ref<string | null>(null)

/// Embedded tracks of the searched unit whose language is among the CHECKED
/// languages — the population worth offering as a one-click local source.
const matchingEmbeddedTracks = computed<IMediaEmbeddedTrack[]>(() =>
    (props.embeddedTracks ?? []).filter(
        (track) =>
            track.language !== null &&
            languages.value.some((checked) => checked.toLowerCase() === track.language!.toLowerCase())
    )
)

/// One-click extraction: same idempotent endpoint as the media page's row
/// action; the emitted `downloaded` makes the page refresh its subtitle
/// payload (file card + coverage chips) exactly like a real download would.
async function extractEmbeddedTrack(track: IMediaEmbeddedTrack): Promise<void> {
    const key = `${track.mediaId}:${track.streamIndex}`
    if (embeddedBusyKey.value) return
    embeddedBusyKey.value = key
    try {
        const result = await mediaApi.extractEmbeddedTrack<{
            fileName: string
            created: boolean
        }>(
            props.mediaType === 'movie' ? MEDIA_TYPE.MOVIE : MEDIA_TYPE.EPISODE,
            track.mediaId,
            track.streamIndex
        )
        toast.push({
            message: result.created
                ? t('subSearch.embeddedExtractDone', { file: result.fileName })
                : t('subSearch.embeddedExtractExists', { file: result.fileName }),
            type: 'success'
        })
        emit('downloaded', result.fileName)
    } catch (error) {
        const detail = axios.isAxiosError(error)
            ? ((error.response?.data as { message?: string } | undefined)?.message ?? error.message)
            : String(error)
        toast.push({ message: t('subSearch.embeddedExtractFailed', { message: detail }), type: 'error' })
    } finally {
        embeddedBusyKey.value = null
    }
}

const { t } = useI18n()
const toast = useToastStore()
const settingStore = useSettingStore()
const translateStore = useTranslateStore()
const settingApi = settingService(axios)

// ── search state ─────────────────────────────────────────────
const query = ref('')

/// Fallback when the target_languages setting is missing or empty — the
/// previously hardcoded pair, kept so a search is always possible.
const FALLBACK_LANGUAGES = ['zh', 'en']

/// Normalized codes of a language-list setting; getSettings parses both the
/// raw JSON-string and array forms, anything unexpected falls through empty.
function settingLanguageCodes(key: 'target_languages' | 'source_languages'): string[] {
    const configured = settingStore.getSettings[key] as ILanguage[]
    return (Array.isArray(configured) ? configured : [])
        .map((language) => String(language?.code ?? '').toLowerCase())
        .filter((code) => code !== '')
}

/// The settings' target_languages set (fallback zh/en): the quick-toggle row
/// derives from it AND the server's「no subtitle needed」exemption judges
/// against that same list — the checked set (what is searched) may go beyond
/// it via the「more languages」picker, the exemption never does.
const targetLanguages = computed<string[]>(() => {
    const codes = settingLanguageCodes('target_languages')
    return codes.length > 0 ? codes : FALLBACK_LANGUAGES
})

/// Languages appended through the culture-table picker this modal session —
/// they join the quick-toggle row after the target-derived ones.
const extraLanguages = ref<string[]>([])

/// Quick-toggle set = target-derived ∪ appended (decoupled from the checked
/// set: unchecking a toggle never removes the button).
const languageOptions = computed<string[]>(() => [
    ...targetLanguages.value,
    ...extraLanguages.value.filter((code) => !targetLanguages.value.includes(code))
])

/// Every derived option is checked by default (previous behaviour).
const languages = ref<string[]>([...languageOptions.value])

/// Translation-source whitelist (settings source_languages): picker options
/// in it carry the「翻译源」marker — the automatic rounds already search them.
const sourceLanguageCodes = computed<Set<string>>(
    () => new Set(settingLanguageCodes('source_languages'))
)

// ── more-languages picker (any culture-table language) ───────
const moreLanguagesOpen = ref(false)
const languageFilter = ref('')

const cultureLanguages = computed<ILanguage[]>(() => translateStore.getLanguages)

/// Same filter semantics as the settings-page language selector: the input
/// only narrows by code or name, it never creates off-table codes.
const filteredCultureLanguages = computed<ILanguage[]>(() => {
    const needle = languageFilter.value.trim().toLowerCase()
    return cultureLanguages.value
        .filter(
            (language) =>
                needle === '' ||
                language.code.toLowerCase().includes(needle) ||
                language.name.toLowerCase().includes(needle)
        )
        .slice()
        .sort((a, b) => a.code.localeCompare(b.code))
})

/// Case-insensitive check lookup — culture-table codes keep their canonical
/// casing ("zh-TW") while the checked set may hold the normalized form.
function isCultureChecked(code: string): boolean {
    return languages.value.some((checked) => checked.toLowerCase() === code.toLowerCase())
}

/// Toggle one culture-table language: on it joins the checked set and (when
/// not target-derived) the quick-toggle row; off it leaves the checked set —
/// the row keeps the button so it can be re-checked like a target toggle.
function toggleCultureLanguage(code: string): void {
    const existing = languages.value.find(
        (checked) => checked.toLowerCase() === code.toLowerCase()
    )
    if (existing !== undefined) {
        languages.value = languages.value.filter((checked) => checked !== existing)
        return
    }
    languages.value = [...languages.value, code]
    if (!languageOptions.value.some((option) => option.toLowerCase() === code.toLowerCase())) {
        extraLanguages.value = [...extraLanguages.value, code]
    }
}

function toggleMoreLanguages(): void {
    moreLanguagesOpen.value = !moreLanguagesOpen.value
    if (moreLanguagesOpen.value && cultureLanguages.value.length === 0) {
        // Culture-table full set (GET /api/translate/languages) — the same
        // source the settings language-policy card picks from. Opening
        // again after a failure retries the fetch.
        void translateStore.setLanguages()
    }
}

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

// ── B09:候选明细抽屉 + 字幕预览 ═══════════════════════════════
/// Expanded candidate rows (detail drawer): keyed by provider:externalId —
/// stable across the score-ordered merge, cleared with the next round.
const expandedKeys = ref<Set<string>>(new Set())

function rowKey(candidate: ISubtitleCandidate): string {
    return candidate.provider + ':' + candidate.externalId
}

function isExpanded(row: { candidate: ISubtitleCandidate }): boolean {
    return expandedKeys.value.has(rowKey(row.candidate))
}

function toggleExpand(row: { candidate: ISubtitleCandidate }): void {
    const key = rowKey(row.candidate)
    expandedKeys.value = expandedKeys.value.has(key)
        ? new Set([...expandedKeys.value].filter((item) => item !== key))
        : new Set([...expandedKeys.value, key])
}

/// Signal dimension → localized label. A static literal map (no dynamic
/// t() keys) so the i18n key checker sees every key; unknown dimensions
/// (a newer server) fall back to the raw name.
const SIGNAL_LABELS: Record<string, string> = {
    hash: t('subSearch.signal.hash'),
    title: t('subSearch.signal.title'),
    year: t('subSearch.signal.year'),
    episode: t('subSearch.signal.episode'),
    releaseTokens: t('subSearch.signal.releaseTokens'),
    wrongEpisodeCap: t('subSearch.signal.wrongEpisodeCap')
}

function signalLabel(name: string): string {
    return SIGNAL_LABELS[name] ?? name
}

/// Front-end half of the 60s anti-double-click window: the server caches a
/// preview per ref for 60s, this map mirrors it so a re-click within the
/// window does not even fire the request. Cleared with each search round.
const PREVIEW_CACHE_TTL_MS = 60_000
const previewCache = new Map<string, { at: number; data: ISubtitlePreviewResponse }>()

/// The preview overlay state: which row is previewed (key + release title),
/// its answer (or null while loading) and the readable error, if any.
const previewing = ref<{ key: string; title: string } | null>(null)
const previewData = ref<ISubtitlePreviewResponse | null>(null)
const previewLoading = ref(false)
const previewError = ref<string | null>(null)

function closePreview(): void {
    previewing.value = null
    previewData.value = null
    previewError.value = null
    previewLoading.value = false
}

async function openPreview(row: CandidateRow): Promise<void> {
    if (disposed) return
    const key = rowKey(row.candidate)
    const cached = previewCache.get(key)
    if (cached && Date.now() - cached.at < PREVIEW_CACHE_TTL_MS) {
        // within the window: show the remembered answer, no request
        previewing.value = { key, title: releaseTitle(row.candidate) }
        previewData.value = cached.data
        previewError.value = null
        return
    }
    if (previewLoading.value) return
    previewing.value = { key, title: releaseTitle(row.candidate) }
    previewData.value = null
    previewError.value = null
    previewLoading.value = true
    try {
        const data = await subtitleSearchService.preview({
            cacheKey: row.cacheKey,
            candidateIndex: row.index,
            mediaType: props.mediaType,
            mediaId: props.mediaId
        })
        if (disposed) return
        previewData.value = data
        previewCache.set(key, { at: Date.now(), data })
    } catch (error) {
        if (disposed) return
        previewError.value = serverMessage(error) ?? t('subSearch.previewUnavailable')
    } finally {
        previewLoading.value = false
    }
}

/// SRT-style HH:MM:SS.mmm of a cue bound.
function formatCueTime(ms: number): string {
    const total = Math.max(0, Math.floor(ms))
    const hours = Math.floor(total / 3_600_000)
    const minutes = Math.floor((total % 3_600_000) / 60_000)
    const seconds = Math.floor((total % 60_000) / 1000)
    const millis = total % 1000
    return (
        `${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}` +
        `:${String(seconds).padStart(2, '0')}.${String(millis).padStart(3, '0')}`
    )
}

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
    /// Public-site deep link of this source for the round's search term
    /// (web-search escape hatch). Undefined until a response delivers the
    /// map — the chip then falls back to the manifest homepage.
    webSearchUrl?: string
}
const sourceStatuses = ref<Map<string, SourceStatus>>(new Map())
const sourceList = computed<SourceStatus[]>(() => [...sourceStatuses.value.values()])

/// Manifests of the current round, keyed by provider — the per-source retry
/// re-fires with the same request shape the round used, and the web-search
/// fallback reads each source's homepage from it.
const providerByKey = new Map<string, ISubtitleProviderHealthState>()

const sourcesTotal = ref(0)
const settledCount = ref(0)
const searchElapsed = ref(0)

/// MatchScore-descending view; insertion happens in mergeCandidates.
const sortedCandidates = computed<CandidateRow[]>(() => candidates.value)

const metaSources = computed(() => `${settledCount.value}/${sourcesTotal.value}`)

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
    provider: ISubtitleProviderHealthState,
    status: SourceStatus['status'],
    extra: { message?: string; count?: number } = {},
    preserveLink = true
): void {
    const prev = sourceStatuses.value.get(provider.provider)
    sourceStatuses.value.set(provider.provider, {
        key: provider.provider,
        label: provider.displayName || provider.provider,
        status,
        ...extra,
        // A sibling's response map may have landed this source's deep link
        // before its own terminal state arrived — keep it across in-round
        // state transitions; the round reset below starts clean instead.
        ...(preserveLink && prev?.webSearchUrl ? { webSearchUrl: prev.webSearchUrl } : {})
    })
}

/// Merges one response's web-search deep-link map into the round's source
/// chips (assrt-throttling escape hatch). The backend derives every entry
/// from the media-level request, so ANY sibling's response can fill links
/// for sources that errored, are still searching or were skipped — first
/// delivery wins, later responses of the same round carry identical URLs.
/// Call AFTER setStatus: setStatus recreates the entry without the link.
function applyWebSearchUrls(urls: SubtitleWebSearchUrlMap | null | undefined): void {
    if (!urls) return
    for (const [key, url] of Object.entries(urls)) {
        const status = sourceStatuses.value.get(key)
        if (status && !status.webSearchUrl) {
            sourceStatuses.value.set(key, { ...status, webSearchUrl: url })
        }
    }
}

/// The chip's escape-hatch link: the round's deep link when a response has
/// delivered one, else the manifest homepage (skipped/unconfigured sources
/// still get somewhere to jump). Undefined (xunlei/shooter) renders nothing.
function webLinkOf(source: SourceStatus): string | undefined {
    return source.webSearchUrl ?? providerByKey.get(source.key)?.websiteUrl ?? undefined
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
    // B09: the round's refs die with it — collapse the drawers, close the
    // preview overlay and forget the preview window (a stale cacheKey would
    // only ever answer 410).
    expandedKeys.value = new Set()
    closePreview()
    previewCache.clear()
    settledCount.value = 0
    sourcesTotal.value = 0
    searchElapsed.value = 0
    providersLoading.value = true
    providersFailed.value = false
    const start = performance.now()

    let providers: ISubtitleProviderHealthState[]
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

    const unconfigured = (p: ISubtitleProviderHealthState) => p.requiresAuth && !p.credentialsConfigured
    const searchable = providers.filter((p) => !unconfigured(p))
    for (const provider of providers) {
        providerByKey.set(provider.provider, provider)
        setStatus(provider, unconfigured(provider) ? 'skipped' : 'searching', {}, false)
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
                applyWebSearchUrls(result.webSearchUrls)
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
        applyWebSearchUrls(result.webSearchUrls)
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

/// Set-membership against the settings' target_languages — exactly the
/// server's exemption rule (SubtitleDownloadPipeline: a candidate whose
/// language is a configured target lands without translation). What the
/// user has currently checked plays no part: a target row keeps its single
/// download button even when its toggle is off, and a non-target row never
/// earns the badge no matter what was checked.
function isTargetLang(candidate: ISubtitleCandidate): boolean {
    return targetLanguages.value.includes(candidate.language.toLowerCase())
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
    closePreview()
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
/// last wraps to the first, focus outside is pulled back in). B09: while
/// the preview overlay is open, Escape closes ONLY the overlay — the
/// search dialog stays — and the Tab trap steps aside so focus moves
/// naturally inside the overlay.
function onWindowKeydown(event: KeyboardEvent): void {
    if (disposed) return
    if (event.key === 'Escape') {
        event.preventDefault()
        if (previewing.value) {
            closePreview()
            return
        }
        close()
        return
    }
    if (event.key !== 'Tab') return
    if (previewing.value) return
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
                        <!-- any culture-table language: opens the inline
                             picker below the query bar; picker selections
                             join this quick-toggle row -->
                        <button
                            type="button"
                            class="mono rounded border border-dashed px-2 py-0.5 text-[11px] transition-colors"
                            :class="moreLanguagesOpen
                                ? 'border-accent text-accent'
                                : 'border-line text-secondary-content hover:border-accent hover:text-primary-content'"
                            :aria-expanded="moreLanguagesOpen"
                            data-test="subsearch-more-languages"
                            @click="toggleMoreLanguages">
                            + {{ t('subSearch.moreLanguages') }}
                        </button>
                    </span>
                    <ButtonComponent variant="primary" size="sm" @click="runSearch()">
                        {{ t('subSearch.searchAction') }}
                    </ButtonComponent>
                </div>

                <!-- more-languages picker: the culture-table full set (same
                     source as the settings language-policy card); filter
                     narrows by code/name, options toggle the checked set,
                     translation-source whitelist entries carry a marker -->
                <div
                    v-if="moreLanguagesOpen"
                    class="border-line border-b px-4 py-2"
                    data-test="subsearch-language-panel">
                    <input
                        v-model="languageFilter"
                        type="text"
                        class="border-line bg-primary text-primary-content mono w-full rounded border px-2.5 py-1.5 text-xs outline-none focus:border-accent"
                        :placeholder="t('subSearch.languageFilterLabel')"
                        :aria-label="t('subSearch.languageFilterLabel')"
                        data-test="subsearch-language-filter">
                    <div
                        class="shell-scroll mt-2 max-h-56 overflow-y-auto"
                        role="listbox"
                        aria-multiselectable="true"
                        :aria-label="t('subSearch.moreLanguages')">
                        <div
                            v-if="translateStore.isLanguagesLoading && cultureLanguages.length === 0"
                            class="text-secondary-content px-1 py-3 text-xs">
                            {{ t('subSearch.languagesLoading') }}
                        </div>
                        <div
                            v-else-if="translateStore.hasLanguagesError"
                            class="text-secondary-content px-1 py-3 text-xs"
                            data-test="subsearch-language-error">
                            {{ t('subSearch.languagesFailed') }}
                        </div>
                        <div
                            v-else-if="cultureLanguages.length === 0"
                            class="text-secondary-content px-1 py-3 text-xs">
                            {{ t('subSearch.languageNoOptions') }}
                        </div>
                        <div
                            v-else-if="filteredCultureLanguages.length === 0"
                            class="text-secondary-content px-1 py-3 text-xs">
                            {{ t('subSearch.languageNoResults') }}
                        </div>
                        <template v-else>
                            <div
                                v-for="language in filteredCultureLanguages"
                                :key="language.code"
                                role="option"
                                :aria-selected="isCultureChecked(language.code)"
                                class="hover:bg-tertiary/50 flex cursor-pointer items-center gap-2 rounded px-2 py-1.5 text-xs transition-colors"
                                :class="isCultureChecked(language.code) ? 'bg-accent/15' : ''"
                                :data-test="'culture-option-' + language.code"
                                @click="toggleCultureLanguage(language.code)">
                                <span class="text-secondary-content mono w-14 flex-none text-[11px]">
                                    {{ language.code }}
                                </span>
                                <span class="text-primary-content truncate">{{ language.name }}</span>
                                <span
                                    v-if="sourceLanguageCodes.has(language.code.toLowerCase())"
                                    class="mono border-info/40 text-info rounded border px-1 py-px text-[10px] flex-none"
                                    :data-test="'culture-source-' + language.code">
                                    {{ t('subSearch.translationSource') }}
                                </span>
                                <span
                                    v-if="isCultureChecked(language.code)"
                                    class="text-accent ml-auto flex-none">
                                    ✓
                                </span>
                            </div>
                        </template>
                    </div>
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
                        <!-- web-search escape hatch (assrt throttling): jump
                             to this source's public search page for the
                             current term and download by hand. Rendered in
                             every state a URL exists for — searching, ok,
                             error, skipped (homepage fallback); sources
                             without a public page (xunlei/shooter) render
                             nothing -->
                        <a
                            v-if="webLinkOf(source)"
                            :href="webLinkOf(source)"
                            target="_blank"
                            rel="noopener noreferrer"
                            class="border-line text-secondary-content hover:border-accent hover:text-primary-content ml-1 rounded border px-1 py-px text-[10px] leading-none no-underline"
                            :data-test="'source-web-' + source.key"
                            :aria-label="source.label + ' ' + t('subSearch.openInSite')">
                            ↗
                        </a>
                    </span>
                </div>

                <!-- B04:本地内封轨道 chip 组——所搜语言有匹配内封文本轨时显示,
                     点击走与详情页行级按钮同一抽取端点(幂等) -->
                <div
                    v-if="matchingEmbeddedTracks.length > 0"
                    class="border-line bg-tertiary/40 flex flex-wrap items-center gap-2 border-b px-4 py-2 text-xs"
                    data-test="embedded-tracks-row">
                    <span class="text-secondary-content">
                        {{ t('subSearch.embeddedTracksTitle') }}
                    </span>
                    <button
                        v-for="track in matchingEmbeddedTracks"
                        :key="`${track.mediaId}-${track.streamIndex}`"
                        type="button"
                        class="mono rounded border px-1.5 py-0.5 text-[10.5px] transition-colors"
                        :class="embeddedBusyKey === `${track.mediaId}:${track.streamIndex}`
                            ? 'border-line text-secondary-content/60'
                            : 'border-info/40 text-info hover:border-info'"
                        :disabled="embeddedBusyKey !== null"
                        :title="t('subSearch.embeddedExtractTitle')"
                        :data-test="`embedded-chip-${track.streamIndex}`"
                        @click="extractEmbeddedTrack(track)">
                        {{ (track.language ?? '').toUpperCase() }} ·
                        {{ t('subSearch.embeddedExtract') }}
                    </button>
                </div>

                <!-- results -->
                <div class="shell-scroll min-h-[120px] flex-1 overflow-y-auto">
                    <div
                        v-for="row in sortedCandidates"
                        :key="row.candidate.provider + ':' + row.candidate.externalId"
                        class="border-line border-b">
                        <div
                            class="hover:bg-tertiary/50 flex items-center gap-3 px-4 py-2.5 transition-colors"
                            :class="isExistingLanguage(row.candidate) ? 'opacity-60' : ''"
                            data-test="candidate-row">
                            <!-- B09:行首明细抽屉开关——展开后该行下方渲染打分信号表 -->
                            <button
                                type="button"
                                class="text-secondary-content hover:text-primary-content h-5 w-5 flex-none cursor-pointer rounded text-[11px] leading-none transition-transform"
                                :class="isExpanded(row) ? 'rotate-90' : ''"
                                :aria-expanded="isExpanded(row)"
                                :aria-label="t('subSearch.matchDetails')"
                                data-test="candidate-toggle"
                                @click="toggleExpand(row)">
                                ▸
                            </button>
                            <span
                                class="w-0.5 shrink-0 self-stretch rounded"
                                :class="isTargetLang(row.candidate) ? 'bg-success' : 'bg-info'"
                                data-test="candidate-track" />
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
                        <!-- B09:明细抽屉——信号表(命中绿点/未中灰点,点数,总和=matchScore)+ 预览入口 -->
                        <div
                            v-if="isExpanded(row)"
                            class="bg-tertiary/30 flex flex-wrap items-start gap-4 px-4 py-3"
                            data-test="candidate-detail">
                            <div v-if="(row.candidate.signals ?? []).length > 0" class="min-w-[260px] flex-1">
                                <div class="text-secondary-content mb-1 text-[11px] font-semibold">
                                    {{ t('subSearch.signalsTitle') }}
                                </div>
                                <div
                                    v-for="signal in row.candidate.signals ?? []"
                                    :key="signal.name"
                                    class="flex items-center gap-2 py-0.5 text-[11px]"
                                    data-test="signal-row">
                                    <span
                                        class="h-1.5 w-1.5 flex-none rounded-full"
                                        :class="signal.hit ? 'bg-success' : 'bg-secondary-content/40'"
                                        data-test="signal-dot" />
                                    <span class="text-primary-content w-36 flex-none">
                                        {{ signalLabel(signal.name) }}
                                    </span>
                                    <span
                                        class="text-secondary-content mono ml-auto"
                                        :data-test="'signal-points-' + signal.name">
                                        {{ signal.points > 0 ? '+' + signal.points : signal.points }}
                                    </span>
                                </div>
                                <div
                                    class="border-line mt-1 flex items-center gap-2 border-t pt-1 text-[11px] font-semibold"
                                    data-test="signal-total-row">
                                    <span class="text-primary-content">{{ t('subSearch.signalTotal') }}</span>
                                    <span class="text-primary-content mono ml-auto" data-test="signal-total">
                                        {{ row.candidate.matchScore }}
                                    </span>
                                </div>
                            </div>
                            <div v-else class="text-secondary-content min-w-[260px] flex-1 py-1 text-[11px]">
                                {{ t('subSearch.signalsUnavailable') }}
                            </div>
                            <div class="flex flex-none flex-col items-end">
                                <ButtonComponent
                                    variant="secondary"
                                    size="sm"
                                    :disabled="previewLoading"
                                    data-test="preview-open"
                                    @click="openPreview(row)">
                                    {{ t('subSearch.preview') }}
                                </ButtonComponent>
                            </div>
                        </div>
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

        <!-- B09:字幕预览浮层(Teleport 模式照 MediaSourcesCard 判例;z 高于
             搜索主弹层,Escape 只关本层)——零副作用的内容预览,前 30 cue -->
        <div
            v-if="previewing"
            class="fixed inset-0 z-[60] flex items-center justify-center bg-primary/72 p-4"
            data-test="subtitle-preview-modal"
            @click.self="closePreview()">
            <div
                class="border-line bg-secondary flex max-h-[80vh] w-[min(680px,94vw)] flex-col overflow-hidden rounded-lg shadow-2xl"
                role="dialog"
                aria-modal="true"
                tabindex="-1"
                :aria-label="t('subSearch.previewTitle')">
                <div class="border-line flex items-center gap-3 border-b px-4 py-3">
                    <span class="text-sm font-semibold">{{ t('subSearch.previewTitle') }}</span>
                    <span class="text-secondary-content mono truncate text-[11px]" :title="previewing.title">
                        {{ previewing.title }}
                    </span>
                    <span
                        v-if="previewData"
                        class="text-secondary-content mono ml-auto flex-none text-[11px]"
                        data-test="preview-meta">
                        {{ t('subSearch.previewMeta', {
                            format: previewData.format,
                            count: previewData.cueCount,
                            shown: previewData.cues.length
                        }) }}
                    </span>
                    <button
                        type="button"
                        class="border-line text-primary-content hover:bg-tertiary ml-auto flex-none cursor-pointer rounded border px-2 py-0.5 text-xs"
                        :aria-label="t('common.close')"
                        data-test="preview-close"
                        @click="closePreview()">
                        {{ t('common.close') }}
                    </button>
                </div>
                <div class="shell-scroll flex-1 overflow-y-auto px-4 py-3">
                    <div v-if="previewLoading" class="text-secondary-content flex items-center gap-2 text-sm" data-test="preview-loading">
                        <span class="animate-pulse">●</span> {{ t('subSearch.previewLoading') }}
                    </div>
                    <div v-else-if="previewError" class="text-danger text-sm" data-test="preview-error">
                        {{ previewError }}
                    </div>
                    <template v-else-if="previewData">
                        <div
                            v-for="(cue, cueIndex) in previewData.cues"
                            :key="cueIndex"
                            class="border-line border-b py-1.5 last:border-b-0"
                            data-test="preview-cue">
                            <div class="text-secondary-content mono text-[10px]">
                                {{ formatCueTime(cue.startMs) }} → {{ formatCueTime(cue.endMs) }}
                            </div>
                            <div
                                v-for="(line, lineIndex) in cue.lines"
                                :key="lineIndex"
                                class="text-primary-content text-xs leading-snug">
                                {{ line }}
                            </div>
                        </div>
                    </template>
                </div>
                <div class="border-line text-secondary-content border-t px-4 py-2 text-[11px]">
                    {{ t('common.escape') }}
                </div>
            </div>
        </div>
    </Teleport>
</template>
