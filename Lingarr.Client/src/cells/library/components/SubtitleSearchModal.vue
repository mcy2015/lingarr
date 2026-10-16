<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useToastStore } from '@/cells/app/stores/toast'
import { subtitleSearchService } from '@/cells/app/api/subtitleSearchService'
import ButtonComponent from '@/cells/kernel/components/ButtonComponent.vue'
import type { ISubtitleCandidate, ISubtitleSearchOutcome } from '@/types/services'

const props = defineProps<{
    mediaType: 'Movie' | 'Episode' | 'Show'
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
const outcome = ref<ISubtitleSearchOutcome | null>(null)
const searchElapsed = ref(0)
const downloadBusy = ref<Set<string>>(new Set())

interface SourceStatus {
    provider: string
    status: 'searching' | 'ok' | 'skipped' | 'error'
    message?: string
    count?: number
}
const sourceStatuses = ref<Map<string, SourceStatus>>(new Map())

const sortedCandidates = computed(() => outcome.value?.candidates ?? [])

const targetLang = computed(() =>
    languages.value.find(l => l === 'zh') ? 'zh' : languages.value[0] ?? 'en'
)

// ── search ──────────────────────────────────────────────────
async function runSearch(): Promise<void> {
    if (searching.value) return
    searching.value = true
    outcome.value = null
    sourceStatuses.value = new Map()
    const start = performance.now()

    try {
        const result = await subtitleSearchService.search({
            mediaType: props.mediaType,
            mediaId: props.mediaId,
            languages: languages.value,
        })
        outcome.value = result
        searchElapsed.value = ((performance.now() - start) / 1000)
    } catch {
        toast.push({ message: t('subSearch.failed'), type: 'error' })
    } finally {
        searching.value = false
    }
}

// ── download ────────────────────────────────────────────────
async function download(candidate: ISubtitleCandidate, translate: boolean): Promise<void> {
    const key = candidate.provider + ':' + candidate.externalId
    if (downloadBusy.value.has(key)) return
    downloadBusy.value.add(key)

    try {
        const result = await subtitleSearchService.download({
            cacheKey: outcome.value?.cacheKey ?? '',
            candidateIndex: sortedCandidates.value.indexOf(candidate),
            mediaType: props.mediaType,
            mediaId: props.mediaId,
            source: 'Manual',
        })
        if (result.filePath) {
            toast.push({
                message: translate
                    ? t('subSearch.downloadedAndQueued', { file: result.filePath })
                    : t('subSearch.downloaded', { file: result.filePath }),
                type: 'success',
            })
            emit('downloaded', result.filePath)
        }
    } catch {
        toast.push({ message: t('subSearch.downloadFailed'), type: 'error' })
    } finally {
        downloadBusy.value.delete(key)
    }
}

function isTargetLang(candidate: ISubtitleCandidate): boolean {
    return candidate.language.toLowerCase() === targetLang.value
}

function isBusy(candidate: ISubtitleCandidate): boolean {
    return downloadBusy.value.has(candidate.provider + ':' + candidate.externalId)
}

onMounted(() => {
    query.value = props.title + (props.episode ? ` ${props.episode}` : '')
    void runSearch()
})
</script>

<template>
    <Teleport to="body">
        <div
            class="fixed inset-0 z-50 flex items-start justify-center bg-primary/72 pt-[12vh]"
            data-test="subtitle-search-modal"
            @click.self="emit('close')">
            <div
                class="border-line bg-secondary flex max-h-[76vh] w-[min(780px,94vw)] flex-col overflow-hidden rounded-lg shadow-2xl"
                role="dialog"
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
                    <ButtonComponent variant="primary" size="sm" :disabled="searching" @click="runSearch()">
                        {{ t('subSearch.searchAction') }}
                    </ButtonComponent>
                </div>

                <!-- results header -->
                <div class="border-line text-secondary-content flex items-center gap-3 border-b px-4 py-2 text-xs">
                    <span class="mono" data-test="search-meta">
                        {{ t('subSearch.meta', {
                            count: sortedCandidates.length,
                            sources: sourceStatuses.size,
                            seconds: searchElapsed.toFixed(1),
                        }) }}
                    </span>
                </div>

                <!-- results -->
                <div class="shell-scroll min-h-[120px] flex-1 overflow-y-auto">
                    <div
                        v-for="candidate in sortedCandidates"
                        :key="candidate.provider + ':' + candidate.externalId"
                        class="border-line hover:bg-tertiary/50 flex items-center gap-3 border-b px-4 py-2.5 transition-colors"
                        :data-test="'candidate-' + candidate.language">
                        <span
                            class="w-0.5 shrink-0 self-stretch rounded"
                            :class="isTargetLang(candidate) ? 'bg-success' : 'bg-info'" />
                        <div class="min-w-0 flex-1">
                            <div
                                class="text-primary-content mono truncate text-xs font-medium"
                                :title="candidate.release">
                                {{ candidate.release }}
                            </div>
                            <div class="text-secondary-content text-[11px]">
                                {{ candidate.uploader ?? '—' }} · {{ candidate.format }} · {{ candidate.sizeLabel ?? '—' }}
                            </div>
                        </div>
                        <span class="flex flex-wrap gap-1">
                            <span
                                class="mono rounded px-1.5 py-px text-[10px]"
                                :class="isTargetLang(candidate)
                                    ? 'border-success/40 text-success border'
                                    : 'border-line text-secondary-content border'">
                                {{ isTargetLang(candidate) ? candidate.language.toUpperCase() + ' · ' + t('subSearch.noTranslate') : candidate.language.toUpperCase() }}
                            </span>
                            <span v-if="candidate.hashMatch" class="mono border-success/40 text-success rounded border px-1.5 py-px text-[10px]">
                                hash ✓
                            </span>
                            <span v-if="candidate.forced" class="mono border-line text-secondary-content rounded border px-1.5 py-px text-[10px]">forced</span>
                            <span v-if="candidate.hearingImpaired" class="mono border-line text-secondary-content rounded border px-1.5 py-px text-[10px]">HI</span>
                        </span>
                        <span
                            class="mono w-8 text-right text-xs"
                            :class="candidate.matchScore < 75 ? 'text-warning' : candidate.hashMatch ? 'text-success' : ''"
                            :title="candidate.matchScore < 75 ? t('subSearch.lowConfidence') : ''">
                            {{ candidate.matchScore }}
                        </span>
                        <span class="mono border-line text-secondary-content rounded border px-1.5 py-px text-[11px]">
                            {{ candidate.provider }}
                        </span>
                        <span class="flex justify-end gap-1.5">
                            <ButtonComponent
                                v-if="isTargetLang(candidate)"
                                variant="primary"
                                size="sm"
                                :disabled="isBusy(candidate)"
                                @click="download(candidate, false)">
                                {{ isBusy(candidate) ? '…' : t('subSearch.download') }}
                            </ButtonComponent>
                            <template v-else>
                                <ButtonComponent
                                    variant="secondary"
                                    size="sm"
                                    :disabled="isBusy(candidate)"
                                    @click="download(candidate, false)">
                                    {{ t('subSearch.downloadOnly') }}
                                </ButtonComponent>
                                <ButtonComponent
                                    variant="primary"
                                    size="sm"
                                    :disabled="isBusy(candidate)"
                                    @click="download(candidate, true)">
                                    {{ t('subSearch.downloadAndTranslate') }}
                                </ButtonComponent>
                            </template>
                        </span>
                    </div>

                    <!-- states -->
                    <div v-if="searching && !outcome" class="text-secondary-content flex items-center gap-2 px-4 py-6 text-sm">
                        <span class="animate-pulse">●</span> {{ t('subSearch.searching') }}
                    </div>
                    <div v-else-if="outcome && sortedCandidates.length === 0" class="text-secondary-content px-4 py-6 text-center text-sm">
                        {{ t('subSearch.noResults') }}
                    </div>
                </div>
            </div>
        </div>
    </Teleport>
</template>
