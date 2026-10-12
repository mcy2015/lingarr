<template>
    <div class="relative select-none">
        <!-- Context -->
        <TooltipComponent ref="tooltip" alignment="left">
            <button
                :ref="clickOutside"
                type="button"
                class="focus-visible:ring-accent w-full cursor-pointer rounded-md focus-visible:ring-2 focus-visible:outline-none"
                aria-haspopup="menu"
                :aria-expanded="isOpen"
                @click="toggle">
                <slot></slot>
            </button>
        </TooltipComponent>
        <!-- Menu -->
        <div
            v-show="isOpen"
            ref="excludeClickOutside"
            class="border-line bg-primary absolute top-8 right-0 z-10 w-56 rounded-md border shadow-lg"
            role="menu"
            aria-orientation="vertical">
            <div class="text-secondary-content px-3 py-1 text-xs" aria-hidden="true">
                {{ t('common.translateTo') }}
            </div>
            <button
                v-for="language in languages"
                :key="language.code"
                type="button"
                role="menuitem"
                class="hover:bg-tertiary text-secondary-content mb-1 flex w-full cursor-pointer px-3 py-2 text-left text-sm transition-colors focus-visible:outline-none"
                @click="selectOption(language)">
                {{ language.name }}
            </button>
        </div>
    </div>
</template>

<script setup lang="ts">
import { ref, Ref, computed, ComputedRef } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ILanguage, ISubtitle, MediaType, SETTINGS } from '@/types'
import { useSettingStore } from '@/store/setting'
import { useTranslateStore } from '@/store/translate'
import useClickOutside from '@/composables/useClickOutside'
import TooltipComponent from '@/components/common/TooltipComponent.vue'

const router = useRouter()
const { t } = useI18n()
const emit = defineEmits<{
    (e: 'update:toggle'): void
    (e: 'select', language: ILanguage): void
}>()
const { media, subtitle, mediaType } = defineProps<{
    /// only the media id is used to queue the translation
    media?: { id: number }
    subtitle?: ISubtitle
    mediaType?: MediaType
}>()
const settingsStore = useSettingStore()
const translateStore = useTranslateStore()

const tooltip = ref<InstanceType<typeof TooltipComponent> | null>(null)
const isOpen: Ref<boolean> = ref(false)
const clickOutside: Ref = ref(null)
const excludeClickOutside: Ref = ref(null)

const languages: ComputedRef<ILanguage[]> = computed(
    () => settingsStore.getSetting('target_languages') as ILanguage[]
)

const navigateToDetails = computed(
    () => settingsStore.getSetting(SETTINGS.NAVIGATE_TO_DETAILS_ON_REQUEST) === 'true'
)

const toggle = () => {
    emit('update:toggle')
    isOpen.value = !isOpen.value
}

const close = () => {
    isOpen.value = false
}

const selectOption = async (target: ILanguage) => {
    if (media && subtitle && mediaType) {
        const requestId = await translateStore.translateSubtitle(
            media.id,
            subtitle,
            subtitle.language,
            target,
            mediaType
        )
        toggle()
        if (navigateToDetails.value) {
            router.push({ name: 'queue-detail', params: { id: requestId } })
        } else {
            tooltip.value?.showTooltip()
        }
    } else {
        close()
        emit('select', target)
    }
}

useClickOutside(
    clickOutside,
    () => {
        isOpen.value = false
    },
    excludeClickOutside
)

defineExpose({ close, open: () => (isOpen.value = true) })
</script>
