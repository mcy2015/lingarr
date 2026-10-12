<template>
    <div class="grid grid-cols-1 gap-4 lg:grid-cols-2">
        <CardComponent :title="t('dashboard.mediaOverview')" class="lg:col-span-2">
            <template #content>
                <template v-if="loading">
                    <div class="flex h-64 items-center justify-center">
                        <LoaderCircleIcon class="h-8 w-8 animate-spin" />
                    </div>
                </template>

                <template v-else-if="error">
                    <div class="flex h-64 items-center justify-center text-danger">
                        {{ error }}
                    </div>
                </template>

                <template v-else-if="!statistics">
                    <div class="text-primary-content flex h-64 items-center justify-center">
                        {{ t('dashboard.noStatistics') }}
                    </div>
                </template>

                <template v-else>
                    <div class="grid grid-cols-1 gap-4 md:grid-cols-2">
                        <StatCard
                            :title="t('dashboard.statCard.movies')"
                            :total="statistics.totalMovies"
                            :translated="getTranslationCount(MEDIA_TYPE.MOVIE)" />

                        <StatCard
                            :title="t('dashboard.statCard.tvShows')"
                            :total="statistics.totalEpisodes"
                            :translated="getTranslationCount(MEDIA_TYPE.EPISODE)" />
                    </div>
                </template>
            </template>
        </CardComponent>

        <CardComponent :title="t('dashboard.translationActivity')">
            <template #content>
                <div class="grid grid-cols-1 gap-4 md:grid-cols-2 lg:grid-cols-1 xl:grid-cols-2">
                    <MetricCard
                        :title="t('dashboard.metricCard.linesTranslated')"
                        :value="statistics?.totalLinesTranslated ?? 0" />

                    <MetricCard
                        :title="t('dashboard.metricCard.filesProcessed')"
                        :value="statistics?.totalFilesTranslated ?? 0" />

                    <MetricCard
                        :title="t('dashboard.metricCard.charactersTranslated')"
                        :value="statistics?.totalCharactersTranslated ?? 0"
                        class="xl:col-span-2" />
                </div>

                <div v-if="translationServices.length" class="mt-4">
                    <h3 class="text-primary-content mb-4 text-sm font-medium">
                        {{ t('dashboard.translationServices') }}
                    </h3>
                    <div class="grid grid-cols-2 gap-2 xl:grid-cols-3">
                        <div
                            v-for="[service, count] in translationServices"
                            :key="service"
                            class="bg-primary rounded-sm p-2">
                            <h4 class="text-primary-content/70 text-xs font-medium">
                                {{ formatServiceName(service) }}
                            </h4>
                            <p class="text-primary-content text-lg font-bold">
                                {{ formatNumber(count) }}
                            </p>
                        </div>
                    </div>
                </div>
            </template>
        </CardComponent>

        <CardComponent :title="t('dashboard.languageStatistics')">
            <template #content>
                <div class="h-80">
                    <LanguageChart v-if="dailyStats?.length" :daily-stats="dailyStats" />
                    <div v-else-if="loading" class="flex h-full w-full items-center justify-center">
                        <LoaderCircleIcon class="h-8 w-8 animate-spin" />
                    </div>
                    <div v-else class="flex h-full w-full items-center justify-center text-xs">
                        {{ t('dashboard.noStatistics') }}
                    </div>
                </div>

                <div v-if="subtitleLanguages.length" class="mt-4">
                    <h3 class="text-primary-content mb-2 text-sm font-medium">
                        {{ t('dashboard.availableSubtitles') }}
                    </h3>
                    <div class="grid grid-cols-3 gap-2 md:grid-cols-4 xl:grid-cols-6">
                        <div
                            v-for="[language, count] in subtitleLanguages"
                            :key="language"
                            class="bg-primary rounded-sm p-2">
                            <h4 class="text-primary-content/70 text-xs font-medium">
                                {{ language.toUpperCase() }}
                            </h4>
                            <p class="text-primary-content text-lg font-bold">
                                {{ formatNumber(count) }}
                            </p>
                        </div>
                    </div>
                </div>

                <div class="flex justify-end">
                    <ButtonComponent
                        variant="ghost"
                        size="xs"
                        :disabled="loading || resetting"
                        :loading="resetting"
                        @click="handleResetStatistics">
                        {{ t('dashboard.resetStatistics') }}
                    </ButtonComponent>
                </div>
            </template>
        </CardComponent>
    </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { DailyStatistic, MEDIA_TYPE, Statistics } from '@/ts'
import services from '@/services'
import CardComponent from '@/components/common/CardComponent.vue'
import ButtonComponent from '@/components/common/ButtonComponent.vue'
import LoaderCircleIcon from '@/components/icons/LoaderCircleIcon.vue'
import LanguageChart from './LanguageChart.vue'
import StatCard from './StatCard.vue'
import MetricCard from './MetricCard.vue'

const { t } = useI18n()

const loading = ref(true)
const resetting = ref(false)
const error = ref<string | null>(null)
const statistics = ref<Statistics>()
const dailyStats = ref<DailyStatistic[]>()

const translationServices = computed(() => {
    if (!statistics.value?.translationsByService) return []
    return Object.entries(statistics.value.translationsByService)
})

const subtitleLanguages = computed(() => {
    if (!statistics.value?.subtitlesByLanguage) return []
    return Object.entries(statistics.value.subtitlesByLanguage)
})

const formatNumber = (num: number): string => {
    return num ? new Intl.NumberFormat().format(num) : '0'
}

const formatServiceName = (service: string): string => {
    return service.charAt(0).toUpperCase() + service.slice(1)
}

const getTranslationCount = (type: string): number => {
    return statistics.value?.translationsByMediaType?.[type] || 0
}

const fetchStatistics = async () => {
    try {
        error.value = null
        loading.value = true
        statistics.value = await services.statistics.getStatistics()
    } catch (err: unknown) {
        if (err instanceof Error) {
            error.value = err?.message || t('dashboard.errors.fetchFailed')
        }
        console.error('Error fetching statistics:', err)
    } finally {
        loading.value = false
    }
}

const fetchDailyStats = async () => {
    loading.value = true
    try {
        dailyStats.value = await services.statistics.getDailyStatistics<DailyStatistic[]>()
    } catch (error) {
        console.error('Error fetching daily statistics:', error)
    } finally {
        loading.value = false
    }
}

const handleResetStatistics = async () => {
    if (!confirm(t('dashboard.resetConfirm'))) {
        return
    }

    try {
        resetting.value = true
        error.value = null
        await services.statistics.resetStatistics()
        await fetchDailyStats()
        await fetchStatistics()
    } catch (err: unknown) {
        if (err instanceof Error) {
            error.value = err?.message || t('dashboard.errors.resetFailed')
        }
        console.error('Error resetting statistics:', err)
    } finally {
        resetting.value = false
    }
}

onMounted(async () => {
    await fetchDailyStats()
    await fetchStatistics()
})
</script>
