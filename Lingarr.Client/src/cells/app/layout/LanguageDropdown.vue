<template>
    <DropdownComponent ref="dropdown" width="small" :label="t('common.language')">
        <template #button>
            <Icon name="language" class="h-5 w-5" />
        </template>
        <template #content>
            <div
                class="py-1"
                aria-orientation="vertical"
                role="menu"
                aria-labelledby="language-menu">
                <button
                    v-for="locale in SUPPORTED_LOCALES"
                    :key="locale.code"
                    class="hover:bg-tertiary flex w-full cursor-pointer items-center justify-between gap-2 whitespace-nowrap px-4 py-2 text-left text-sm"
                    :class="
                        locale.code === currentLocale
                            ? 'text-accent font-medium'
                            : 'text-secondary-content'
                    "
                    role="menuitem"
                    @click="switchLocale(locale.code)">
                    {{ locale.label }}
                    <Icon v-if="locale.code === currentLocale" name="checkMark" class="inline h-4 w-4" />
                </button>
            </div>
        </template>
    </DropdownComponent>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { setLocale, SUPPORTED_LOCALES, type LocaleCode } from '@/i18n'
import DropdownComponent from '@/cells/kernel/components/DropdownComponent.vue'
import Icon from '@/cells/kernel/components/icons/Icon.vue'

const { t, locale } = useI18n()
const currentLocale = computed(() => locale.value)
const dropdown = ref<InstanceType<typeof DropdownComponent>>()

const switchLocale = (code: LocaleCode) => {
    setLocale(code)
    dropdown.value?.close()
}
</script>
