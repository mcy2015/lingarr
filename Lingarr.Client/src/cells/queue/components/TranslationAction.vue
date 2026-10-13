<template>
    <div class="flex items-center gap-2">
        <button
            class="hover:text-primary-content/50 text-primary-content cursor-pointer transition-colors"
            @click="router.push({ name: 'queue-detail', params: { id: item.id } })">
            <Icon name="eyeOn" class="h-5 w-5" />
        </button>
        <div class="flex w-20 items-center gap-2">
            <Icon v-if="loading" name="loaderCircle" class="h-5 w-5 animate-spin" />
            <button
                v-else-if="inProgress"
                :title="t('common.translationAction.pause')"
                class="hover:text-primary-content/50 text-primary-content cursor-pointer transition-colors"
                @click="executeAction(TRANSLATION_ACTIONS.PAUSE)">
                <Icon name="pause" class="h-5 w-5" />
            </button>
            <button
                v-else-if="item.status === TRANSLATION_STATUS.PAUSED"
                :title="t('common.translationAction.resumePaused')"
                class="hover:text-primary-content/50 text-primary-content cursor-pointer transition-colors"
                @click="executeAction(TRANSLATION_ACTIONS.RESUME_PAUSED)">
                <Icon name="start" class="h-5 w-5" />
            </button>
            <template v-else-if="removable">
                <button
                    v-if="resumable"
                    :disabled="loading"
                    :title="t('common.translationAction.resume')"
                    class="hover:text-primary-content/50 text-primary-content cursor-pointer transition-colors"
                    @click="executeAction(TRANSLATION_ACTIONS.RESUME)">
                    <Icon name="start" class="h-5 w-5" />
                </button>
                <button
                    v-if="proofreadable"
                    :disabled="loading"
                    :title="t('common.translationAction.proofread')"
                    class="hover:text-primary-content/50 text-primary-content cursor-pointer transition-colors"
                    @click="executeAction(TRANSLATION_ACTIONS.PROOFREAD)">
                    <Icon name="search" class="h-5 w-5" />
                </button>
                <button
                    :disabled="loading"
                    :title="t('common.translationAction.retry')"
                    class="hover:text-primary-content/50 text-primary-content cursor-pointer transition-colors"
                    @click="executeAction(TRANSLATION_ACTIONS.RETRY)">
                    <Icon name="retry" class="h-5 w-5" />
                </button>
                <button
                    :disabled="loading"
                    class="hover:text-primary-content/50 text-primary-content cursor-pointer transition-colors"
                    @click="executeAction(TRANSLATION_ACTIONS.REMOVE)">
                    <Icon name="trash" class="h-5 w-5" />
                </button>
            </template>
        </div>
    </div>
</template>

<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ITranslationRequest, TRANSLATION_ACTIONS, TRANSLATION_STATUS } from '@/types'
import useTranslationRequestStore from '@/cells/app/stores/translationRequest'
import Icon from '@/cells/kernel/components/icons/Icon.vue'
const router = useRouter()
const translationRequestStore = useTranslationRequestStore()
const { t } = useI18n()

const props = defineProps<{
    item: ITranslationRequest
    onAction: (action: TRANSLATION_ACTIONS) => Promise<void>
}>()

const loading = ref(false)

const inProgress = computed(
    () =>
        props.item.status === TRANSLATION_STATUS.INPROGRESS ||
        props.item.status === TRANSLATION_STATUS.PENDING
)

const removable = computed(
    () =>
        props.item.status !== TRANSLATION_STATUS.PENDING &&
        props.item.status !== TRANSLATION_STATUS.INPROGRESS
)

const resumable = computed(
    () =>
        props.item.status === TRANSLATION_STATUS.FAILED ||
        props.item.status === TRANSLATION_STATUS.CANCELLED ||
        props.item.status === TRANSLATION_STATUS.INTERRUPTED
)

const proofreadable = computed(
    () =>
        props.item.status === TRANSLATION_STATUS.COMPLETED &&
        !!props.item.translatedSubtitle &&
        translationRequestStore.proofreadSupported
)

const executeAction = async (action: TRANSLATION_ACTIONS) => {
    loading.value = true
    try {
        await props.onAction(action)
    } catch {
        loading.value = false
        return
    }

    if (action == TRANSLATION_ACTIONS.RETRY || action == TRANSLATION_ACTIONS.PROOFREAD) {
        // Special case to set loading to false when onAction is done
        // Retry and Proofread are the only cases that do not reliably change props.status right away
        loading.value = false
    }
}

watch(
    () => props.item.status,
    () => {
        loading.value = false
    }
)
</script>
