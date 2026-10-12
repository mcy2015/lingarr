<template>
    <div class="flex grow flex-col items-center justify-center gap-4 p-4">
        <Icon name="mediaEmoji" class="mb-4 h-16 w-16" />
        <h2 class="mb-2 text-xl font-semibold">{{ t('common.noMedia.title') }}</h2>
        <p class="mb-4 max-w-md text-center text-sm">
            {{ t('common.noMedia.description') }}
        </p>
        <div class="text-sm">
            <p class="font-medium">{{ t('common.noMedia.nextSteps') }}</p>
            <ol class="mt-2 list-inside list-decimal text-left">
                <li>{{ t('common.noMedia.step1') }}</li>
                <li>{{ t('common.noMedia.step2') }}</li>
                <li>{{ t('common.noMedia.step3') }}</li>
            </ol>
        </div>
        <ButtonComponent variant="accent" @click="router.push({ name: 'settings-sources' })">
            {{ t('common.noMedia.goToSettings') }}
        </ButtonComponent>
    </div>
</template>
<script setup lang="ts">
import Icon from '@/components/icons/Icon.vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import ButtonComponent from '@/components/common/ButtonComponent.vue'

const router = useRouter()
const { t } = useI18n()
</script>
