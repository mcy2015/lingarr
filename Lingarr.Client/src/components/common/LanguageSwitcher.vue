<template>
    <div class="flex items-center justify-center gap-2">
        <button
            v-for="locale in SUPPORTED_LOCALES"
            :key="locale.code"
            type="button"
            class="rounded-full border px-2 py-0.5 text-xs transition-colors"
            :class="
                isActive(locale.code)
                    ? 'border-accent bg-accent text-secondary-content'
                    : 'border-accent bg-transparent text-muted hover:brightness-150'
            "
            :aria-pressed="isActive(locale.code)"
            @click="switchLocale(locale.code)">
            {{ locale.label }}
        </button>
    </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { setLocale, SUPPORTED_LOCALES, type LocaleCode } from '@/i18n'

const { locale } = useI18n()
const currentLocale = computed(() => locale.value)

const isActive = (code: LocaleCode) => currentLocale.value === code

const switchLocale = (code: LocaleCode) => {
    setLocale(code)
}
</script>
