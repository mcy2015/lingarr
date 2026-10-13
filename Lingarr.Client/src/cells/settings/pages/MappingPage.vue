<template>
    <div class="w-full">
        <div class="bg-tertiary flex items-center justify-between gap-2 p-4">
            {{ t('services.mapping.title') }}
            <div class="flex space-x-4">
                <button
                    class="text-info transition duration-300 hover:text-info"
                    @click="addMapping">
                    <Icon name="plus" class="h-5 w-5" />
                </button>
                <Icon v-if="loading" name="loaderCircle" class="h-5 w-5 animate-spin" />
                <Icon v-else name="floppy" class="h-5 w-5 cursor-pointer" @click="saveMappings" />
            </div>
        </div>

        <div class="w-full px-4">
            <div class="border-line hidden border-b font-bold md:grid md:grid-cols-12">
                <div class="col-span-4 px-4 py-2">{{ t('services.mapping.source') }}</div>
                <div class="col-span-4 px-4 py-2">{{ t('services.mapping.target') }}</div>
                <div class="col-span-3 px-4 py-2">{{ t('services.mapping.mediaType') }}</div>
                <div class="col-span-1 flex justify-end px-4 py-2"></div>
            </div>
            <div
                v-for="(pair, index) in mappingStore.mappings"
                :key="`pair-${index}`"
                class="md:border-line rounded-lg py-4 shadow-sm md:grid md:grid-cols-12 md:rounded-none md:border-b md:bg-transparent md:p-0 md:shadow-none">
                <div class="float-right md:hidden">
                    <Icon
                        name="times"
                        class="h-5 w-5 cursor-pointer text-danger transition duration-300 hover:text-danger"
                        @click="removeMapping(index)" />
                </div>
                <div class="mb-2 md:col-span-4 md:mb-0 md:px-4 md:py-2">
                    <span class="font-bold md:hidden">
                        {{ t('services.mapping.source') }}:&nbsp;
                    </span>
                    <div
                        class="flex items-center"
                        :class="{
                            'justify-between': pair.sourcePath
                        }">
                        <span v-if="pair.sourcePath" class="bg-secondary rounded-md px-2 py-1">
                            {{ pair.sourcePath }}
                        </span>

                        <div
                            class="flex cursor-pointer"
                            :class="{
                                'w-full justify-end': !pair.sourcePath
                            }"
                            @click="openModal('source', index)">
                            <Icon name="plus" class="h-12 w-5" />
                        </div>
                    </div>
                </div>
                <div class="mb-2 md:col-span-4 md:mb-0 md:px-4 md:py-2">
                    <span class="font-bold md:hidden">
                        {{ t('services.mapping.destination') }}:&nbsp;
                    </span>
                    <div
                        class="flex items-center"
                        :class="{
                            'justify-between': pair.destinationPath
                        }">
                        <span v-if="pair.destinationPath" class="bg-secondary rounded-md px-2 py-1">
                            {{ pair.destinationPath }}
                        </span>
                        <div
                            class="flex cursor-pointer"
                            :class="{
                                'w-full justify-end': !pair.destinationPath
                            }"
                            @click="openModal('target', index)">
                            <Icon name="plus" class="h-12 w-5" />
                        </div>
                    </div>
                </div>
                <div class="mb-2 md:col-span-3 md:mb-0 md:px-4 md:py-2">
                    <span class="font-bold md:hidden">
                        {{ t('services.mapping.mediaType') }}:&nbsp;
                    </span>
                    <SelectComponent
                        v-model:selected="pair.mediaType"
                        :options="[
                            { value: MEDIA_TYPE.MOVIE, label: t('services.mapping.movie') },
                            { value: MEDIA_TYPE.SHOW, label: t('services.mapping.show') }
                        ]" />
                </div>

                <div
                    class="hidden items-center justify-between md:col-span-1 md:flex md:justify-end md:px-4 md:py-2">
                    <Icon
                        name="times"
                        class="h-5 w-5 cursor-pointer text-danger transition duration-300 hover:text-danger"
                        @click="removeMapping(index)" />
                </div>
            </div>
        </div>
    </div>

    <DirectoryModal :is-open="isModalOpen" @close="isModalOpen = false" @select="handleSelection" />
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { useMappingStore } from '@/cells/settings/stores/mapping'
import { MEDIA_TYPE } from '@/types'
import SelectComponent from '@/cells/kernel/components/SelectComponent.vue'
import Icon from '@/cells/kernel/components/icons/Icon.vue'
import DirectoryModal from '@/cells/settings/components/DirectoryModal.vue'
import { delay } from '@/cells/kernel/utils/delay'

const mappingStore = useMappingStore()
const { t } = useI18n()

const loading = ref(false)
const isModalOpen = ref(false)
const currentSide = ref<'source' | 'target'>('source')
const currentPairIndex = ref(0)

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
