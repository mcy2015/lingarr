<template>
    <div class="w-full flex h-full min-h-0 flex-col gap-3.5 overflow-hidden p-4 lg:p-6">
        <!-- ═══ 头部：返回 · 标题 · 汇总 chips · 动作 ═══ -->
        <div class="flex shrink-0 flex-wrap items-center gap-3">
            <button
                type="button"
                class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                @click="router.push({ name: 'library' })">
                {{ t('mediaDetail.back') }}
            </button>
            <div class="min-w-0">
                <b class="text-[15px] font-semibold">{{ item?.title ?? t('common.loading') }}</b>
                <div class="mono text-secondary-content mt-0.5 text-[11.5px]">
                    {{ headerMeta }}
                </div>
            </div>
            <div class="flex flex-wrap gap-1">
                <span
                    v-for="track in item?.tracks ?? []"
                    :key="track.language"
                    :class="chipClass(track.state)"
                    :title="trackTitle(track)">
                    {{ trackLabel(track) }}
                </span>
            </div>
            <span class="flex-1"></span>
            <button
                v-if="isShow"
                type="button"
                class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                @click="router.push({ name: 'import', query: { tab: 'season', show: props.id } })">
                {{ t('library.action.seasonImport') }}
            </button>
            <ContextMenu @select="translateMissing">
                <button
                    type="button"
                    class="bg-accent text-accent-content hover:brightness-106 cursor-pointer rounded-md border border-accent px-2.5 py-1 text-xs font-semibold transition-colors">
                    {{ t('mediaDetail.translateMissing') }}
                </button>
            </ContextMenu>
        </div>

        <!-- ═══ 剧集：季段选 + 逐集轨道表 ═══ -->
        <template v-if="isShow">
            <div class="flex shrink-0 flex-wrap items-center gap-2">
                <div class="border-line inline-flex overflow-hidden rounded-md border" role="group" :aria-label="t('mediaDetail.seasons')">
                    <button
                        v-for="season in seasons"
                        :key="season.seasonNumber"
                        type="button"
                        class="border-line text-secondary-content hover:text-primary-content cursor-pointer border-r px-3 py-1.5 text-[12.5px] transition-colors last:border-r-0"
                        :class="activeSeason === season.seasonNumber ? 'bg-tertiary text-primary-content' : ''"
                        :aria-pressed="activeSeason === season.seasonNumber"
                        @click="activeSeason = season.seasonNumber">
                        S{{ String(season.seasonNumber).padStart(2, '0') }}
                    </button>
                </div>
                <span class="mono text-secondary-content ml-auto text-xs">
                    {{ t('mediaDetail.lastActivity', { time: lastActivityLabel }) }}
                </span>
            </div>

            <div class="border-line bg-secondary flex min-h-0 flex-[1.5] flex-col rounded-md border">
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
                                <span class="text-secondary-content/60 text-xs">{{ t('mediaDetail.noEpisodes') }}</span>
                            </td>
                        </tr>
                        <tr v-for="ep in episodes" :key="ep.id" class="hover:bg-tertiary/50">
                            <td :class="tdClass">
                                <span class="mono text-secondary-content text-[11px]">
                                    E{{ String(ep.episodeNumber).padStart(2, '0') }}
                                </span>
                            </td>
                            <td :class="tdClass">
                                <b class="truncate font-semibold" :title="ep.title">{{ ep.title }}</b>
                            </td>
                            <td :class="tdClass">
                                <div class="flex flex-wrap gap-1">
                                    <span
                                        v-for="lang in episodeLanguages(ep)"
                                        :key="lang"
                                        :class="chipClass(episodeHas(ep, lang) ? 'complete' : 'none')">
                                        {{ lang.toUpperCase() }} {{ episodeHas(ep, lang) ? '✓' : '·' }}
                                    </span>
                                </div>
                            </td>
                            <td :class="tdClass">
                                <span :class="episodeStatusClass(ep)">{{ episodeStatusLabel(ep) }}</span>
                            </td>
                            <td :class="tdClass">
                                <time class="mono text-secondary-content text-xs">
                                    {{ episodeRequest(ep) ? formatTime(episodeRequest(ep)!.updatedAt ?? '') : '—' }}
                                </time>
                            </td>
                            <td :class="tdClass">
                                <div class="flex items-center justify-end gap-1.5 whitespace-nowrap">
                                    <router-link
                                        v-if="episodeRequest(ep)?.status === TRANSLATION_STATUS.COMPLETED"
                                        :to="{ name: 'proofread', params: { id: episodeRequest(ep)!.id } }">
                                        <button
                                            type="button"
                                            class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded border px-2 py-0.5 text-xs transition-colors">
                                            {{ t('common.translationAction.proofread') }}
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

        <!-- ═══ 电影：字幕轨 chips 列表 ═══ -->
        <div v-else class="border-line bg-secondary flex min-h-0 flex-1 flex-col rounded-md border">
            <div class="border-line flex shrink-0 items-center gap-2.5 border-b px-4 py-3">
                <h2 class="text-[13px] font-semibold">{{ t('library.detail.subtitles') }}</h2>
                <span class="mono text-secondary-content ml-auto text-xs">
                    {{ item?.subtitleFiles?.length ?? 0 }}
                </span>
            </div>
            <div v-if="!item?.subtitleFiles?.length" class="text-secondary-content/60 px-4 py-5 text-center text-xs">
                {{ t('mediaDetail.noSubtitles') }}
            </div>
            <div class="shell-scroll min-h-0 flex-1 overflow-y-auto">
            <div
                v-for="file in item?.subtitleFiles ?? []"
                :key="file.path"
                class="border-line flex items-center gap-3 border-b px-4 py-2 text-[12.5px] last:border-b-0">
                <span :class="chipClass()">{{ file.language.toUpperCase() }}</span>
                <span class="mono text-secondary-content truncate text-xs">{{ file.fileName }}{{ file.format }}</span>
            </div>
            </div>
        </div>

        <!-- ═══ 翻译历史 ═══ -->
        <div class="border-line bg-secondary flex min-h-[160px] flex-1 flex-col rounded-md border">
            <div class="border-line flex shrink-0 items-center gap-2.5 border-b px-4 py-3">
                <h2 class="text-[13px] font-semibold">{{ t('mediaDetail.history') }}</h2>
                <span class="mono text-secondary-content ml-auto text-xs">
                    {{ t('mediaDetail.historyCount', { count: history.length }) }}
                </span>
            </div>
            <div v-if="history.length === 0" class="text-secondary-content/60 px-4 py-5 text-center text-xs">
                {{ t('mediaDetail.noHistory') }}
            </div>
            <div class="shell-scroll min-h-0 flex-1 overflow-y-auto">
            <router-link
            v-for="request in history"
                :key="request.id"
                :to="{ name: 'queue-detail', params: { id: request.id } }"
                class="border-line hover:bg-tertiary/50 flex cursor-pointer items-center gap-3 border-b px-4 py-2.5 text-[12.5px] last:border-b-0">
                <div class="min-w-0 flex-1">
                    <b class="block truncate text-[12.5px] font-semibold">{{ request.title }}</b>
                    <span class="text-secondary-content text-[11.5px]">
                        {{ request.sourceLanguage.toUpperCase() }} → {{ request.targetLanguage.toUpperCase() }}
                        · {{ formatTime(request.createdAt ?? request.updatedAt ?? '') }}
                    </span>
                </div>
                <span v-if="request.serviceType" :class="chipClass()">
                    {{ request.serviceType }}
                </span>
                <span :class="historyStatusClass(request)">{{ historyStatusLabel(request) }}</span>
            </router-link>
            </div>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import axios from 'axios'
import ContextMenu from '@/components/layout/ContextMenu.vue'
import services from '@/services'
import { translationRequestService } from '@/cells/app/api/translationRequestService'
import { useUiStore } from '@/cells/app/stores/ui'
import { useTranslateStore } from '@/cells/app/stores/translate'
import {
    ILanguage,
    MEDIA_TYPE,
    IMediaLibraryItem,
    ISeason,
    IShow,
    ISubtitle,
    ITranslationRequest,
    IPagedResult,
    TRANSLATION_STATUS
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
const uiStore = useUiStore()
const translateStore = useTranslateStore()
const translationRequestApi = translationRequestService(axios)

const item = ref<IMediaLibraryItem | null>(null)
const history = ref<ITranslationRequest[]>([])
const requestsByEpisode = ref(new Map<number, ITranslationRequest>())
const seasonSubtitles = ref<ISubtitle[]>([])
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

const headerMeta = computed(() => {
    if (!item.value) return ''
    if (isShow.value) {
        return t('mediaDetail.showMeta', {
            seasons: item.value.seasonCount,
            episodes: item.value.episodeCount
        })
    }
    return t('mediaDetail.movieMeta')
})

const lastActivityLabel = computed(() =>
    item.value?.lastActivityAt ? formatTime(item.value.lastActivityAt) : '—'
)

onMounted(load)
onUnmounted(() => {
    uiStore.clearCta()
    uiStore.setSubOverride('')
})

// live topbar sub mirrors the page header line
watch([item, activeSeason], ([value]) => {
    if (!value) return
    uiStore.setSubOverride(
        value.mediaType === 'Show'
            ? t('topbar.sub.mediaDetailLive', {
                  title: value.title,
                  seasons: value.seasonCount,
                  episodes: value.episodeCount
              })
            : t('mediaDetail.movieMeta')
    )
}, { immediate: true })

async function load(): Promise<void> {
    const mediaType = isShow.value ? 'Show' : 'Movie'
    const page = await services.media.library<IPagedResult<IMediaLibraryItem>>({
        pageNumber: 1,
        searchQuery: '',
        sortBy: 'Title',
        ascending: true,
        mediaType,
        id: Number(props.id)
    })
    item.value = page.items[0] ?? null
    if (!item.value) return

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

async function loadSeasonSubtitles(season: ISeason): Promise<void> {
    if (!season.path) {
        seasonSubtitles.value = []
        return
    }
    try {
        seasonSubtitles.value = await services.subtitle.collect<ISubtitle[]>(season.path)
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
            return t('library.state.inProgress')
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
    switch (request.status) {
        case TRANSLATION_STATUS.COMPLETED:
            return `✓ ${t('library.state.completed')}`
        case TRANSLATION_STATUS.FAILED:
            return `✗ ${t('library.state.failed')}`
        case TRANSLATION_STATUS.INPROGRESS:
            return `◐ ${t('library.state.inProgress')}`
        case TRANSLATION_STATUS.PENDING:
            return `⏳ ${t('library.state.queued')}`
        default:
            return request.status
    }
}

async function translateEpisode(ep: EpisodeRow, language: ILanguage): Promise<void> {
    const source = episodeSource(ep)
    if (!source) return
    await translateStore.translateSubtitle(ep.id, source, source.language, language, MEDIA_TYPE.EPISODE)
}

/// Header CTA: queue every episode that still lacks a target-language track
/// (needs a source subtitle to translate from).
async function translateMissing(language: ILanguage): Promise<void> {
    if (!isShow.value) return
    const missing = episodes.value.filter(
        (ep) => episodeSource(ep) && !episodeHas(ep, language.code)
    )
    for (const ep of missing) {
        await translateEpisode(ep, language)
    }
}

// topbar CTA mirrors the header button
onMounted(() => {
    uiStore.registerCta(t('mediaDetail.translateMissing'), () => {
        /* language menu opens from the header button; the CTA scrolls to it */
        document.querySelector('main button.bg-accent')?.scrollIntoView({ block: 'center' })
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
    if (state === 'none') return base + ' opacity-55'
    return base
}

function trackLabel(track: { language: string; have: number; total: number }): string {
    const lang = track.language.toUpperCase()
    if (track.have >= track.total) return `${lang} ✓`
    if (track.have > 0) return `${lang} ◐ ${track.have}/${track.total}`
    return `${lang} ·`
}

function trackTitle(track: { language: string; have: number; total: number }): string {
    if (track.have >= track.total) return t('library.track.complete', { lang: track.language })
    if (track.have > 0)
        return t('library.track.partial', { lang: track.language, have: track.have, total: track.total })
    return t('library.track.none', { lang: track.language })
}

function formatTime(value: string | Date): string {
    return new Date(value).toLocaleTimeString(locale.value, {
        hour: '2-digit',
        minute: '2-digit'
    })
}
</script>
