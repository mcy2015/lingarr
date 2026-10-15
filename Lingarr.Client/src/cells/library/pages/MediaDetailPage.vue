<template>
    <!-- ═══ pb-head(原型下钻屏页头):返回 · 标题 + mono 副行 · 动作组 ═══ -->
    <DrillDownPage
        :back-to="{ name: 'library' }"
        :back-label="t('mediaDetail.back')"
        :title="item?.title ?? t('common.loading')"
        :sub="headerMeta">
        <template #actions>
            <button
                v-if="isShow"
                type="button"
                class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                @click="router.push({ name: 'import', query: { tab: 'season', show: props.id } })">
                {{ t('library.action.seasonImport') }}
            </button>
            <button
                v-else
                type="button"
                class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                @click="router.push({ name: 'import' })">
                {{ t('library.toolbar.import') }}
            </button>
            <ContextMenu v-if="isShow" @select="translateMissing">
                <button
                    type="button"
                    class="bg-accent text-accent-content border-accent cursor-pointer rounded-md border px-2.5 py-1 text-xs font-semibold transition-colors hover:brightness-106">
                    {{ t('mediaDetail.translateMissing') }}
                </button>
            </ContextMenu>
            <button
                v-else
                type="button"
                class="bg-accent text-accent-content border-accent cursor-pointer rounded-md border px-2.5 py-1 text-xs font-semibold transition-colors hover:brightness-106"
                @click="movieTranslateMissing">
                {{ t('mediaDetail.translateMissing') }}
            </button>
        </template>

        <!-- ═══ 工具栏:季段选(剧集)· 语言 chips · 最近活动 ═══ -->
        <div class="flex shrink-0 flex-wrap items-center gap-2">
            <div
                v-if="isShow"
                class="border-line inline-flex overflow-hidden rounded-md border"
                role="group"
                :aria-label="t('mediaDetail.seasons')">
                <button
                    v-for="season in seasons"
                    :key="season.seasonNumber"
                    type="button"
                    class="border-line text-secondary-content hover:text-primary-content cursor-pointer border-r px-3 py-1.5 text-[12.5px] transition-colors last:border-r-0"
                    :class="
                        activeSeason === season.seasonNumber
                            ? 'bg-tertiary text-primary-content'
                            : ''
                    "
                    :aria-pressed="activeSeason === season.seasonNumber"
                    @click="activeSeason = season.seasonNumber">
                    S{{ String(season.seasonNumber).padStart(2, '0') }}
                </button>
            </div>
            <div class="flex flex-wrap gap-1">
                <span
                    v-for="track in item?.tracks ?? []"
                    :key="track.language"
                    :class="chipClass(movieBusyState(track))"
                    :title="trackTitle(track)">
                    {{ trackLabel(track) }}
                </span>
            </div>
            <span class="mono text-secondary-content ml-auto text-xs">
                {{ t('mediaDetail.lastActivity', { time: lastActivityLabel }) }}
            </span>
        </div>

        <!-- ═══ 剧集:逐集轨道表 ═══ -->
        <template v-if="isShow">
            <div
                class="border-line bg-secondary flex min-h-0 flex-[1.5] flex-col rounded-md border">
                <div class="shell-scroll min-h-0 flex-1 overflow-auto">
                    <table class="w-full text-[13px]">
                        <thead>
                            <tr>
                                <th :class="thClass" class="w-11">#</th>
                                <th :class="thClass">{{ t('mediaDetail.episodeTitle') }}</th>
                                <th :class="thClass">{{ t('library.table.tracks') }}</th>
                                <th :class="thClass">{{ t('library.table.status') }}</th>
                                <th :class="thClass">{{ t('library.table.lastActivity') }}</th>
                                <th :class="thClass" class="w-36"></th>
                            </tr>
                        </thead>
                        <tbody>
                            <tr v-if="episodes.length === 0">
                                <td :class="tdClass" colspan="6">
                                    <span class="text-secondary-content/60 text-xs">
                                        {{ t('mediaDetail.noEpisodes') }}
                                    </span>
                                </td>
                            </tr>
                            <tr v-for="ep in episodes" :key="ep.id" class="hover:bg-tertiary/50">
                                <td :class="tdClass">
                                    <span class="mono text-secondary-content text-[11px]">
                                        E{{ String(ep.episodeNumber).padStart(2, '0') }}
                                    </span>
                                </td>
                                <td :class="tdClass">
                                    <b class="truncate font-semibold" :title="ep.title">
                                        {{ ep.title }}
                                    </b>
                                </td>
                                <td :class="tdClass">
                                    <div class="flex flex-wrap gap-1">
                                        <span
                                            v-for="lang in episodeLanguages(ep)"
                                            :key="lang"
                                            :class="
                                                chipClass(
                                                    episodeHas(ep, lang) ? 'complete' : 'none'
                                                )
                                            ">
                                            {{ lang.toUpperCase() }}
                                            {{ episodeHas(ep, lang) ? '✓' : '·' }}
                                        </span>
                                    </div>
                                </td>
                                <td :class="tdClass">
                                    <span :class="episodeStatusClass(ep)">
                                        {{ episodeStatusLabel(ep) }}
                                    </span>
                                </td>
                                <td :class="tdClass">
                                    <time class="mono text-secondary-content text-xs">
                                        {{
                                            episodeRequest(ep)
                                                ? formatTime(episodeRequest(ep)!.updatedAt ?? '')
                                                : '—'
                                        }}
                                    </time>
                                </td>
                                <td :class="tdClass">
                                    <div
                                        class="flex items-center justify-end gap-1.5 whitespace-nowrap">
                                        <router-link
                                            v-if="
                                                episodeRequest(ep)?.status ===
                                                TRANSLATION_STATUS.COMPLETED
                                            "
                                            :to="{
                                                name: 'proofread',
                                                params: { id: episodeRequest(ep)!.id }
                                            }">
                                            <button
                                                type="button"
                                                class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded border px-2 py-0.5 text-xs transition-colors">
                                                {{ t('common.translationAction.proofread') }}
                                            </button>
                                        </router-link>
                                        <router-link
                                            v-else-if="episodeDetailRequest(ep)"
                                            :to="{
                                                name: 'queue-detail',
                                                params: { id: episodeDetailRequest(ep)!.id }
                                            }">
                                            <button
                                                type="button"
                                                class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded border px-2 py-0.5 text-xs transition-colors">
                                                {{ t('library.action.detail') }}
                                            </button>
                                        </router-link>
                                        <ContextMenu
                                            v-if="episodeSource(ep)"
                                            :media="ep"
                                            :subtitle="episodeSource(ep)"
                                            media-type="Episode"
                                            @select="translateEpisode(ep, $event)">
                                            <button
                                                type="button"
                                                class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded border px-2 py-0.5 text-xs transition-colors">
                                                {{ t('mediaDetail.translate') }}
                                            </button>
                                        </ContextMenu>
                                    </div>
                                </td>
                            </tr>
                        </tbody>
                    </table>
                </div>
            </div>
        </template>

        <!-- ═══ 电影:进行中任务卡(无任务时空态主行动) ═══ -->
        <div
            v-else
            class="border-line bg-secondary flex min-h-[160px] flex-1 flex-col rounded-md border">
            <div class="border-line flex shrink-0 items-center gap-2.5 border-b px-4 py-3">
                <h2 class="text-[13px] font-semibold">{{ t('mediaDetail.tasks') }}</h2>
                <span v-if="tasksMeta" class="mono text-secondary-content ml-auto text-xs">
                    {{ tasksMeta }}
                </span>
            </div>
            <div class="shell-scroll flex min-h-0 flex-1 flex-col overflow-y-auto">
                <JobRow v-for="request in activeRequests" :key="request.id" :request="request" />
                <div
                    v-if="item && activeRequests.length === 0"
                    class="flex flex-wrap items-center justify-center gap-3 px-4 py-6 text-center">
                    <span v-if="movieTargetComplete" class="text-success text-xs">
                        ✓ {{ t('mediaDetail.movie.done') }}
                    </span>
                    <template v-else>
                        <span class="text-secondary-content/60 text-xs">
                            {{ t('mediaDetail.movie.untranslated') }}
                        </span>
                        <button
                            type="button"
                            class="bg-accent text-accent-content border-accent cursor-pointer rounded-md border px-2.5 py-1 text-xs font-semibold transition-colors hover:brightness-106"
                            @click="movieTranslateMissing">
                            {{ t('mediaDetail.movie.start') }}
                        </button>
                    </template>
                </div>
            </div>
        </div>

        <!-- ═══ Subtitle files: external subtitle files actually existing under the directory (ground truth of the covering chips);
             dual-language files (scjp/简日…) one row with two language tags; the embedded track section below shows ffprobe detection
             results (F2b: text tracks with recognized language and uncovered target language have a translate entry; graphic tracks
             such as PGS are marked non-translatable and have no entry) ═══ -->
        <div
            class="border-line bg-secondary flex max-h-[240px] min-h-0 flex-col rounded-md border"
            data-test="subtitle-files-card">
            <div class="border-line flex shrink-0 items-center gap-2.5 border-b px-4 py-3">
                <h2 class="text-[13px] font-semibold">
                    {{ t('mediaDetail.subtitleFiles.title') }}
                </h2>
                <span class="mono text-secondary-content ml-auto text-xs">
                    {{ t('mediaDetail.subtitleFiles.count', { count: subtitleFiles.length }) }}
                </span>
            </div>
            <div class="shell-scroll min-h-0 flex-1 overflow-y-auto">
                <div
                    v-if="subtitleFiles.length === 0"
                    class="text-secondary-content/60 px-4 py-4 text-center text-xs">
                    {{ t('mediaDetail.subtitleFiles.empty') }}
                </div>
                <div
                    v-for="file in subtitleFiles"
                    :key="file.directory + '/' + file.fileName"
                    class="border-line flex flex-wrap items-center gap-x-2.5 gap-y-1 border-b px-4 py-2 text-[12.5px]"
                    :data-test="'subtitle-file-row'">
                    <span
                        class="mono text-primary-content min-w-0 flex-1 truncate text-[11.5px]"
                        :title="file.directory + '/' + file.fileName">
                        {{ file.fileName }}
                    </span>
                    <span class="flex flex-none gap-1">
                        <span
                            v-for="lang in file.languages"
                            :key="lang"
                            class="mono rounded-[3px] border px-1.5 py-px text-[10.5px] font-bold"
                            :class="
                                lang === 'unknown'
                                    ? 'border-line bg-tertiary text-secondary-content'
                                    : 'border-success/40 bg-success/10 text-success'
                            ">
                            {{ languageLabel(lang) }}
                        </span>
                    </span>
                    <span class="mono text-secondary-content flex-none text-[10.5px] uppercase">
                        {{ file.format }}
                    </span>
                    <span class="mono text-secondary-content flex-none text-[10.5px]">
                        {{ formatBytes(file.bytes) }}
                    </span>
                </div>

                <!-- ═══ 内嵌轨道分区:每轨一行(语言 chip · 标题 · codec);
                     图形轨(PGS/DVB…)标「图形 · 不可翻译」且无动作入口 ═══ -->                <div
                    class="border-line/60 bg-tertiary flex items-center gap-2.5 border-y px-4 py-1.5"
                    data-test="embedded-tracks-section">
                    <h3 class="text-secondary-content text-[11px] font-semibold tracking-wide">
                        {{ t('mediaDetail.subtitleFiles.embedded.title') }}
                    </h3>
                    <span class="mono text-secondary-content/70 ml-auto text-[10.5px]">
                        {{
                            t('mediaDetail.subtitleFiles.embedded.count', {
                                count: embeddedTracks.length
                            })
                        }}
                    </span>
                </div>
                <div
                    v-if="!embeddedScanned"
                    class="text-secondary-content/70 px-4 py-3 text-xs"
                    data-test="embedded-not-scanned">
                    {{ t('mediaDetail.subtitleFiles.embedded.notScanned') }}
                </div>
                <div
                    v-else-if="embeddedTracks.length === 0"
                    class="text-secondary-content/60 px-4 py-3 text-xs"
                    data-test="embedded-empty">
                    {{ t('mediaDetail.subtitleFiles.embedded.empty') }}
                </div>
                <template v-else>
                    <div
                        v-for="track in embeddedTracks"
                        :key="`${track.mediaId}-${track.streamIndex}`"
                        class="border-line flex flex-wrap items-center gap-x-2.5 gap-y-1 border-b px-4 py-2 text-[12.5px] last:border-b-0"
                        data-test="embedded-track-row">
                        <span class="mono text-secondary-content/70 w-8 flex-none text-[10.5px]">
                            #{{ track.streamIndex }}
                        </span>
                        <span
                            class="text-primary-content min-w-0 flex-1 truncate text-[12px]"
                            :title="track.title ?? ''">
                            {{ track.title ?? '—' }}
                        </span>
                        <span class="flex flex-none gap-1">
                            <span
                                class="mono rounded-[3px] border px-1.5 py-px text-[10.5px] font-bold"
                                :class="
                                    track.language
                                        ? 'border-success/40 bg-success/10 text-success'
                                        : 'border-line bg-tertiary text-secondary-content'
                                ">
                                {{
                                    track.language
                                        ? languageLabel(track.language)
                                        : (track.rawLanguage ?? t('mediaDetail.lang.unknown'))
                                }}
                            </span>
                            <span
                                v-if="!track.isTextBased"
                                class="mono border-line/60 bg-tertiary text-secondary-content/70 rounded-[3px] border px-1.5 py-px text-[10.5px]">
                                {{ t('mediaDetail.subtitleFiles.embedded.graphic') }}
                            </span>
                        </span>
                        <span class="mono text-secondary-content flex-none text-[10.5px] uppercase">
                            {{ track.codec }}
                        </span>
                        <!-- F2b: 文本轨 + 已识别语言 + 仍有未覆盖的目标语言 → 翻译入口;
                             图形轨/语言未知/目标全覆盖时不渲染 -->
                        <ContextMenu
                            v-if="embeddedTrackTranslatable(track)"
                            @select="(language) => translateEmbeddedTrack(track, language)">
                            <button
                                type="button"
                                class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded border px-2 py-0.5 text-xs transition-colors"
                                data-test="embedded-translate">
                                {{ t('mediaDetail.subtitleFiles.embedded.translate') }}
                            </button>
                        </ContextMenu>
                    </div>
                </template>
            </div>
        </div>

        <!-- ═══ 翻译历史(请求列表形态;行内详情 → queue-detail) ═══ -->
        <div class="border-line bg-secondary flex min-h-[160px] flex-1 flex-col rounded-md border">
            <div class="border-line flex shrink-0 items-center gap-2.5 border-b px-4 py-3">
                <h2 class="text-[13px] font-semibold">{{ t('mediaDetail.history') }}</h2>
                <span class="mono text-secondary-content ml-auto text-xs">
                    {{ t('mediaDetail.historyCount', { count: history.length }) }}
                </span>
            </div>
            <div
                v-if="history.length === 0"
                class="text-secondary-content/60 px-4 py-5 text-center text-xs">
                {{ t('mediaDetail.noHistory') }}
            </div>
            <div class="shell-scroll min-h-0 flex-1 overflow-y-auto">
                <router-link
                    v-for="request in history"
                    :key="request.id"
                    :to="{ name: 'queue-detail', params: { id: request.id } }"
                    class="border-line hover:bg-tertiary/50 flex cursor-pointer items-center gap-3 border-b px-4 py-2.5 text-[12.5px] last:border-b-0">
                    <div class="min-w-0 flex-1">
                        <b class="block truncate text-[12.5px] font-semibold">
                            {{ request.title }}
                        </b>
                        <span class="text-secondary-content text-[11.5px]">
                            {{ request.sourceLanguage.toUpperCase() }} →
                            {{ request.targetLanguage.toUpperCase() }} ·
                            {{ formatTime(request.createdAt ?? request.updatedAt ?? '') }}
                        </span>
                    </div>
                    <span v-if="request.serviceType" :class="chipClass()">
                        {{ request.serviceType }}
                    </span>
                    <span :class="historyStatusClass(request)">
                        {{ historyStatusLabel(request) }}
                    </span>
                </router-link>
            </div>
        </div>
    </DrillDownPage>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import axios from 'axios'
import ContextMenu from '@/cells/app/layout/ContextMenu.vue'
import JobRow from '@/cells/app/components/JobRow.vue'
import DrillDownPage from '@/cells/app/components/DrillDownPage.vue'
import { mediaService } from '@/cells/app/api/mediaService'
import { subtitleService } from '@/cells/library/services/subtitleService'
import { translationRequestService } from '@/cells/app/api/translationRequestService'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { useTranslateStore } from '@/cells/app/stores/translate'
import { useUiStore } from '@/cells/app/stores/ui'
import {
    ILanguage,
    MEDIA_TYPE,
    IMediaEmbeddedTrack,
    IMediaLibraryItem,
    IMediaSubtitlesResponse,
    ISeason,
    IShow,
    ISubtitle,
    ITranslationRequest,
    IPagedResult,
    SETTINGS,
    TRANSLATION_STATUS,
    TranslationStatus
} from '@/types'

interface EpisodeRow {
    id: number
    episodeNumber: number
    title: string
    fileName: string
    season: ISeason
}

const props = defineProps<{ type: 'show' | 'movie'; id: string }>()

const { t, locale } = useI18n()
const router = useRouter()
const settingStore = useSettingStore()
const toastStore = useToastStore()
const uiStore = useUiStore()
const translateStore = useTranslateStore()
const translationRequestApi = translationRequestService(axios)
const mediaApi = mediaService(axios)
const subtitleApi = subtitleService(axios)

const item = ref<IMediaLibraryItem | null>(null)
const history = ref<ITranslationRequest[]>([])
const requestsByEpisode = ref(new Map<number, ITranslationRequest>())
const seasonSubtitles = ref<ISubtitle[]>([])
/// One payload backs both sections of the「字幕文件」card: external files
/// and the ffprobe-embedded tracks of the last scan (null until loaded).
const subtitlePayload = ref<IMediaSubtitlesResponse | null>(null)
const subtitleFiles = computed(() => subtitlePayload.value?.files ?? [])
const embeddedTracks = computed(() => subtitlePayload.value?.embedded ?? [])
const embeddedScanned = computed(() => subtitlePayload.value?.embeddedScanned ?? false)
const activeSeason = ref<number | null>(null)
const isShow = computed(() => props.type === 'show')
const show = computed<IShow | null>(() => item.value?.show ?? null)
const seasons = computed<ISeason[]>(() => show.value?.seasons ?? [])
const episodes = computed<EpisodeRow[]>(() => {
    const season = seasons.value.find((s) => s.seasonNumber === activeSeason.value)
    return (season?.episodes ?? []).map((ep) => ({
        id: ep.id,
        episodeNumber: ep.episodeNumber,
        title: ep.title,
        fileName: ep.fileName ?? '',
        season: season as ISeason
    }))
})

const trackedLanguages = computed<string[]>(() => {
    const langs = new Set<string>()
    for (const track of item.value?.tracks ?? []) {
        langs.add(track.language)
    }
    return [...langs]
})

const firstTargetLanguage = computed<ILanguage | null>(() => {
    const languages = (settingStore.getSetting(SETTINGS.TARGET_LANGUAGES) ?? []) as ILanguage[]
    return languages[0] ?? null
})

const configuredSourceCodes = computed<string[]>(() => {
    const languages = (settingStore.getSetting(SETTINGS.SOURCE_LANGUAGES) ?? []) as ILanguage[]
    return languages.map((language) => language.code.toLowerCase())
})

/// Language pair shown in the movie head: first configured source that the
/// scan found, arrowed at the first configured target. Null when either
/// side cannot be derived from real data.
const movieLanguagePair = computed<string | null>(() => {
    const tracks = (item.value?.tracks ?? []).filter((track) => track.have > 0)
    const target = firstTargetLanguage.value?.code?.toLowerCase()
    if (!target) return null
    const present = tracks.map((track) => track.language.toLowerCase())
    const source =
        configuredSourceCodes.value.find((code) => present.includes(code)) ??
        present.find((code) => code !== target)
    return source ? `${source.toUpperCase()} → ${target.toUpperCase()}` : null
})

/// Episodes still missing the primary target's subtitles — client-side from
/// the coverage scan the library item already carries.
const missingTargetEpisodes = computed<number | null>(() => {
    const target = firstTargetLanguage.value?.code
    if (!target || !item.value || !isShow.value) return null
    const track = item.value.tracks.find(
        (candidate) => candidate.language.toLowerCase() === target.toLowerCase()
    )
    return Math.max(0, item.value.episodeCount - (track?.have ?? 0))
})

const headerMeta = computed(() => {
    if (!item.value) return ''
    if (isShow.value) {
        const missing = missingTargetEpisodes.value
        return (
            t('mediaDetail.showMeta', {
                seasons: item.value.seasonCount,
                episodes: item.value.episodeCount
            }) +
            (missing
                ? ' · ' +
                  t('mediaDetail.missingEpisodes', {
                      count: missing,
                      lang: firstTargetLanguage.value!.code.toUpperCase()
                  })
                : '')
        )
    }
    const pair = movieLanguagePair.value
    return pair ? `${t('mediaDetail.movieMeta')} · ${pair}` : t('mediaDetail.movieMeta')
})

const lastActivityLabel = computed(() =>
    item.value?.lastActivityAt ? formatTime(item.value.lastActivityAt) : '—'
)

/// In-flight requests of this media (movie: the request itself; queued ones
/// included — they are the "current tasks" the prototype card summarizes).
const activeRequests = computed<ITranslationRequest[]>(() =>
    history.value
        .filter(
            (request) =>
                request.status === TRANSLATION_STATUS.INPROGRESS ||
                request.status === TRANSLATION_STATUS.PENDING
        )
        .sort(
            (a, b) =>
                Number(b.status === TRANSLATION_STATUS.INPROGRESS) -
                Number(a.status === TRANSLATION_STATUS.INPROGRESS)
        )
)

const tasksMeta = computed(() => {
    const requests = activeRequests.value
    if (requests.length === 0) return ''
    if (requests.length === 1) {
        const request = requests[0]
        const meta = `#${request.id}` + (request.serviceType ? ` · ${request.serviceType}` : '')
        return t('mediaDetail.tasksMeta', { meta })
    }
    return t('mediaDetail.tasksCount', { count: requests.length })
})

/// Active request per target language — backs the busy chip variant
/// ("ZH ⏳ 38%") on the movie toolbar.
const activeByLanguage = computed(() => {
    const map = new Map<string, ITranslationRequest>()
    for (const request of activeRequests.value) {
        map.set(request.targetLanguage.toLowerCase(), request)
    }
    return map
})

const movieTargetComplete = computed(() => {
    const target = firstTargetLanguage.value?.code
    if (!target) return false
    return (item.value?.tracks ?? []).some(
        (track) =>
            track.language.toLowerCase() === target.toLowerCase() && track.have >= track.total
    )
})

onMounted(load)
onUnmounted(() => {
    uiStore.clearCta()
    uiStore.setSubOverride('')
})

// live topbar sub mirrors the page header line
watch(
    [item, activeSeason],
    ([value]) => {
        if (!value) return
        uiStore.setSubOverride(headerMeta.value)
    },
    { immediate: true }
)

async function load(): Promise<void> {
    const mediaType = isShow.value ? 'Show' : 'Movie'
    const page = await mediaApi.library<IPagedResult<IMediaLibraryItem>>({
        pageNumber: 1,
        searchQuery: '',
        sortBy: 'Title',
        ascending: true,
        mediaType,
        id: Number(props.id)
    })
    item.value = page.items[0] ?? null
    if (!item.value) return

    // subtitle files living next to the media (movie dir / every season dir)
    // + embedded container tracks from the last ffprobe scan
    try {
        subtitlePayload.value = await mediaApi.mediaSubtitles<IMediaSubtitlesResponse>(
            isShow.value ? MEDIA_TYPE.SHOW : MEDIA_TYPE.MOVIE,
            Number(props.id)
        )
    } catch {
        subtitlePayload.value = null
    }

    // season scan for per-episode language attribution
    if (isShow.value && seasons.value.length > 0) {
        activeSeason.value = seasons.value[0].seasonNumber
        await loadSeasonSubtitles(seasons.value[0])
    }

    // requests: per-episode (shows) or the movie itself
    const ids = isShow.value
        ? seasons.value.flatMap((s) => s.episodes.map((e) => e.id))
        : [item.value.id]
    if (ids.length > 0) {
        const historyPage = await translationRequestApi.requests<IPagedResult<ITranslationRequest>>(
            1,
            '',
            'CreatedAt',
            false,
            null,
            ids.join(',')
        )
        history.value = historyPage.items
        const latest = new Map<number, ITranslationRequest>()
        for (const request of history.value) {
            if (request.mediaId == null) continue
            const current = latest.get(request.mediaId)
            if (!current || (request.updatedAt ?? '') > (current.updatedAt ?? '')) {
                latest.set(request.mediaId, request)
            }
        }
        requestsByEpisode.value = latest
    }
}

/// 字幕文件行的语言标签:常见码用本地化名,其余回退原码。
function languageLabel(code: string): string {
    const key = `mediaDetail.lang.${code}`
    const label = t(key)
    return label === key ? code : label
}

function formatBytes(bytes: number): string {
    if (!Number.isFinite(bytes) || bytes <= 0) return '—'
    if (bytes < 1024) return `${bytes} B`
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

async function loadSeasonSubtitles(season: ISeason): Promise<void> {
    if (!season.path) {
        seasonSubtitles.value = []
        return
    }
    try {
        seasonSubtitles.value = await subtitleApi.collect<ISubtitle[]>(season.path)
    } catch {
        seasonSubtitles.value = []
    }
}

function episodeSubtitleFiles(ep: EpisodeRow): ISubtitle[] {
    const base = ep.fileName.replace(/\.[^.]+$/, '')
    return seasonSubtitles.value.filter(
        (file) =>
            file.fileName === base ||
            file.fileName.toLowerCase().startsWith(base.toLowerCase() + '.')
    )
}

function episodeLanguages(_ep: EpisodeRow): string[] {
    return trackedLanguages.value
}

function episodeHas(ep: EpisodeRow, lang: string): boolean {
    return episodeSubtitleFiles(ep).some(
        (file) => file.language.toLowerCase() === lang.toLowerCase()
    )
}

function episodeSource(ep: EpisodeRow): ISubtitle | undefined {
    return episodeSubtitleFiles(ep)[0]
}

function episodeRequest(ep: EpisodeRow): ITranslationRequest | null {
    return requestsByEpisode.value.get(ep.id) ?? null
}

/// Episodes with a live or failed request expose it through a「详情」link —
/// the drill-down only exists when the request id is actually known.
const EPISODE_DETAIL_STATUSES: TranslationStatus[] = [
    TRANSLATION_STATUS.INPROGRESS,
    TRANSLATION_STATUS.PENDING,
    TRANSLATION_STATUS.FAILED
]

function episodeDetailRequest(ep: EpisodeRow): ITranslationRequest | null {
    const request = episodeRequest(ep)
    if (!request) return null
    return EPISODE_DETAIL_STATUSES.includes(request.status) ? request : null
}

function episodeStatusClass(ep: EpisodeRow): string {
    const request = episodeRequest(ep)
    if (!request) return 'text-secondary-content/60 text-xs'
    switch (request.status) {
        case TRANSLATION_STATUS.INPROGRESS:
            return 'text-accent text-xs'
        case TRANSLATION_STATUS.COMPLETED:
            return 'text-success text-xs'
        case TRANSLATION_STATUS.FAILED:
            return 'text-danger text-xs'
        case TRANSLATION_STATUS.PENDING:
            return 'text-info text-xs'
        default:
            return 'text-secondary-content text-xs'
    }
}

function episodeStatusLabel(ep: EpisodeRow): string {
    const request = episodeRequest(ep)
    if (!request) return t('library.state.none')
    switch (request.status) {
        case TRANSLATION_STATUS.INPROGRESS:
            return request.progress > 0
                ? t('library.state.inProgressRatio', { progress: request.progress })
                : t('library.state.inProgress')
        case TRANSLATION_STATUS.COMPLETED:
            return t('library.state.completed')
        case TRANSLATION_STATUS.FAILED:
            return t('library.state.failed')
        case TRANSLATION_STATUS.PENDING:
            return t('library.state.queued')
        default:
            return request.status
    }
}

function historyStatusClass(request: ITranslationRequest): string {
    return request.status === TRANSLATION_STATUS.COMPLETED
        ? 'text-success text-xs whitespace-nowrap'
        : request.status === TRANSLATION_STATUS.FAILED
          ? 'text-danger text-xs whitespace-nowrap'
          : 'text-secondary-content text-xs whitespace-nowrap'
}

function historyStatusLabel(request: ITranslationRequest): string {
    // library.state.* 文案本身已带状态符号(✓/✗/◐/⏳),这里不再拼前缀,
    // 否则中英文都会渲染出双符号。
    switch (request.status) {
        case TRANSLATION_STATUS.COMPLETED:
            return t('library.state.completed')
        case TRANSLATION_STATUS.FAILED:
            return t('library.state.failed')
        case TRANSLATION_STATUS.INPROGRESS:
            return t('library.state.inProgress')
        case TRANSLATION_STATUS.PENDING:
            return t('library.state.queued')
        default:
            return request.status
    }
}

async function translateEpisode(ep: EpisodeRow, language: ILanguage): Promise<void> {
    const source = episodeSource(ep)
    if (!source) return
    await translateStore.translateSubtitle(
        ep.id,
        source,
        source.language,
        language,
        MEDIA_TYPE.EPISODE
    )
}

// ── 内嵌轨道翻译(F2b)══════════════════════════════════════

/// Episodes by id across all seasons — show-level embedded rows carry their
/// owning episode's mediaId, this maps it back to the episode's file name.
const episodeById = computed(() => {
    const map = new Map<number, { id: number; fileName?: string | null }>()
    for (const season of seasons.value) {
        for (const episode of season.episodes) {
            map.set(episode.id, episode)
        }
    }
    return map
})

/// Languages the owning media item already covers: external subtitle files
/// plus embedded track languages (the same union the server gates on).
/// Movies own the whole payload; show rows scope external files to the
/// owning episode through base-name matching.
function coveredLanguagesFor(mediaId: number): Set<string> {
    const covered = new Set<string>()
    if (!isShow.value) {
        for (const file of subtitleFiles.value) {
            for (const language of file.languages) covered.add(language.toLowerCase())
        }
        for (const track of embeddedTracks.value) {
            if (track.language) covered.add(track.language.toLowerCase())
        }
        return covered
    }

    const episode = episodeById.value.get(mediaId)
    const base = episode?.fileName
        ? episode.fileName.replace(/\.[^.]+$/, '').toLowerCase()
        : null
    for (const file of subtitleFiles.value) {
        if (base && !file.fileName.toLowerCase().startsWith(base + '.')) continue
        for (const language of file.languages) covered.add(language.toLowerCase())
    }
    for (const track of embeddedTracks.value) {
        if (track.mediaId === mediaId && track.language) {
            covered.add(track.language.toLowerCase())
        }
    }
    return covered
}

/// Entry gating: text track + recognized language + at least one configured
/// target language not covered yet. Graphic tracks and unknown-language
/// tracks never get the entry; the server re-checks on initiation.
function embeddedTrackTranslatable(track: IMediaEmbeddedTrack): boolean {
    if (!track.isTextBased || !track.language) return false
    const covered = coveredLanguagesFor(track.mediaId)
    const targets = (settingStore.getSetting(SETTINGS.TARGET_LANGUAGES) ?? []) as ILanguage[]
    return targets.some((target) => !covered.has(target.code.toLowerCase()))
}

async function translateEmbeddedTrack(
    track: IMediaEmbeddedTrack,
    language: ILanguage
): Promise<void> {
    if (!track.language) return
    try {
        const requestId = await translateStore.translateEmbeddedTrack({
            mediaType: isShow.value ? MEDIA_TYPE.EPISODE : MEDIA_TYPE.MOVIE,
            mediaId: track.mediaId,
            streamIndex: track.streamIndex,
            sourceLanguage: track.language,
            targetLanguage: language.code
        })
        toastStore.push({
            message: t('mediaDetail.subtitleFiles.embedded.queuedToast', { id: requestId }),
            type: 'success'
        })
        await load()
        if (settingStore.getSetting(SETTINGS.NAVIGATE_TO_DETAILS_ON_REQUEST) === 'true') {
            await router.push({ name: 'queue-detail', params: { id: requestId } })
        }
    } catch (error) {
        const detail = axios.isAxiosError(error)
            ? ((error.response?.data as { message?: string } | undefined)?.message ??
              error.message)
            : String(error)
        toastStore.push({
            message: t('mediaDetail.subtitleFiles.embedded.queuedFailed', { message: detail }),
            type: 'error'
        })
    }
}

/// Screen button + topbar CTA (shared action): queue every episode of the
/// active season that still lacks the target-language track, then report
/// the real count and follow the jobs into the queue screen.
async function translateMissing(language: ILanguage): Promise<void> {
    const missing = episodes.value.filter(
        (ep) => episodeSource(ep) && !episodeHas(ep, language.code)
    )
    if (missing.length === 0) {
        toastStore.push({ message: t('mediaDetail.show.noneQueued'), type: 'info' })
        return
    }
    const pair = `${episodeSource(missing[0])!.language.toUpperCase()} → ${language.code.toUpperCase()}`
    for (const ep of missing) {
        await translateEpisode(ep, language)
    }
    toastStore.push({
        message: t('mediaDetail.show.queuedToast', { count: missing.length, pair }),
        type: 'success'
    })
    await router.push({ name: 'queue' })
}

/// Movie counterpart of「翻译缺失」(screen-08): with a job in flight the
/// action reports the real request id and progress; otherwise it creates
/// the translation for the first configured target from the movie's own
/// source subtitle.
async function movieTranslateMissing(): Promise<void> {
    if (!item.value) return
    const busy =
        activeRequests.value.find((request) => request.status === TRANSLATION_STATUS.INPROGRESS) ??
        activeRequests.value[0]
    if (busy) {
        toastStore.push({
            message: t('mediaDetail.movie.busyToast', { id: busy.id, progress: busy.progress }),
            type: 'info'
        })
        return
    }

    const target = firstTargetLanguage.value
    if (!target) {
        toastStore.push({ message: t('mediaDetail.noTarget'), type: 'info' })
        return
    }
    const files = item.value.subtitleFiles ?? []
    const source =
        files.find((file) => configuredSourceCodes.value.includes(file.language.toLowerCase())) ??
        files[0]
    if (!source) {
        toastStore.push({ message: t('mediaDetail.movie.noSource'), type: 'info' })
        return
    }

    const requestId = await translateStore.translateSubtitle(
        item.value.id,
        source,
        source.language,
        target,
        MEDIA_TYPE.MOVIE
    )
    toastStore.push({
        message: t('mediaDetail.movie.queuedToast', { id: requestId }),
        type: 'success'
    })
    await load()
}

// topbar CTA shares the screen button's action: shows queue for the primary
// target directly (the language menu stays on the screen button), movies
// run the busy/create path verbatim.
onMounted(() => {
    uiStore.registerCta(t('mediaDetail.translateMissing'), () => {
        if (isShow.value) {
            const target = firstTargetLanguage.value
            if (!target) {
                toastStore.push({ message: t('mediaDetail.noTarget'), type: 'info' })
                return
            }
            void translateMissing(target)
            return
        }
        void movieTranslateMissing()
    })
})

// ── chips / formatting (matrix vocabulary) ─────────────────────
const thClass =
    'text-secondary-content border-line whitespace-nowrap border-b px-3 py-2 text-left text-xs font-medium'
const tdClass = 'border-line/50 align-middle border-b px-3 py-2'

function chipClass(state?: string): string {
    const base =
        'mono bg-tertiary border-line text-secondary-content inline-flex items-center gap-1 whitespace-nowrap rounded border px-1.5 py-px text-[11px]'
    if (state === 'complete') return base + ' text-success border-success/40'
    if (state === 'partial') return base + ' text-accent border-accent/45'
    if (state === 'busy') return base + ' text-info border-info/45'
    if (state === 'none') return base + ' opacity-55'
    return base
}

/// Movie toolbar chip state: a target with an in-flight request renders the
/// busy variant instead of the static coverage state.
function movieBusyState(track: { language: string; state: string }): string {
    if (!isShow.value && activeByLanguage.value.has(track.language.toLowerCase())) {
        return 'busy'
    }
    return track.state
}

function trackLabel(track: { language: string; have: number; total: number }): string {
    const lang = track.language.toUpperCase()
    const active = activeByLanguage.value.get(track.language.toLowerCase())
    if (!isShow.value && active) {
        return `${lang} ⏳ ${active.progress}%`
    }
    if (track.have >= track.total) return `${lang} ✓`
    if (track.have > 0) return `${lang} ◐ ${track.have}/${track.total}`
    return `${lang} ·`
}

function trackTitle(track: { language: string; have: number; total: number }): string {
    if (track.have >= track.total) return t('library.track.complete', { lang: track.language })
    if (track.have > 0)
        return t('library.track.partial', {
            lang: track.language,
            have: track.have,
            total: track.total
        })
    return t('library.track.none', { lang: track.language })
}

function formatTime(value: string | Date): string {
    return new Date(value).toLocaleTimeString(locale.value, {
        hour: '2-digit',
        minute: '2-digit'
    })
}
</script>
