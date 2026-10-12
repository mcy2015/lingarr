<template>
    <div class="rounded-md border border-line bg-secondary p-4">
        <div class="flex justify-between">
            <div>
                <h3 class="text-secondary-content/70 text-sm font-medium">
                    {{ title }}
                </h3>
                <p class="text-primary-content mt-2 font-mono text-2xl font-bold tabular-nums">
                    {{ formatNumber(total) }}
                </p>
            </div>
            <div class="text-right">
                <h3 class="text-secondary-content/70 text-sm font-medium">
                    {{ t('dashboard.statCard.translated') }}
                </h3>
                <p class="text-accent mt-2 font-mono text-xl font-bold tabular-nums">
                    {{ formatNumber(translated) }}
                </p>
            </div>
        </div>
        <div class="bg-tertiary mt-3 h-1.5 w-full overflow-hidden rounded-sm">
            <div
                class="bg-accent h-full rounded-sm transition-all duration-500"
                :style="{ width: `${calculatePercentage(translated, total)}%` }"></div>
        </div>
    </div>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'

interface Props {
    title: string
    total: number
    translated: number
}

withDefaults(defineProps<Props>(), {
    total: 0,
    translated: 0
})

const { t } = useI18n()

const formatNumber = (num: number): string => {
    return new Intl.NumberFormat().format(num)
}

const calculatePercentage = (value: number, total: number): number => {
    if (total === 0) return 0
    const percentage = (value / total) * 100
    return Math.min(percentage, 100)
}
</script>
