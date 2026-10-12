<template>
    <div class="space-y-4">
        <TabComponent v-model="activeType" :options="tabs" />

        <ShowLibrarySection v-if="activeType === 'show'" />
        <MovieLibrarySection v-else />
    </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import TabComponent from '@/components/common/TabComponent.vue'
import ShowLibrarySection from '@/components/features/library/ShowLibrarySection.vue'
import MovieLibrarySection from '@/components/features/library/MovieLibrarySection.vue'
import { ITabOption } from '@/types'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()

type LibraryType = 'show' | 'movie'

const activeType = ref<LibraryType>(route.query.type === 'movie' ? 'movie' : 'show')

const tabs = computed<ITabOption[]>(() => [
    { label: t('library.tabs.shows'), value: 'show' },
    { label: t('library.tabs.movies'), value: 'movie' }
])

// keep ?type= in the URL so library tabs are linkable and survive reloads
watch(activeType, (value) => {
    router.replace({ query: { ...route.query, type: value } })
})

watch(
    () => route.query.type,
    (value) => {
        activeType.value = value === 'movie' ? 'movie' : 'show'
    }
)
</script>
