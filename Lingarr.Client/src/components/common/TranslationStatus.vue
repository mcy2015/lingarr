<template>
    <span :title="displayStatus">
        {{ displayStatus }}
    </span>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { TranslationStatus, TRANSLATION_STATUS } from '@/ts'

const { t } = useI18n()

const props = defineProps<{
    translationStatus: TranslationStatus
}>()

const displayStatus = computed(() => {
    switch (props.translationStatus) {
        case TRANSLATION_STATUS.PENDING:
            return t('common.translationStatus.pending')
        case TRANSLATION_STATUS.INPROGRESS:
            return t('common.translationStatus.inProgress')
        case TRANSLATION_STATUS.COMPLETED:
            return t('common.translationStatus.completed')
        case TRANSLATION_STATUS.FAILED:
            return t('common.translationStatus.failed')
        case TRANSLATION_STATUS.CANCELLED:
            return t('common.translationStatus.cancelled')
        case TRANSLATION_STATUS.INTERRUPTED:
            return t('common.translationStatus.interrupted')
        default:
            return props.translationStatus
    }
})
</script>
