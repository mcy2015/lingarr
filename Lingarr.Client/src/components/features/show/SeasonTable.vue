<template>
    <div class="bg-secondary p-4">
        <div
            class="border-secondary bg-primary text-secondary-content grid grid-cols-12 border-b-2 font-bold">
            <div class="col-span-6 px-4 py-2 md:col-span-3">{{ t('shows.season') }}</div>
            <div class="col-span-6 flex justify-between px-4 py-2 md:col-span-8">
                <span>{{ t('shows.episodes') }}</span>
                <span class="hidden md:block">{{ t('shows.include') }}</span>
                <span class="block md:hidden">✓</span>
            </div>
        </div>
        <!-- Seasons -->
        <div
            v-for="season in seasons"
            :key="season.id"
            class="bg-primary text-accent-content text-sm md:text-base">
            <div
                class="grid grid-cols-12 transition-colors"
                :class="{ 'hover:bg-accent/5 cursor-pointer': season.episodes.length }"
                @click="toggleSeason(season)">
                <div class="col-span-6 flex items-center px-4 py-2 select-none md:col-span-3">
                    <CaretButton
                        v-if="season.episodes.length"
                        :is-expanded="expandedSeason?.id !== season.id"
                        class="pr-2" />
                    <div v-else class="w-7" />
                    <span v-if="season.seasonNumber == 0">{{ t('shows.specials') }}</span>
                    <span v-else>
                        {{ t('shows.seasonNumber', { number: season.seasonNumber }) }}
                    </span>
                </div>
                <div class="col-span-6 flex justify-between px-4 py-2 select-none md:col-span-8">
                    <span>
                        {{ t('shows.episodeCount', { count: season.episodes.length }) }}
                    </span>
                    <span @click.stop>
                        <ToggleButton
                            v-model="season.includeInTranslation"
                            size="small"
                            @toggle:update="
                                () =>
                                    showStore.include(
                                        MEDIA_TYPE.SEASON,
                                        season.id,
                                        season.includeInTranslation
                                    )
                            " />
                    </span>
                </div>
            </div>
            <EpisodeTable
                v-if="expandedSeason?.id === season.id"
                :subtitles="subtitles"
                :episodes="season.episodes" />
        </div>
    </div>
</template>

<script setup lang="ts">
import { ref, Ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ISeason, ISubtitle, MEDIA_TYPE } from '@/types'
import EpisodeTable from '@/components/features/show/EpisodeTable.vue'
import CaretButton from '@/components/common/CaretButton.vue'
import ToggleButton from '@/components/common/ToggleButton.vue'
import services from '@/services'
import { useShowStore } from '@/store/show'

defineProps<{
    seasons: ISeason[]
}>()

const showStore = useShowStore()
const { t } = useI18n()
const subtitles: Ref<ISubtitle[]> = ref([])
const expandedSeason: Ref<ISeason | null> = ref(null)

async function toggleSeason(season: ISeason) {
    if (!season.episodes.length) return
    if (expandedSeason.value?.id === season.id) {
        expandedSeason.value = null
        return
    }
    expandedSeason.value = season
    await collectSubtitles()
}

async function collectSubtitles() {
    if (expandedSeason.value?.path) {
        subtitles.value = await services.subtitle.collect(expandedSeason.value.path)
    }
}
</script>
