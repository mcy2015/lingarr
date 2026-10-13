<template>
    <div class="w-full flex h-full min-h-0 flex-col gap-3.5 overflow-hidden p-4 lg:p-6">
        <!-- ═══ 头部：标题 · 进度 · 双轨图例 ═══ -->
        <div class="flex shrink-0 flex-wrap items-center gap-4">
            <div class="min-w-0">
                <b class="text-[15px] font-semibold">{{ detail?.title ?? t('proofread.loading') }}</b>
                <div class="mono text-secondary-content mt-0.5 text-[11.5px]">
                    {{
                        detail
                            ? t('proofread.subtitle', {
                                  source: detail.sourceLanguage.toUpperCase(),
                                  target: detail.targetLanguage.toUpperCase(),
                                  count: detail.lines.length
                              })
                            : ''
                    }}
                </div>
            </div>
            <div class="prog flex min-w-[220px] flex-1 items-center gap-3">
                <div class="track flex-1">
                    <div class="fill" :style="{ width: progressPct + '%' }"></div>
                    <div class="head" :style="{ left: progressPct + '%' }"></div>
                </div>
                <span class="mono text-secondary-content min-w-[110px] text-right text-xs">
                    {{ t('proofread.progress', { done: approvedCount, total: lines.length }) }}
                </span>
            </div>
            <div class="text-secondary-content flex items-center gap-4 text-[11.5px]">
                <span class="flex items-center gap-1.5">
                    <i class="legend-swatch bg-track-a"></i>
                    {{ t('proofread.legend.source', { lang: detail?.sourceLanguage.toUpperCase() ?? '' }) }}
                </span>
                <span class="flex items-center gap-1.5">
                    <i class="legend-swatch bg-track-b"></i>
                    {{ t('proofread.legend.target', { lang: detail?.targetLanguage.toUpperCase() ?? '' }) }}
                </span>
            </div>
        </div>

        <!-- ═══ 双轨行 ═══ -->
        <div v-if="loading" class="text-secondary-content/60 py-10 text-center text-sm">
            {{ t('common.loading') }}
        </div>
        <div v-else-if="lines.length === 0" class="text-secondary-content/60 py-10 text-center text-sm">
            {{ t('proofread.noLines') }}
        </div>
        <div v-else class="shell-scroll min-h-0 flex-1 space-y-2 overflow-y-auto pr-1">
            <div
                v-for="line in lines"
                :key="line.position"
                :ref="el => setLineRef(line.position, el)"
                class="pline [content-visibility:auto] grid cursor-pointer grid-cols-[150px_minmax(0,1fr)_minmax(0,1fr)_34px] rounded-md border transition-colors"
                :class="[
                    focusedPosition === line.position ? 'border-accent focus' : 'border-line',
                    approved.has(line.position) ? 'appr' : ''
                ]"
                tabindex="0"
                @click="focusLine(line.position)">
                <div class="tc mono border-line bg-tertiary border-r px-3 py-2.5 text-[10.5px] leading-[1.7] whitespace-nowrap text-secondary-content">
                    <span class="text-primary-content">#{{ line.position }}</span>
                    <template v-if="line.startTimeMs != null">
                        <br />
                        {{ formatTimecode(line.startTimeMs) }}<br />
                        → {{ formatTimecode(line.endTimeMs ?? line.startTimeMs) }}
                    </template>
                </div>
                <div class="src border-line relative border-r px-3.5 py-2.5 text-[13px] text-secondary-content">
                    {{ line.source }}
                    <span
                        v-if="glossaryHits(line).length > 0"
                        class="gloss"
                        :title="glossaryTitle(line)">
                        {{ t('proofread.glossaryCount', { count: glossaryHits(line).length }) }}
                    </span>
                </div>
                <div class="dst relative px-3.5 py-2.5 text-[13px]">
                    <template v-if="editingPosition === line.position">
                        <input
                            :ref="el => setEditRef(el)"
                            v-model="draft"
                            type="text"
                            class="border-line bg-primary text-primary-content focus:border-accent w-full rounded border px-2.5 py-1.5 text-[13px] outline-none"
                            :aria-label="t('proofread.editLabel')"
                            @keydown.enter="applyDraft(line)"
                            @keydown.escape="cancelEdit" />
                        <div class="mt-2 flex flex-wrap items-center gap-1.5">
                            <button
                                type="button"
                                class="bg-accent text-accent-content hover:bg-accent/90 cursor-pointer rounded-md px-2.5 py-1 text-xs font-semibold transition-colors"
                                @click="applyDraft(line)">
                                {{ t('proofread.approve') }}
                            </button>
                            <button
                                type="button"
                                class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                                @click="rejectLine(line)">
                                {{ t('proofread.reject') }}
                            </button>
                            <span v-if="glossaryHits(line).length > 0" class="text-warning text-[11.5px]">
                                {{ glossaryTitle(line) }}
                            </span>
                            <span v-if="draft !== line.target" class="text-accent ml-auto text-[11.5px]">
                                {{ t('proofread.unsaved') }}
                            </span>
                        </div>
                    </template>
                    <span v-else class="dst-text">{{ line.target }}</span>
                </div>
                <div
                    class="mark flex items-start justify-center pt-3 text-sm"
                    :title="markTitle(line)">
                    <span v-if="approved.has(line.position)" class="text-success">✓</span>
                    <span v-else-if="rejected.has(line.position)" class="text-danger">✗</span>
                    <span v-else class="text-secondary-content">·</span>
                </div>
            </div>
        </div>

        <!-- ═══ 吸底操作条 ═══ -->
        <div class="border-line bg-secondary flex shrink-0 flex-wrap items-center gap-4 rounded-md border px-4 py-2.5">
            <div class="text-secondary-content flex flex-wrap gap-3.5 text-[11.5px]">
                <span><kbd>J</kbd><kbd>K</kbd> {{ t('proofread.keys.navigate') }}</span>
                <span><kbd>Enter</kbd> {{ t('proofread.keys.approve') }}</span>
                <span><kbd>E</kbd> {{ t('proofread.keys.edit') }}</span>
            </div>
            <span class="flex-1"></span>
            <button
                type="button"
                class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-3 py-1 text-xs transition-colors"
                @click="exportSnapshot">
                {{ t('proofread.export') }}
            </button>
            <button
                type="button"
                class="bg-accent text-accent-content hover:bg-accent/90 cursor-pointer rounded-md px-3 py-1 text-xs font-semibold transition-colors"
                @click="finish">
                {{ t('proofread.finish', { done: approvedCount, total: lines.length }) }}
            </button>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import axios from 'axios'
import services from '@/services'
import useTranslationRequestStore from '@/store/translationRequest'
import { useUiStore } from '@/cells/app/stores/ui'
import { useToastStore } from '@/cells/app/stores/toast'
import { glossaryService } from '@/cells/app/api/glossaryService'
import { useProofreadKeyboard } from '@/cells/proofread/composables/useProofreadKeyboard'
import {
    IPagedResult,
    ITranslationGlossary,
    ITranslationRequest,
    ITranslationRequestDetail,
    ISubtitleLineComparison,
    PROOFREAD_LINE_ORIGIN
} from '@/types'

const props = defineProps<{ id?: string }>()

const { t } = useI18n()
const router = useRouter()
const translationRequestStore = useTranslationRequestStore()
const uiStore = useUiStore()
const toastStore = useToastStore()
const glossaryApi = glossaryService(axios)

const loading = ref(true)
const detail = ref<ITranslationRequestDetail | null>(null)
const glossary = ref<ITranslationGlossary[]>([])

const focusedPosition = ref<number | null>(null)
const editingPosition = ref<number | null>(null)
const draft = ref('')
const approved = ref(new Set<number>())
const rejected = ref(new Set<number>())

const lines = computed<ISubtitleLineComparison[]>(() => detail.value?.lines ?? [])
const approvedCount = computed(() => approved.value.size)
const progressPct = computed(() =>
    lines.value.length === 0 ? 0 : Math.round((approvedCount.value / lines.value.length) * 100)
)

const lineRefs = new Map<number, HTMLElement>()
const editInput = ref<HTMLInputElement | null>(null)

function setLineRef(position: number, el: unknown): void {
    if (el instanceof HTMLElement) {
        lineRefs.set(position, el)
    }
}

function setEditRef(el: unknown): void {
    editInput.value = el instanceof HTMLInputElement ? el : null
}

// topbar CTA (prototype): 暂存 — every line edit already persisted
onMounted(() => {
    uiStore.registerCta(t('topbar.cta.stash'), () => {
        toastStore.push({ message: t('proofread.stashed'), type: 'success' })
    })
})
onUnmounted(() => {
    uiStore.clearCta()
    uiStore.setSubOverride('')
})

// live topbar sub: title + reviewed progress (updates as Enter approves)
watch([approvedCount, () => lines.value.length, () => detail.value?.title], ([done, total, title]) => {
    if (!title) return
    uiStore.setSubOverride(t('topbar.sub.proofreadLive', {
        title: String(title),
        done: Number(done),
        total: Number(total)
    }))
}, { immediate: true })

onMounted(async () => {
    let requestId = Number(props.id)
    if (!Number.isFinite(requestId) || requestId <= 0) {
        // bare /proofread — land on the newest completed request
        const page = await services.translationRequest.requests<IPagedResult<ITranslationRequest>>(
            1,
            '',
            'Id',
            false,
            'Completed'
        )
        const latest = page.items?.[0]
        if (!latest) {
            loading.value = false
            return
        }
        requestId = latest.id
        void router.replace({ name: 'proofread', params: { id: String(requestId) } })
    }

    try {
        detail.value = await services.translationRequest.get<ITranslationRequestDetail>(requestId)
        glossary.value = await glossaryApi.getAll()
        focusLine(lines.value[0]?.position ?? 0)
    } finally {
        loading.value = false
    }
})

// ── focus / keyboard flow ──────────────────────────────────────
function focusLine(position: number): void {
    focusedPosition.value = position
    lineRefs.get(position)?.scrollIntoView({ block: 'nearest' })
}

function step(delta: number): void {
    if (lines.value.length === 0) return
    const positions = lines.value.map((line) => line.position)
    const current = focusedPosition.value ?? positions[0]
    const index = Math.max(0, Math.min(positions.length - 1, positions.indexOf(current) + delta))
    focusLine(positions[index])
}

function startEdit(position: number | null): void {
    if (position == null) return
    focusedPosition.value = position
    const line = lines.value.find((l) => l.position === position)
    if (!line) return
    editingPosition.value = position
    draft.value = line.target
    void nextTick(() => editInput.value?.focus())
}

function cancelEdit(): void {
    editingPosition.value = null
}

async function approveLine(line: ISubtitleLineComparison): Promise<void> {
    approved.value.add(line.position)
    rejected.value.delete(line.position)
    // advance to the next unapproved line — the queue-review rhythm
    const next = lines.value.find((l) => !approved.value.has(l.position) && l.position > line.position)
        ?? lines.value.find((l) => !approved.value.has(l.position))
    if (next) {
        focusLine(next.position)
    }
}

async function applyDraft(line: ISubtitleLineComparison): Promise<void> {
    if (draft.value === line.target) {
        await approveLine(line)
        cancelEdit()
        return
    }
    try {
        await translationRequestStore.applyProofreadLine({
            id: Number(props.id),
            position: line.position,
            target: draft.value,
            origin: PROOFREAD_LINE_ORIGIN.MANUAL
        })
        line.target = draft.value
        await approveLine(line)
        cancelEdit()
    } catch {
        // keep the editor open on failure; the workbench marks nothing saved
    }
}

function rejectLine(line: ISubtitleLineComparison): void {
    rejected.value.add(line.position)
    approved.value.delete(line.position)
    cancelEdit()
    step(1)
}

useProofreadKeyboard({
    next: () => step(1),
    prev: () => step(-1),
    approve: () => {
        const line = lines.value.find((l) => l.position === focusedPosition.value)
        if (line && editingPosition.value === null) {
            void approveLine(line)
        }
    },
    edit: () => startEdit(focusedPosition.value)
})

// ── glossary hints ─────────────────────────────────────────────
function glossaryHits(line: ISubtitleLineComparison): ITranslationGlossary[] {
    return glossary.value.filter((entry) =>
        line.source.toLowerCase().includes(entry.sourceTerm.toLowerCase())
    )
}

function glossaryTitle(line: ISubtitleLineComparison): string {
    return glossaryHits(line)
        .map((entry) =>
            entry.isLocked
                ? t('proofread.glossaryLocked', { source: entry.sourceTerm, target: entry.targetTerm })
                : t('proofread.glossaryHint', { source: entry.sourceTerm, target: entry.targetTerm })
        )
        .join(' · ')
}

// ── formatting / export / finish ───────────────────────────────
function formatTimecode(ms: number): string {
    const total = Math.max(0, ms)
    const hours = Math.floor(total / 3_600_000)
    const minutes = Math.floor((total % 3_600_000) / 60_000)
    const seconds = Math.floor((total % 60_000) / 1000)
    const millis = total % 1000
    return `${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')},${String(millis).padStart(3, '0')}`
}

function markTitle(line: ISubtitleLineComparison): string {
    if (approved.value.has(line.position)) {
        return t('proofread.mark.approved')
    }
    if (rejected.value.has(line.position)) {
        return t('proofread.mark.rejected')
    }
    return t('proofread.mark.pending')
}

/// Current texts as an SRT snapshot download — the review artefact before
/// the final write-off.
function exportSnapshot(): void {
    const blocks = lines.value.map((line, index) => {
        const start = line.startTimeMs ?? index * 3000
        const end = line.endTimeMs ?? start + 2500
        return `${index + 1}\n${formatTimecode(start)} --> ${formatTimecode(end)}\n${line.target}\n`
    })
    const blob = new Blob([blocks.join('\n')], { type: 'text/plain;charset=utf-8' })
    const url = URL.createObjectURL(blob)
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = `${(detail.value?.title ?? 'proofread').replace(/[^\w.-]+/g, '_')}.srt`
    anchor.click()
    URL.revokeObjectURL(url)
}

/// Edits already persist per line through applyProofreadLine; finishing is
/// the reviewer's sign-off — back to the queue with a summary.
function finish(): void {
    router.push({ name: 'queue' })
}
</script>

<style scoped>
/* dual-track accents (prototype .pline) */
.src::before,
.dst::before {
    content: '';
    position: absolute;
    left: 0;
    top: 0;
    bottom: 0;
    width: 2px;
}
.src::before {
    background: var(--track-a);
}
.dst::before {
    background: var(--track-b);
}
.pline.appr .dst {
    opacity: 0.9;
}
.legend-swatch {
    width: 10px;
    height: 10px;
    border-radius: 2px;
    display: inline-block;
}
.gloss {
    display: inline-block;
    font-family: var(--font-mono, monospace);
    font-size: 10.5px;
    border: 1px dashed color-mix(in srgb, var(--warning) 60%, var(--line));
    color: var(--warning);
    border-radius: 3px;
    padding: 0 5px;
    margin-left: 6px;
    vertical-align: 1px;
}
kbd {
    font-family: var(--font-mono, monospace);
    font-size: 10.5px;
    border: 1px solid var(--line);
    border-radius: 3px;
    padding: 0 5px;
    background: var(--tertiary);
    margin-right: 4px;
}

/* signature track bar (prototype .track) */
.track {
    position: relative;
    height: 6px;
    background: var(--tertiary);
    border-radius: 3px;
}
.track .fill {
    position: absolute;
    inset: 0 auto 0 0;
    background: var(--accent);
    border-radius: 3px;
}
.track .head {
    position: absolute;
    top: -4px;
    width: 2px;
    height: 14px;
    background: var(--accent);
}
</style>
