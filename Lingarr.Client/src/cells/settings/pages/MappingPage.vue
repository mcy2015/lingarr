<template>
    <!-- ═══ 路径映射下钻屏(原型 screen-06 B 下钻):pb-head(← 媒体与自动化 +
         映射数 mono 副行 + 新增/保存动作)→ 映射行卡(源/目标路径 + 媒体类型 +
         移除,目录选择走 DirectoryModal)。CRUD 数据流零变化(原 mappingStore)。 -->
    <div class="w-full flex h-full min-h-0 flex-col gap-3.5 overflow-hidden p-4 lg:p-6">
        <PbHead
            :back-to="{ name: 'settings-sources' }"
            :back-label="t('settings.menu.groups.sources')"
            :title="t('settings.sources.mapping')"
            :sub="subLine">
            <template #actions>
                <button
                    type="button"
                    class="border-line text-primary-content hover:bg-tertiary focus-visible:ring-accent cursor-pointer rounded-md border px-2.5 py-1 text-xs font-medium transition-colors focus-visible:ring-2 focus-visible:outline-none"
                    data-test="mapping-add"
                    @click="addMapping">
                    {{ t('services.mapping.add') }}
                </button>
                <button
                    type="button"
                    class="border-line text-primary-content hover:bg-tertiary focus-visible:ring-accent inline-flex cursor-pointer items-center gap-1.5 rounded-md border px-2.5 py-1 text-xs font-medium transition-colors focus-visible:ring-2 focus-visible:outline-none disabled:cursor-not-allowed disabled:opacity-50"
                    :disabled="loading"
                    data-test="mapping-save"
                    @click="saveMappings">
                    <Icon
                        v-if="loading"
                        name="loaderCircle"
                        class="h-3.5 w-3.5 animate-spin"
                        aria-hidden="true" />
                    {{ t('common.save') }}
                </button>
            </template>
        </PbHead>

        <div
            class="border-line bg-secondary shell-scroll min-h-0 flex-1 overflow-y-auto rounded-md border"
            data-test="mapping-card">
            <div class="border-line hidden border-b font-medium md:grid md:grid-cols-12">
                <div class="text-secondary-content px-4 py-2 text-xs">
                    {{ t('services.mapping.source') }}
                </div>
                <div class="text-secondary-content px-4 py-2 text-xs">
                    {{ t('services.mapping.target') }}
                </div>
                <div class="text-secondary-content px-4 py-2 text-xs">
                    {{ t('services.mapping.mediaType') }}
                </div>
                <div class="flex justify-end px-4 py-2"></div>
            </div>
            <div
                v-for="(pair, index) in mappingStore.mappings"
                :key="`pair-${index}`"
                class="border-line flex flex-wrap items-center gap-x-3 gap-y-2 border-b px-4 py-2.5 text-[13px] last:border-b-0"
                :data-test="`mapping-pair-${index}`">
                <!-- 源路径:空缺时以「＋」补位开目录模态 -->
                <div class="flex min-w-0 flex-1 flex-wrap items-center gap-2">
                    <span class="font-semibold md:hidden">
                        {{ t('services.mapping.source') }}:&nbsp;
                    </span>
                    <span
                        v-if="pair.sourcePath"
                        class="mono border-line bg-tertiary inline-flex min-w-0 max-w-full items-center rounded border px-2 py-1 text-xs">
                        <span class="truncate" :title="pair.sourcePath">{{ pair.sourcePath }}</span>
                    </span>
                    <button
                        v-else
                        type="button"
                        class="text-secondary-content hover:text-primary-content cursor-pointer text-xs transition-colors"
                        :data-test="`mapping-pick-source-${index}`"
                        @click="openModal('source', index)">
                        + {{ t('services.mapping.pick') }}
                    </button>
                </div>

                <!-- 目标路径 -->
                <div class="flex min-w-0 flex-1 flex-wrap items-center gap-2">
                    <span class="font-semibold md:hidden">
                        {{ t('services.mapping.destination') }}:&nbsp;
                    </span>
                    <span
                        v-if="pair.destinationPath"
                        class="mono border-line bg-tertiary inline-flex min-w-0 max-w-full items-center rounded border px-2 py-1 text-xs">
                        <span class="truncate" :title="pair.destinationPath">
                            {{ pair.destinationPath }}
                        </span>
                    </span>
                    <button
                        v-else
                        type="button"
                        class="text-secondary-content hover:text-primary-content cursor-pointer text-xs transition-colors"
                        :data-test="`mapping-pick-target-${index}`"
                        @click="openModal('target', index)">
                        + {{ t('services.mapping.pick') }}
                    </button>
                </div>

                <!-- 媒体类型 -->
                <div class="flex min-w-0 flex-none items-center">
                    <SelectComponent
                        v-model:selected="pair.mediaType"
                        :options="[
                            { value: MEDIA_TYPE.MOVIE, label: t('services.mapping.movie') },
                            { value: MEDIA_TYPE.SHOW, label: t('services.mapping.show') }
                        ]" />
                </div>

                <!-- 移除 -->
                <div class="flex flex-none items-center justify-end">
                    <button
                        type="button"
                        class="border-danger/50 text-danger hover:bg-danger/10 focus-visible:ring-danger cursor-pointer rounded-md border px-2 py-0.5 text-xs transition-colors focus-visible:ring-2 focus-visible:outline-none"
                        :data-test="`mapping-remove-${index}`"
                        @click="removeMapping(index)">
                        {{ t('settings.providers.delete') }}
                    </button>
                </div>
            </div>
        </div>
    </div>

    <DirectoryModal :is-open="isModalOpen" @close="isModalOpen = false" @select="handleSelection" />
</template>

<script setup lang="ts">
import { computed, ref, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { useMappingStore } from '@/cells/settings/stores/mapping'
import { MEDIA_TYPE } from '@/types'
import SelectComponent from '@/cells/kernel/components/SelectComponent.vue'
import Icon from '@/cells/kernel/components/icons/Icon.vue'
import DirectoryModal from '@/cells/settings/components/DirectoryModal.vue'
import PbHead from '@/cells/app/components/PbHead.vue'
import { delay } from '@/cells/kernel/utils/delay'

const mappingStore = useMappingStore()
const { t } = useI18n()

const loading = ref(false)
const isModalOpen = ref(false)
const currentSide = ref<'source' | 'target'>('source')
const currentPairIndex = ref(0)

/// mono 副行 = 真实映射数(与媒体源卡计数行同源)。
const subLine = computed(() =>
    t('settings.sources.mappingHint', { count: mappingStore.mappings.length })
)

const openModal = (side: 'source' | 'target', index: number) => {
    currentSide.value = side
    currentPairIndex.value = index
    isModalOpen.value = true
}

const handleSelection = (path: string) => {
    const updatedMapping = mappingStore.getMappingByIndex(currentPairIndex.value)
    if (currentSide.value === 'source') {
        updatedMapping.sourcePath = path
    } else {
        updatedMapping.destinationPath = path
    }
    mappingStore.updateMapping(currentPairIndex.value, updatedMapping)
    isModalOpen.value = false
}

const saveMappings = async () => {
    if (loading.value) return
    loading.value = true
    await mappingStore.setMappings(mappingStore.mappings)
    await delay(1000)
    loading.value = false
}

const addMapping = () => {
    mappingStore.addMapping({
        sourcePath: '',
        destinationPath: '',
        mediaType: undefined
    })
}

const removeMapping = (index: number) => {
    mappingStore.removeMapping(index)
}

onMounted(async () => {
    await mappingStore.fetchMappings()
})
</script>
