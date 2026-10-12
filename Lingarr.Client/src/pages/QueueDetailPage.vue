<template>
    <div class="w-full">
        <div class="bg-tertiary flex items-center p-4">
            <ButtonComponent
                variant="ghost"
                size="xs"
                :aria-label="t('common.back')"
                @click="router.push({ name: 'queue' })">
                <Icon name="arrowLeft" class="ml-1 h-3.5 w-3.5" />
            </ButtonComponent>
            <ButtonComponent
                v-if="detail?.status === TRANSLATION_STATUS.COMPLETED"
                variant="secondary"
                size="xs"
                :disabled="converting"
                class="ml-2"
                @click="handleConvert">
                {{
                    converting
                        ? t('translations.detail.converting')
                        : detail?.isBilingual
                          ? t('translations.detail.convertToMonolingual')
                          : t('translations.detail.convertToBilingual')
                }}
            </ButtonComponent>
            <router-link
                v-if="detail?.status === TRANSLATION_STATUS.COMPLETED"
                :to="{ name: 'proofread', params: { id } }"
                class="ml-2">
                <ButtonComponent variant="accent" size="xs">
                    {{ t('proofread.openWorkbench') }}
                </ButtonComponent>
            </router-link>
        </div>

        <div v-if="loading" class="flex justify-center py-12">
            <Icon name="loaderCircle" class="text-primary-content/50 h-8 w-8 animate-spin" />
        </div>

        <div v-else-if="detail" class="grid grid-flow-row auto-rows-max grid-cols-1 gap-4 p-4">
            <CardComponent :title="detail.title">
                <template #content>
                    <div class="grid grid-cols-2 gap-x-6 gap-y-3 md:grid-cols-4">
                        <div>
                            <span class="font-semibold">{{ t('translations.detail.status') }}</span>
                            <div class="mt-1">
                                <TranslationStatus :translation-status="detail.status" />
                            </div>
                        </div>
                        <div>
                            <span class="font-semibold">{{ t('translations.detail.source') }}</span>
                            <div class="mt-1">
                                <BadgeComponent
                                    classes="font-mono text-secondary-content border-line bg-tertiary">
                                    {{ detail.sourceLanguage.toUpperCase() }}
                                </BadgeComponent>
                            </div>
                        </div>
                        <div>
                            <span class="font-semibold">{{ t('translations.detail.target') }}</span>
                            <div class="mt-1">
                                <BadgeComponent
                                    classes="font-mono text-secondary-content border-line bg-tertiary">
                                    {{ detail.targetLanguage.toUpperCase() }}
                                </BadgeComponent>
                            </div>
                        </div>
                        <div v-if="detail.status === TRANSLATION_STATUS.COMPLETED">
                            <span class="font-semibold">{{
                                t('translations.detail.outputForm')
                            }}</span>
                            <div class="mt-1">
                                <BadgeComponent
                                    :classes="
                                        detail.isBilingual
                                            ? 'text-success-content border-success/40 bg-success/50'
                                            : 'text-primary-content/70 border-line bg-secondary'
                                    ">
                                    {{
                                        detail.isBilingual
                                            ? t('translations.bilingual')
                                            : t('translations.monolingual')
                                    }}
                                </BadgeComponent>
                            </div>
                        </div>
                        <div>
                            <span class="font-semibold">
                                {{ t('translations.detail.mediaType') }}
                            </span>
                            <div class="mt-1 text-sm">{{ detail.mediaType }}</div>
                        </div>
                        <div v-if="detail.createdAt">
                            <span class="font-semibold">
                                {{ t('translations.detail.createdAt') }}
                            </span>
                            <div class="mt-1 text-sm">
                                {{ formatDateTime(detail.createdAt) }}
                            </div>
                        </div>
                        <div v-if="detail.completedAt">
                            <span class="font-semibold">
                                {{ t('translations.detail.completed') }}
                            </span>
                            <div class="mt-1 text-sm">
                                {{ formatDateTime(detail.completedAt) }}
                            </div>
                        </div>
                        <div v-if="detail.subtitleToTranslate" class="col-span-2">
                            <span class="font-semibold">
                                {{ t('translations.detail.subtitlePath') }}
                            </span>
                            <div class="mt-1 text-sm break-all">
                                {{ detail.subtitleToTranslate }}
                            </div>
                        </div>
                    </div>
                    <div
                        v-if="detail.errorMessage"
                        class="rounded-md border border-danger/40 bg-danger/15 p-4">
                        <div class="text-sm text-danger">{{ detail.errorMessage }}</div>
                        <details v-if="detail.stackTrace" class="mt-3">
                            <summary
                                class="cursor-pointer text-sm text-danger/70 hover:text-danger">
                                {{ t('translations.detail.stackTrace') }}
                            </summary>
                            <pre
                                class="mt-2 rounded bg-black/30 p-3 text-xs break-all whitespace-pre-wrap text-danger/80"
                                >{{ detail.stackTrace }}</pre>
                        </details>
                    </div>
                    <div v-if="showProgress" class="flex items-center gap-3">
                        <TranslationProgress :progress="displayProgress" />
                        <span class="text-primary-content shrink-0 text-sm font-medium">
                            {{ displayProgress }}%
                        </span>
                    </div>
                </template>
            </CardComponent>

            <CardComponent
                v-if="reversedLines.length > 0"
                :title="t('translations.detail.translatedLines')">
                <template #content>
                    <div class="border-line hidden border-b font-bold md:grid md:grid-cols-12">
                        <div class="col-span-1 px-4 py-2 text-sm">#</div>
                        <div class="col-span-4 px-4 py-2 text-sm">
                            {{ t('translations.detail.source') }}
                        </div>
                        <div class="col-span-5 px-4 py-2 text-sm">
                            {{ t('translations.detail.target') }}
                        </div>
                        <div class="col-span-2 px-4 py-2 text-sm">
                            {{ t('translations.detail.service') }}
                        </div>
                    </div>
                    <div class="max-h-96 overflow-y-auto">
                        <div
                            v-for="line in reversedLines"
                            :key="line.position"
                            class="border-line grid grid-cols-1 gap-1 border-b transition-colors duration-500 last:border-b-0 md:grid-cols-12 md:gap-0"
                            :class="
                                line.position === latestPosition
                                    ? 'bg-accent/10'
                                    : line.position === latestPosition - 1
                                      ? 'bg-accent/5'
                                      : ''
                            ">
                            <div class="text-secondary-content/60 px-4 py-2 font-mono text-xs tabular-nums md:col-span-1">
                                {{ line.position }}
                            </div>
                            <div class="text-primary-content px-4 py-2 text-sm md:col-span-4">
                                {{ line.source }}
                            </div>
                            <div class="text-primary-content px-4 py-2 text-sm md:col-span-5">
                                {{ line.target }}
                            </div>
                            <LineProofreadPanel
                                :line="line"
                                :request-id="detail.id"
                                :source-language="detail.sourceLanguage"
                                :target-language="detail.targetLanguage"
                                :editable="editable"
                                :proofreadable="proofreadable"
                                :close-signal="closeSignals[line.position] ?? 0"
                                @applied="(target: string) => (line.target = target)" />
                        </div>
                    </div>
                </template>
            </CardComponent>
        </div>

        <div v-else class="text-primary-content/50 py-12 text-center">
            {{ t('translations.detail.notFound') }}
        </div>
    </div>
</template>

<script setup lang="ts">
import { reactive, ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import {
    ITranslationRequestDetail,
    IRequestProgress,
    ILineTranslated,
    ILineProofread,
    TRANSLATION_STATUS
} from '@/types'
import { useTranslationDetailHub } from '@/composables/useTranslationDetailHub'
import { formatDateTime } from '@/utils/date'
import services from '@/services'
import useTranslationRequestStore from '@/store/translationRequest'
import TranslationStatus from '@/components/common/TranslationStatus.vue'
import TranslationProgress from '@/components/common/TranslationProgress.vue'
import BadgeComponent from '@/components/common/BadgeComponent.vue'
import ButtonComponent from '@/components/common/ButtonComponent.vue'
import CardComponent from '@/components/common/CardComponent.vue'
import LineProofreadPanel from '@/components/features/translation/LineProofreadPanel.vue'
import Icon from '@/components/icons/Icon.vue'

const props = defineProps<{
    id: string
}>()

const router = useRouter()
const translationRequestStore = useTranslationRequestStore()
const { t } = useI18n()

const detail = ref<ITranslationRequestDetail | null>(null)
const loading = ref(true)
const progress = ref(0)
const latestPosition = ref<number>(0)
// Per-line counters; bumping one tells that line's LineProofreadPanel to close
// (replaces the old delete proofreadState[position] on hub events).
const closeSignals = reactive<Record<number, number>>({})

const reversedLines = computed(() => (detail.value ? [...detail.value.lines].reverse() : []))

const editable = computed(
    () =>
        detail.value?.status === TRANSLATION_STATUS.COMPLETED && !!detail.value?.translatedSubtitle
)

const proofreadable = computed(() => editable.value && translationRequestStore.proofreadSupported)

const converting = ref(false)
const handleConvert = async () => {
    if (!detail.value || converting.value) return
    converting.value = true
    try {
        await services.translationRequest.convert(detail.value.id, !detail.value.isBilingual)
        detail.value = await services.translationRequest.get<ITranslationRequestDetail>(
            Number(props.id)
        )
    } catch (error) {
        console.error('Conversion failed:', error)
    } finally {
        converting.value = false
    }
}

const showProgress = computed(() => {
    if (!detail.value) {
        return false
    }
    const status = detail.value.status
    return (
        (status === TRANSLATION_STATUS.INPROGRESS && progress.value > 0) ||
        status === TRANSLATION_STATUS.COMPLETED
    )
})

const displayProgress = computed(() =>
    detail.value?.status === TRANSLATION_STATUS.COMPLETED ? 100 : progress.value
)

const handleProgress = (requestProgress: IRequestProgress) => {
    if (detail.value && requestProgress.id === detail.value.id) {
        detail.value.status = requestProgress.status
        progress.value = requestProgress.progress
        if (requestProgress.completedAt) {
            detail.value.completedAt = requestProgress.completedAt
        }
        if (requestProgress.errorMessage) {
            detail.value.errorMessage = requestProgress.errorMessage
        }
        if (requestProgress.stackTrace) {
            detail.value.stackTrace = requestProgress.stackTrace
        }
    }
}

const handleLineTranslated = (line: ILineTranslated) => {
    if (detail.value && line.id === detail.value.id) {
        detail.value.lines.push({
            position: line.position,
            source: line.source,
            target: line.target,
            service: line.service ?? null
        })
        latestPosition.value = line.position
    }
}

const handleLineProofread = (line: ILineProofread) => {
    if (detail.value && line.id === detail.value.id) {
        const target = detail.value.lines.find((entry) => entry.position === line.position)
        if (target) {
            target.target = line.target
            if (line.service) {
                target.service = line.service
            }
        }
        closeSignals[line.position] = (closeSignals[line.position] ?? 0) + 1
    }
}

const { connect } = useTranslationDetailHub()

onMounted(async () => {
    try {
        detail.value = await services.translationRequest.get<ITranslationRequestDetail>(
            Number(props.id)
        )
        progress.value = detail.value.progress ?? 0
        if (!detail.value.lines) {
            detail.value.lines = []
        }
    } catch {
        detail.value = null
    } finally {
        loading.value = false
    }

    await translationRequestStore.fetchProofreadStatus()

    await connect({
        onRequestProgress: handleProgress,
        onLineTranslated: handleLineTranslated,
        onLineProofread: handleLineProofread
    })
})
</script>
