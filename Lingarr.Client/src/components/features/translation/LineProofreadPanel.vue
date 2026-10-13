<template>
    <!--
        Multi-root fragment: both nodes are direct grid children of the line row.
        The first node is the service cell (md:col-span-2), the second the
        full-width proofread/edit panel (md:col-span-12).
    -->
    <div
        class="text-primary-content/70 flex items-center justify-between gap-2 px-4 py-2 text-xs md:col-span-2">
        <span class="truncate">
            {{ line.service ?? t('translations.detail.unknown') }}
        </span>
        <div class="flex shrink-0 items-center gap-2">
            <button
                v-if="editable"
                :title="t('translations.detail.editThisLine')"
                class="hover:text-primary-content/50 text-primary-content cursor-pointer transition-colors"
                @click="startEdit">
                <Icon name="pen" class="h-4 w-4" />
            </button>
            <button
                v-if="proofreadable"
                :disabled="loading"
                :title="t('translations.detail.proofreadThisLine')"
                class="hover:text-primary-content/50 text-primary-content cursor-pointer transition-colors disabled:cursor-not-allowed disabled:opacity-50"
                @click="requestProofread">
                <Icon v-if="loading" name="loaderCircle" class="h-4 w-4 animate-spin" />
                <Icon v-else name="search" class="h-4 w-4" />
            </button>
        </div>
    </div>
    <div v-if="showPanel" class="bg-accent/5 px-4 py-2 text-sm md:col-span-12">
        <div class="flex flex-col gap-2 md:ml-auto md:w-1/2">
            <template v-if="draft !== undefined">
                <span class="font-semibold">
                    {{ t('translations.detail.editTranslation') }}
                </span>
                <TextAreaComponent
                    class="w-full"
                    :model-value="draft"
                    :rows="2"
                    @update:model-value="(value: string) => (draft = value)" />
                <div class="flex items-center justify-end gap-3">
                    <ButtonComponent variant="ghost" size="xs" @click="applyDraft">
                        <Icon name="checkMark" class="mr-1 h-4 w-4" />
                        {{ t('translations.detail.accept') }}
                    </ButtonComponent>
                    <ButtonComponent variant="ghost" size="xs" @click="dismiss">
                        <Icon name="timesCircle" class="mr-1 h-4 w-4" />
                        {{ t('translations.detail.cancel') }}
                    </ButtonComponent>
                </div>
            </template>
            <template v-else-if="suggestion">
                <span class="font-semibold">
                    {{ t('translations.detail.proofreadSuggestion') }}
                </span>
                <span>{{ suggestion }}</span>
                <div class="flex items-center justify-end gap-3">
                    <ButtonComponent variant="ghost" size="xs" @click="applySuggestion">
                        <Icon name="checkMark" class="mr-1 h-4 w-4" />
                        {{ t('translations.detail.apply') }}
                    </ButtonComponent>
                    <ButtonComponent variant="ghost" size="xs" @click="dismiss">
                        <Icon name="timesCircle" class="mr-1 h-4 w-4" />
                        {{ t('translations.detail.dismiss') }}
                    </ButtonComponent>
                </div>
            </template>
            <span v-else-if="error" class="text-danger">
                {{ error }}
            </span>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
    ISubtitleLineComparison,
    ProofreadLineOrigin,
    PROOFREAD_LINE_ORIGIN
} from '@/types'
import services from '@/services'
import useTranslationRequestStore from '@/store/translationRequest'
import TextAreaComponent from '@/cells/kernel/components/TextAreaComponent.vue'
import ButtonComponent from '@/cells/kernel/components/ButtonComponent.vue'
import Icon from '@/cells/kernel/components/icons/Icon.vue'

const props = withDefaults(
    defineProps<{
        line: ISubtitleLineComparison
        requestId: number
        sourceLanguage: string
        targetLanguage: string
        editable: boolean
        proofreadable: boolean
        /** Increment to force the panel closed (e.g. a LineProofread hub event arrived). */
        closeSignal?: number
    }>(),
    {
        closeSignal: 0
    }
)

const emit = defineEmits<{
    applied: [target: string]
}>()

const { t } = useI18n()
const translationRequestStore = useTranslationRequestStore()

const loading = ref(false)
const suggestion = ref<string | undefined>(undefined)
const draft = ref<string | undefined>(undefined)
const error = ref<string | undefined>(undefined)

const showPanel = computed(
    () => draft.value !== undefined || !!suggestion.value || !!error.value
)

const reset = () => {
    loading.value = false
    suggestion.value = undefined
    draft.value = undefined
    error.value = undefined
}

watch(
    () => props.closeSignal,
    () => reset()
)

function startEdit() {
    suggestion.value = undefined
    error.value = undefined
    draft.value = props.line.target
}

async function requestProofread() {
    loading.value = true
    suggestion.value = undefined
    draft.value = undefined
    error.value = undefined
    try {
        suggestion.value = await services.translate.proofreadLine<string>({
            sourceLine: props.line.source,
            translatedLine: props.line.target,
            sourceLanguage: props.sourceLanguage,
            targetLanguage: props.targetLanguage
        })
    } catch {
        error.value = t('translations.detail.proofreadUnavailable')
    } finally {
        loading.value = false
    }
}

async function apply(target: string, origin: ProofreadLineOrigin) {
    try {
        await translationRequestStore.applyProofreadLine({
            id: props.requestId,
            position: props.line.position,
            target,
            origin
        })
    } catch {
        draft.value = undefined
        suggestion.value = undefined
        error.value = t('translations.detail.couldNotApplyChange')
        return
    }
    emit('applied', target)
    reset()
}

function applyDraft() {
    if (draft.value) {
        apply(draft.value, PROOFREAD_LINE_ORIGIN.MANUAL)
    }
}

function applySuggestion() {
    if (suggestion.value) {
        apply(suggestion.value, PROOFREAD_LINE_ORIGIN.PROOFREAD)
    }
}

function dismiss() {
    reset()
}
</script>
