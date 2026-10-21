<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import axios from 'axios'
import { useToastStore } from '@/cells/app/stores/toast'
import { useTranslateStore } from '@/cells/app/stores/translate'
import { translationRequestService } from '@/cells/app/api/translationRequestService'
import { ApiError } from '@/cells/app/api/apiError'
import { subtitleService } from '@/cells/library/services/subtitleService'
import { MEDIA_TYPE } from '@/types'
import type { ILanguage, ISubtitle, ITranslationRequest } from '@/types'
import type {
    IMediaSubtitleFile,
    ISubtitleShiftResponse,
    ISubtitleVariantResponse,
    ITimelineSyncResponse
} from '@/types/services'

/// B10 subtitle-tools panel: the per-file action center of the media
/// detail page's subtitle card. Five actions, each dispatched to the
/// endpoint that owns it — re-translate (api/translate/file via the
/// translate store), bilingual re-render (api/translation-request/convert),
/// timeline sync + restore (api/timeline-sync, B05), single-file variant
/// conversion and manual ±ms shift (api/subtitle/tools, B10). In-flight
/// states disable the acting row; outcomes go through the toast store.
const props = defineProps<{
    /// The subtitle file row the panel was opened from.
    file: IMediaSubtitleFile
    /// The owning Movie/Episode pair the ownership-checked endpoints need
    /// (the B06 mergeOwner resolution). Null when the file cannot be
    /// attributed to a single media item — path-addressed actions disable.
    owner: { mediaType: 'Movie' | 'Episode'; mediaId: number } | null
    /// The media's effective target languages — the re-translate choices.
    targetLanguages: ILanguage[]
    /// Latest translation request whose written output IS this file (null
    /// when none matches) — the bilingual re-render's input.
    request: ITranslationRequest | null
}>()

const emit = defineEmits<{
    (e: 'close'): void
    /// Emitted after any action that changed the file or the request list —
    /// the page reloads its subtitle payload and history.
    (e: 'done'): void
}>()

const { t } = useI18n()
const toast = useToastStore()
const translateStore = useTranslateStore()
const convertApi = translationRequestService(axios).convert
const subtitleApi = subtitleService(axios)

// ── address + capability gates ─────────────────────────────────
const subtitlePath = computed(() => props.file.directory + '/' + props.file.fileName)

/// The formats every rewrite-capable action round-trips (the B05 rewriter /
/// B01 variant stage / translation chain all gate on the same set).
const REWRITE_FORMATS = new Set(['srt', 'ass', 'ssa', 'vtt'])

const normalizedFormat = computed(() =>
    props.file.format.trim().replace(/^\./, '').toLowerCase()
)
const rewriteSupported = computed(() => REWRITE_FORMATS.has(normalizedFormat.value))

/// A re-translation needs an unambiguous source language: dual-language
/// files (scjp…) and unknown-language rows cannot start one.
const retranslatable = computed(
    () => props.file.languages.length === 1 && props.file.languages[0].toLowerCase() !== 'unknown'
)

const retranslateDisabledTitle = computed(() => {
    if (!props.owner) return t('mediaDetail.b10tools.noOwnerTitle')
    if (!retranslatable.value) return t('mediaDetail.b10tools.retranslate.notTranslatable')
    if (props.targetLanguages.length === 0) return t('mediaDetail.b10tools.retranslate.noTarget')
    return ''
})

const rerenderDisabledTitle = computed(() =>
    props.request ? '' : t('mediaDetail.b10tools.rerender.noRequestTitle')
)

function rewriteDisabledTitle(): string {
    if (!props.owner) return t('mediaDetail.b10tools.noOwnerTitle')
    if (!rewriteSupported.value) return t('mediaDetail.b10tools.formatUnsupportedTitle')
    return ''
}

const timelineDisabledTitle = computed(rewriteDisabledTitle)
const variantDisabledTitle = computed(rewriteDisabledTitle)
const shiftDisabledTitle = computed(rewriteDisabledTitle)

// ── close / focus / a11y (SubtitleSearchModal precedent) ───────
const dialogRef = ref<HTMLElement | null>(null)
let disposed = false

function teardown(): void {
    disposed = true
    window.removeEventListener('keydown', onWindowKeydown)
}

function close(): void {
    teardown()
    emit('close')
}

/// Escape closes from anywhere; Tab cycles inside the dialog.
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
    window.addEventListener('keydown', onWindowKeydown)
    dialogRef.value?.focus()
})

onBeforeUnmount(teardown)

// ── shared action runner (one in-flight action at a time) ──────
const busyAction = ref<string | null>(null)

async function runAction(key: string, action: () => Promise<void>): Promise<void> {
    if (busyAction.value) return
    busyAction.value = key
    try {
        await action()
    } finally {
        busyAction.value = null
    }
}

/// Failure normalization: the axios interceptor already converts rejections
/// to ApiError (message from the backend payload); the fallbacks cover raw
/// axios errors and non-HTTP throws.
function errorDetail(error: unknown): string {
    if (error instanceof ApiError) return error.message
    if (axios.isAxiosError(error)) {
        return (
            (error.response?.data as { message?: string } | undefined)?.message ?? error.message
        )
    }
    return String(error)
}

// ── 1 · re-translate (existing chain: api/translate/file) ──────
/// The first effective target is the pre-picked re-translate target.
const retranslateTarget = ref<ILanguage | null>(props.targetLanguages[0] ?? null)

async function retranslate(): Promise<void> {
    const owner = props.owner
    const target = retranslateTarget.value
    if (!owner || !target || retranslateDisabledTitle.value) return
    const source = props.file.languages[0]
    const subtitle: ISubtitle = {
        path: subtitlePath.value,
        language: source,
        fileName: props.file.fileName,
        format: '.' + normalizedFormat.value,
        caption: ''
    }
    await runAction('retranslate', async () => {
        try {
            const requestId = await translateStore.translateSubtitle(
                owner.mediaId,
                subtitle,
                source,
                target,
                owner.mediaType === 'Movie' ? MEDIA_TYPE.MOVIE : MEDIA_TYPE.EPISODE
            )
            toast.push({
                message: t('mediaDetail.b10tools.retranslate.queuedToast', { id: requestId }),
                type: 'success'
            })
            emit('done')
        } catch (error) {
            toast.push({
                message: t('mediaDetail.b10tools.retranslate.failedToast', {
                    message: errorDetail(error)
                }),
                type: 'error'
            })
        }
    })
}

// ── 2 · bilingual re-render (existing chain: convert endpoint) ─
/// The convert endpoint takes the request id + the target form; the current
/// form rides the matched request's isBilingual flag.
const rerenderTargetBilingual = computed(() => !(props.request?.isBilingual ?? false))

async function rerender(): Promise<void> {
    const request = props.request
    if (!request || rerenderDisabledTitle.value) return
    await runAction('rerender', async () => {
        try {
            const result = await convertApi<{
                success: boolean
                skipped?: boolean
                isBilingual?: boolean
                message?: string
            }>(request.id, rerenderTargetBilingual.value)
            if (result.success && !result.skipped) {
                toast.push({
                    message: t('mediaDetail.b10tools.rerender.doneToast', {
                        form: result.isBilingual
                            ? t('mediaDetail.b10tools.rerender.formBilingual')
                            : t('mediaDetail.b10tools.rerender.formMonolingual')
                    }),
                    type: 'success'
                })
                emit('done')
            } else {
                // success && skipped: already in the target form — honest
                // no-change info, never a fake success.
                toast.push({ message: t('mediaDetail.b10tools.rerender.noChangeToast'), type: 'info' })
            }
        } catch (error) {
            toast.push({
                message: t('mediaDetail.b10tools.rerender.failedToast', {
                    message: errorDetail(error)
                }),
                type: 'error'
            })
        }
    })
}

// ── 3 · timeline sync + restore (B05 endpoints) ────────────────
/// Set once this session learned a .lingarr-tl-bk backup exists (a
/// corrected/already-corrected sync verdict, or a shift that just wrote
/// one) — the restore button appears with it.
const backupKnown = ref(false)

async function timelineSyncAction(): Promise<void> {
    const owner = props.owner
    if (!owner || timelineDisabledTitle.value) return
    await runAction('timeline', async () => {
        try {
            const result = await subtitleApi.timelineSync<ITimelineSyncResponse>({
                mediaType: owner.mediaType,
                mediaId: owner.mediaId,
                subtitlePath: subtitlePath.value
            })
            if (result.status === 'corrected') {
                backupKnown.value = true
                toast.push({
                    message: t('mediaDetail.b10tools.timeline.doneToast', {
                        cues: result.cues,
                        offsetMs: Math.round(result.offsetSeconds * 1000)
                    }),
                    type: 'success'
                })
                emit('done')
            } else if (result.status === 'no-correction-needed') {
                toast.push({ message: t('mediaDetail.b10tools.timeline.noopToast'), type: 'info' })
            } else if (result.status === 'already-corrected') {
                backupKnown.value = true
                toast.push({ message: t('mediaDetail.b10tools.timeline.alreadyToast'), type: 'info' })
            } else {
                // status "failed": the sync RAN — its verdict is data.
                toast.push({
                    message: t('mediaDetail.b10tools.timeline.failedToast', {
                        reason: result.reason ?? result.status
                    }),
                    type: 'error'
                })
            }
        } catch (error) {
            toast.push({
                message: t('mediaDetail.b10tools.timeline.failedToast', {
                    reason: errorDetail(error)
                }),
                type: 'error'
            })
        }
    })
}

async function timelineRestoreAction(): Promise<void> {
    const owner = props.owner
    if (!owner) return
    await runAction('restore', async () => {
        try {
            await subtitleApi.timelineRestore({ mediaType: owner.mediaType, mediaId: owner.mediaId, subtitlePath: subtitlePath.value })
            backupKnown.value = false
            toast.push({ message: t('mediaDetail.b10tools.timeline.restoreDone'), type: 'success' })
            emit('done')
        } catch (error) {
            toast.push({
                message: t('mediaDetail.b10tools.timeline.restoreFailed', {
                    message: errorDetail(error)
                }),
                type: 'error'
            })
        }
    })
}

// ── 4 · Chinese variant (B01 core, single file) ────────────────
const variantMode = ref<'simplified' | 'traditional'>('simplified')

async function convertVariantAction(): Promise<void> {
    const owner = props.owner
    if (!owner || variantDisabledTitle.value) return
    await runAction('variant', async () => {
        try {
            const result = await subtitleApi.convertVariant<ISubtitleVariantResponse>({
                mediaType: owner.mediaType,
                mediaId: owner.mediaId,
                subtitlePath: subtitlePath.value,
                mode: variantMode.value
            })
            if (result.changed) {
                toast.push({
                    message: t('mediaDetail.b10tools.variant.doneToast', {
                        mode:
                            variantMode.value === 'simplified'
                                ? t('mediaDetail.b10tools.variant.simplifiedLabel')
                                : t('mediaDetail.b10tools.variant.traditionalLabel')
                    }),
                    type: 'success'
                })
                emit('done')
            } else {
                // 200 no-change: the batch's skip semantics — info with the
                // server's real reason, never a fake success.
                toast.push({
                    message: t('mediaDetail.b10tools.variant.noChangeToast', {
                        reason: result.reason ?? ''
                    }),
                    type: 'info'
                })
            }
        } catch (error) {
            toast.push({
                message: t('mediaDetail.b10tools.variant.failedToast', {
                    message: errorDetail(error)
                }),
                type: 'error'
            })
        }
    })
}

// ── 5 · manual shift (B10 endpoint, rewriter at R=1) ───────────
const shiftInput = ref('')

async function shiftAction(): Promise<void> {
    const owner = props.owner
    if (!owner || shiftDisabledTitle.value) return
    // v-model on type="number" may deliver a number or a string — normalize
    const offsetMs = Number(String(shiftInput.value ?? '').trim())
    if (!Number.isFinite(offsetMs) || offsetMs === 0) {
        toast.push({ message: t('mediaDetail.b10tools.shift.invalidToast'), type: 'error' })
        return
    }
    await runAction('shift', async () => {
        try {
            const result = await subtitleApi.shiftSubtitle<ISubtitleShiftResponse>({
                mediaType: owner.mediaType,
                mediaId: owner.mediaId,
                subtitlePath: subtitlePath.value,
                offsetMs
            })
            // A shift writes the same backup a correction does — restore
            // undoes it too.
            backupKnown.value = true
            toast.push({
                message: t('mediaDetail.b10tools.shift.doneToast', {
                    ms: offsetMs,
                    cues: result.cues,
                    clamped: result.clampedCues
                }),
                type: 'success'
            })
            emit('done')
        } catch (error) {
            toast.push({
                message: t('mediaDetail.b10tools.shift.failedToast', {
                    message: errorDetail(error)
                }),
                type: 'error'
            })
        }
    })
}
</script>

<template>
    <Teleport to="body">
        <div
            class="fixed inset-0 z-50 flex items-start justify-center bg-primary/72 pt-[10vh]"
            data-test="subtitle-tools-modal"
            @click.self="close()">
            <div
                ref="dialogRef"
                class="border-line bg-secondary shell-scroll max-h-[80vh] w-[min(620px,94vw)] overflow-y-auto rounded-lg shadow-2xl"
                role="dialog"
                aria-modal="true"
                tabindex="-1"
                :aria-label="t('mediaDetail.b10tools.title')">
                <!-- header: the addressed file, verbatim -->
                <div class="border-line flex items-center gap-3 border-b px-4 py-3">
                    <div class="min-w-0 flex-1">
                        <span class="text-sm font-semibold">{{ t('mediaDetail.b10tools.title') }}</span>
                        <div
                            class="mono text-secondary-content mt-0.5 truncate text-[11px]"
                            :title="subtitlePath"
                            data-test="tools-file-name">
                            {{ file.fileName }}
                            <span class="uppercase">{{ normalizedFormat }}</span>
                        </div>
                    </div>
                    <span class="text-secondary-content mono flex-none text-[11px]">
                        {{ t('common.escape') }}
                    </span>
                </div>

                <div class="flex flex-col divide-y divide-line">
                    <!-- 1 · re-translate -->
                    <div class="px-4 py-3" data-test="tools-section-retranslate">
                        <div class="flex items-center gap-2">
                            <b class="text-[12.5px] font-semibold">
                                {{ t('mediaDetail.b10tools.retranslate.title') }}
                            </b>
                            <span class="text-secondary-content text-[11px]">
                                {{ t('mediaDetail.b10tools.retranslate.hint') }}
                            </span>
                        </div>
                        <div class="mt-2 flex flex-wrap items-center gap-1.5">
                            <button
                                v-for="target in targetLanguages"
                                :key="target.code"
                                type="button"
                                class="mono rounded-[3px] border px-1.5 py-px text-[10.5px] font-bold transition-colors disabled:opacity-40"
                                :class="
                                    retranslateTarget?.code.toLowerCase() === target.code.toLowerCase()
                                        ? 'border-accent/60 bg-accent/15 text-accent'
                                        : 'border-line bg-tertiary text-secondary-content hover:text-primary-content'
                                "
                                :aria-pressed="
                                    retranslateTarget?.code.toLowerCase() === target.code.toLowerCase()
                                "
                                :data-test="'tools-retranslate-target-' + target.code"
                                @click="retranslateTarget = target">
                                {{ target.code.toUpperCase() }}
                            </button>
                            <button
                                type="button"
                                class="border-line text-primary-content hover:bg-tertiary ml-auto cursor-pointer rounded border px-2.5 py-0.5 text-xs font-semibold transition-colors disabled:cursor-default disabled:opacity-50"
                                :disabled="retranslateDisabledTitle !== '' || busyAction !== null"
                                :title="retranslateDisabledTitle || t('mediaDetail.b10tools.retranslate.action')"
                                data-test="tools-retranslate-action"
                                @click="retranslate()">
                                {{ busyAction === 'retranslate' ? '…' : t('mediaDetail.b10tools.retranslate.action') }}
                            </button>
                        </div>
                    </div>

                    <!-- 2 · bilingual re-render -->
                    <div class="px-4 py-3" data-test="tools-section-rerender">
                        <div class="flex items-center gap-2">
                            <b class="text-[12.5px] font-semibold">
                                {{ t('mediaDetail.b10tools.rerender.title') }}
                            </b>
                            <span
                                v-if="request"
                                class="mono text-secondary-content text-[11px]"
                                data-test="tools-rerender-current">
                                {{
                                    request.isBilingual
                                        ? t('mediaDetail.b10tools.rerender.currentBi')
                                        : t('mediaDetail.b10tools.rerender.currentMono')
                                }}
                            </span>
                            <button
                                type="button"
                                class="border-line text-primary-content hover:bg-tertiary ml-auto cursor-pointer rounded border px-2.5 py-0.5 text-xs font-semibold transition-colors disabled:cursor-default disabled:opacity-50"
                                :disabled="rerenderDisabledTitle !== '' || busyAction !== null"
                                :title="rerenderDisabledTitle || t('mediaDetail.b10tools.rerender.action')"
                                data-test="tools-rerender-action"
                                @click="rerender()">
                                {{
                                    busyAction === 'rerender'
                                        ? '…'
                                        : rerenderTargetBilingual
                                          ? t('mediaDetail.b10tools.rerender.toBilingual')
                                          : t('mediaDetail.b10tools.rerender.toMono')
                                }}
                            </button>
                        </div>
                        <div v-if="!request" class="text-secondary-content/70 mt-1 text-[11px]">
                            {{ t('mediaDetail.b10tools.rerender.noRequestTitle') }}
                        </div>
                    </div>

                    <!-- 3 · timeline sync (+ restore once a backup is known) -->
                    <div class="px-4 py-3" data-test="tools-section-timeline">
                        <div class="flex items-center gap-2">
                            <b class="text-[12.5px] font-semibold">
                                {{ t('mediaDetail.b10tools.timeline.title') }}
                            </b>
                            <span class="text-secondary-content text-[11px]">
                                {{ t('mediaDetail.b10tools.timeline.hint') }}
                            </span>
                            <span class="ml-auto flex gap-1.5">
                                <button
                                    v-if="backupKnown"
                                    type="button"
                                    class="border-line text-secondary-content hover:bg-tertiary cursor-pointer rounded border px-2.5 py-0.5 text-xs transition-colors disabled:cursor-default disabled:opacity-50"
                                    :disabled="busyAction !== null"
                                    :title="t('mediaDetail.b10tools.timeline.restore')"
                                    data-test="tools-timeline-restore"
                                    @click="timelineRestoreAction()">
                                    {{ busyAction === 'restore' ? '…' : t('mediaDetail.b10tools.timeline.restore') }}
                                </button>
                                <button
                                    type="button"
                                    class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded border px-2.5 py-0.5 text-xs font-semibold transition-colors disabled:cursor-default disabled:opacity-50"
                                    :disabled="timelineDisabledTitle !== '' || busyAction !== null"
                                    :title="timelineDisabledTitle || t('mediaDetail.b10tools.timeline.action')"
                                    data-test="tools-timeline-action"
                                    @click="timelineSyncAction()">
                                    {{ busyAction === 'timeline' ? '…' : t('mediaDetail.b10tools.timeline.action') }}
                                </button>
                            </span>
                        </div>
                    </div>

                    <!-- 4 · Chinese variant -->
                    <div class="px-4 py-3" data-test="tools-section-variant">
                        <div class="flex items-center gap-2">
                            <b class="text-[12.5px] font-semibold">
                                {{ t('mediaDetail.b10tools.variant.title') }}
                            </b>
                            <span class="ml-auto flex flex-wrap items-center gap-1.5">
                                <button
                                    type="button"
                                    class="rounded-[3px] border px-1.5 py-px text-[10.5px] font-bold transition-colors disabled:opacity-40"
                                    :class="
                                        variantMode === 'simplified'
                                            ? 'border-accent/60 bg-accent/15 text-accent'
                                            : 'border-line bg-tertiary text-secondary-content hover:text-primary-content'
                                    "
                                    :aria-pressed="variantMode === 'simplified'"
                                    data-test="tools-variant-mode-simplified"
                                    @click="variantMode = 'simplified'">
                                    {{ t('mediaDetail.b10tools.variant.simplified') }}
                                </button>
                                <button
                                    type="button"
                                    class="rounded-[3px] border px-1.5 py-px text-[10.5px] font-bold transition-colors disabled:opacity-40"
                                    :class="
                                        variantMode === 'traditional'
                                            ? 'border-accent/60 bg-accent/15 text-accent'
                                            : 'border-line bg-tertiary text-secondary-content hover:text-primary-content'
                                    "
                                    :aria-pressed="variantMode === 'traditional'"
                                    data-test="tools-variant-mode-traditional"
                                    @click="variantMode = 'traditional'">
                                    {{ t('mediaDetail.b10tools.variant.traditional') }}
                                </button>
                                <button
                                    type="button"
                                    class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded border px-2.5 py-0.5 text-xs font-semibold transition-colors disabled:cursor-default disabled:opacity-50"
                                    :disabled="variantDisabledTitle !== '' || busyAction !== null"
                                    :title="variantDisabledTitle || t('mediaDetail.b10tools.variant.action')"
                                    data-test="tools-variant-action"
                                    @click="convertVariantAction()">
                                    {{ busyAction === 'variant' ? '…' : t('mediaDetail.b10tools.variant.action') }}
                                </button>
                            </span>
                        </div>
                    </div>

                    <!-- 5 · manual shift -->
                    <div class="px-4 py-3" data-test="tools-section-shift">
                        <div class="flex flex-wrap items-center gap-2">
                            <b class="text-[12.5px] font-semibold">
                                {{ t('mediaDetail.b10tools.shift.title') }}
                            </b>
                            <span class="text-secondary-content text-[11px]">
                                {{ t('mediaDetail.b10tools.shift.hint') }}
                            </span>
                            <span class="ml-auto flex items-center gap-1.5">
                                <input
                                    v-model="shiftInput"
                                    type="number"
                                    step="any"
                                    class="border-line bg-primary text-primary-content mono w-24 rounded border px-2 py-0.5 text-xs outline-none focus:border-accent"
                                    :aria-label="t('mediaDetail.b10tools.shift.label')"
                                    data-test="tools-shift-input" />
                                <span class="text-secondary-content text-[10.5px]">ms</span>
                                <button
                                    type="button"
                                    class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded border px-2.5 py-0.5 text-xs font-semibold transition-colors disabled:cursor-default disabled:opacity-50"
                                    :disabled="shiftDisabledTitle !== '' || busyAction !== null"
                                    :title="shiftDisabledTitle || t('mediaDetail.b10tools.shift.action')"
                                    data-test="tools-shift-action"
                                    @click="shiftAction()">
                                    {{ busyAction === 'shift' ? '…' : t('mediaDetail.b10tools.shift.action') }}
                                </button>
                            </span>
                        </div>
                        <div class="text-secondary-content/60 mt-1 text-[11px]">
                            {{ t('mediaDetail.b10tools.shift.negativeHint') }}
                        </div>
                    </div>
                </div>
            </div>
        </div>
    </Teleport>
</template>
