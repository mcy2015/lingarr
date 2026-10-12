<template>
    <div v-if="settingsCompleted === 'true'" class="w-full">
        <ListToolbar
            v-model:filter="filter"
            center-actions
            :sort-options="[
                {
                    label: t('shows.sortByTitle'),
                    value: 'Title'
                },
                {
                    label: t('shows.sortByAdded'),
                    value: 'DateAdded'
                }
            ]"
            @reload="showStore.fetch()">
            <template #actions>
                <ToggleButton
                    v-model="navigateToDetails"
                    size="small"
                    :label="t('shows.openDetailsOnRequest')" />
                <ContextMenu v-if="isSelectMode && selectedCount > 0" @select="handleTranslate">
                    <ButtonComponent size="sm" variant="accent">
                        {{ t('shows.translateWithCount', { count: selectedCount }) }}
                    </ButtonComponent>
                </ContextMenu>
                <ButtonComponent size="sm" variant="accent" @click="toggleSelectMode">
                    {{ isSelectMode ? t('shows.cancel') : t('shows.translateMultiple') }}
                </ButtonComponent>
                <ButtonComponent
                    size="sm"
                    variant="accent"
                    @click="showStore.includeAll(MEDIA_TYPE.SHOW, true)">
                    {{ t('shows.includeAll') }}
                </ButtonComponent>
                <ButtonComponent
                    size="sm"
                    variant="accent"
                    @click="showStore.includeAll(MEDIA_TYPE.SHOW, false)">
                    {{ t('shows.excludeAll') }}
                </ButtonComponent>
                <span class="text-accent-content text-sm">
                    {{
                        t('shows.includedSummary', {
                            included: showStore.includeSummary.included,
                            total: showStore.includeSummary.total
                        })
                    }}
                </span>
            </template>
        </ListToolbar>

        <div class="w-full px-4">
            <!-- Shows -->
            <div class="border-line hidden border-b font-bold md:grid md:grid-cols-12">
                <div :class="isSelectMode ? 'col-span-7' : 'col-span-8'" class="px-4 py-2">
                    {{ t('shows.title') }}
                </div>
                <div class="col-span-1 px-4 py-2">{{ t('shows.include') }}</div>
                <div class="col-span-3 px-4 py-2">{{ t('shows.delay') }}</div>
                <div
                    v-if="isSelectMode"
                    class="col-span-1 flex items-center justify-center px-4 py-2">
                    <CheckboxComponent :model-value="selectAll" @change="toggleSelectAll()" />
                </div>
            </div>
            <div v-for="item in shows.items" :key="item.id">
                <div
                    class="border-line flex cursor-pointer flex-wrap items-center gap-x-3 gap-y-2 border-b py-3 md:grid md:grid-cols-12 md:gap-0 md:py-0"
                    @click="toggleShow(item)">
                    <div
                        :class="isSelectMode ? 'md:col-span-7' : 'md:col-span-8'"
                        class="flex w-full items-center gap-2 md:w-auto md:px-4 md:py-2">
                        <CaretButton :is-expanded="expandedShow !== item.id" class="pr-2" />
                        <span>{{ item.title }}</span>
                        <ActiveTranslationBadge :show="item" />
                        <span v-if="isSelectMode" class="ml-auto md:hidden" @click.stop>
                            <CheckboxComponent
                                :model-value="isSelected(item)"
                                @change="toggleSelect(item)" />
                        </span>
                    </div>
                    <div class="flex items-center gap-2 md:col-span-1 md:px-4 md:py-2" @click.stop>
                        <ToggleButton
                            v-model="item.includeInTranslation"
                            size="small"
                            @toggle:update="
                                () =>
                                    showStore.include(
                                        MEDIA_TYPE.SHOW,
                                        item.id,
                                        item.includeInTranslation
                                    )
                            " />
                        <span
                            class="text-primary-content/50 text-xs font-semibold tracking-wide uppercase md:hidden">
                            {{ t('shows.include') }}
                        </span>
                    </div>
                    <div class="flex items-center gap-2 md:col-span-3 md:px-4 md:py-2" @click.stop>
                        <InputComponent
                            :model-value="item.translationAgeThreshold ?? null"
                            :placeholder="t('shows.hours')"
                            class="w-14"
                            size="sm"
                            :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                            :type="INPUT_TYPE.NUMBER"
                            @update:value="
                                (value) => {
                                    item.translationAgeThreshold = value
                                    showStore.updateThreshold(MEDIA_TYPE.SHOW, item.id, value)
                                }
                            " />
                        <span
                            class="text-primary-content/50 text-xs font-semibold tracking-wide uppercase md:hidden">
                            {{ t('shows.delay') }}
                        </span>
                    </div>
                    <div
                        v-if="isSelectMode"
                        class="hidden items-center justify-center px-4 py-2 md:col-span-1 md:flex"
                        @click.stop>
                        <CheckboxComponent
                            :model-value="isSelected(item)"
                            @change="toggleSelect(item)" />
                    </div>
                </div>
                <SeasonTable v-if="expandedShow === item.id" :seasons="item.seasons" />
            </div>
        </div>

        <PaginationComponent
            v-if="shows.totalCount"
            v-model="filter"
            :total-count="shows.totalCount"
            :page-size="shows.pageSize" />
    </div>
    <NoMediaNotification v-else />
</template>

<script setup lang="ts">
import { ref, Ref, computed, ComputedRef } from 'vue'
import { useI18n } from 'vue-i18n'
import {
    ILanguage,
    INPUT_TYPE,
    INPUT_VALIDATION_TYPE,
    IShow,
    MEDIA_TYPE,
    SETTINGS
} from '@/types'
import { useSelection } from '@/composables/useSelection'
import { usePagedList } from '@/composables/usePagedList'
import { useInstanceStore } from '@/store/instance'
import { useSettingStore } from '@/store/setting'
import { useShowStore } from '@/store/show'
import { useTranslateStore } from '@/store/translate'
import PaginationComponent from '@/components/common/PaginationComponent.vue'
import ListToolbar from '@/components/common/ListToolbar.vue'
import CaretButton from '@/components/common/CaretButton.vue'
import NoMediaNotification from '@/components/common/NoMediaNotification.vue'
import ToggleButton from '@/components/common/ToggleButton.vue'
import SeasonTable from '@/components/features/show/SeasonTable.vue'
import InputComponent from '@/components/common/InputComponent.vue'
import CheckboxComponent from '@/components/common/CheckboxComponent.vue'
import ContextMenu from '@/components/layout/ContextMenu.vue'
import ButtonComponent from '@/components/common/ButtonComponent.vue'
import ActiveTranslationBadge from '@/components/common/ActiveTranslationBadge.vue'

const instanceStore = useInstanceStore()
const showStore = useShowStore()
const settingStore = useSettingStore()
const translateStore = useTranslateStore()
const { t } = useI18n()
const expandedShow: Ref<boolean | number | null> = ref(null)

const settingsCompleted: ComputedRef<string> = computed(
    () => settingStore.getSetting(SETTINGS.SONARR_SETTINGS_COMPLETED) as string
)

const navigateToDetails = computed({
    get: () => settingStore.getSetting(SETTINGS.NAVIGATE_TO_DETAILS_ON_REQUEST) as string,
    set: (value: string) => {
        settingStore.updateSetting(SETTINGS.NAVIGATE_TO_DETAILS_ON_REQUEST, value, true)
    }
})

const { items: shows, filter } = usePagedList<IShow>({
    items: () => showStore.get,
    filter: () => showStore.getFilter,
    setFilter: (value) => showStore.setFilter(value),
    fetch: () => showStore.fetch()
})

const toggleShow = async (show: IShow) => {
    if (expandedShow.value === show.id) {
        expandedShow.value = null
        return
    }
    instanceStore.setPoster({ content: show, type: 'show' })
    expandedShow.value = show.id
}

const {
    selectedIds,
    selectedCount,
    selectAll,
    isSelected,
    toggleSelect,
    toggleSelectAll,
    clearSelection
} = useSelection<IShow>(() => shows.value.items)

const isSelectMode = ref(false)

const toggleSelectMode = () => {
    isSelectMode.value = !isSelectMode.value
    if (!isSelectMode.value) {
        clearSelection()
    }
}

const handleTranslate = async (language: ILanguage) => {
    await translateStore.bulkTranslate([...selectedIds.value], language.code, MEDIA_TYPE.SHOW)
    clearSelection()
    isSelectMode.value = false
}
</script>
