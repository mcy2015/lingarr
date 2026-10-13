<template>
    <div
        v-if="translateStore.hasLanguagesError"
        class="border-line bg-primary mb-4 rounded-md border p-4">
        <div class="flex items-start">
            <svg
                xmlns="http://www.w3.org/2000/svg"
                class="text-accent mt-1 mr-2 h-5 w-5"
                viewBox="0 0 20 20"
                fill="currentColor">
                <path
                    fill-rule="evenodd"
                    d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.707 7.293a1 1 0 00-1.414 1.414L8.586 10l-1.293 1.293a1 1 0 101.414 1.414L10 11.414l1.293 1.293a1 1 0 001.414-1.414L11.414 10l1.293-1.293a1 1 0 00-1.414-1.414L10 8.586 8.707 7.293z"
                    clip-rule="evenodd" />
            </svg>
            <div>
                <p class="text-secondary-content font-medium">
                    {{ t('services.sourceAndTarget.errorTitle') }}
                </p>
                <p class="text-secondary-content mt-1 text-sm">
                    {{ t('services.sourceAndTarget.errorMessage') }}
                </p>
                <button
                    class="border-line hover:bg-accent mt-3 cursor-pointer justify-end rounded border px-3 py-2 transition-colors hover:text-primary-content"
                    :disabled="translateStore.isLanguagesLoading"
                    @click="retryLoadLanguages">
                    <span v-if="translateStore.isLanguagesLoading">{{ t('common.loading') }}</span>
                    <span v-else>{{ t('common.retry') }}</span>
                </button>
            </div>
        </div>
    </div>

    <template v-if="!translateStore.hasLanguagesError">
        <div class="flex flex-col space-x-2">
            <span class="font-semibold">{{ t('services.sourceAndTarget.title') }}</span>
            {{ t('services.sourceAndTarget.description') }}
        </div>

        <div v-if="translateStore.isLanguagesLoading" class="py-4">
            <div class="flex items-center space-x-2">
                <Icon name="loaderCircle" class="h-5 w-5" />
                <span>{{ t('common.loading') }}</span>
            </div>
        </div>

        <template v-else>
            <div>
                <span>{{ t('services.sourceAndTarget.sourceLanguages') }}</span>
                <LanguageSelect
                    v-model:selected="sourceLanguages"
                    class="w-full"
                    :options="languages" />
            </div>
            <div>
                <span>{{ t('services.sourceAndTarget.targetLanguages') }}</span>
                <LanguageSelect
                    v-model:selected="targetLanguages"
                    class="w-full"
                    :options="languages" />
            </div>
        </template>
        <p class="text-secondary-content text-sm">
            {{ t('services.sourceAndTarget.fallbackHintPrefix') }}
            <CodeSnippet>en-US</CodeSnippet>
            {{ t('services.sourceAndTarget.fallbackHintMiddle') }}
            <CodeSnippet>en</CodeSnippet>
            {{ t('services.sourceAndTarget.fallbackHintSuffix') }}
        </p>
    </template>
</template>

<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import CodeSnippet from '@/cells/kernel/components/CodeSnippet.vue'
import LanguageSelect from '@/cells/settings/components/LanguageSelect.vue'
import { ILanguage, SETTINGS } from '@/types'
import { useTranslateStore } from '@/cells/app/stores/translate'
import { useSettingStore } from '@/cells/app/stores/setting'
import Icon from '@/cells/kernel/components/icons/Icon.vue'

const settingsStore = useSettingStore()
const translateStore = useTranslateStore()
const { t } = useI18n()
const languages = computed(() => translateStore.getLanguages)
const emit = defineEmits(['save'])

const sourceLanguages = computed({
    get: (): ILanguage[] => settingsStore.getSetting(SETTINGS.SOURCE_LANGUAGES) as ILanguage[],
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.SOURCE_LANGUAGES, newValue, true, true)
        emit('save')
    }
})

const targetLanguages = computed({
    get: (): ILanguage[] => settingsStore.getSetting(SETTINGS.TARGET_LANGUAGES) as ILanguage[],
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.TARGET_LANGUAGES, newValue, true, true)
        emit('save')
    }
})

function retryLoadLanguages() {
    translateStore.setLanguages()
}

onMounted(() => {
    translateStore.setLanguages()
})
</script>
