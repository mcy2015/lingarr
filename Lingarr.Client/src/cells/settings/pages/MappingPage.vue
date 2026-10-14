<template>
    <!-- ═══ 路径映射下钻屏(原型 screen-06 B 下钻):pb-head(← 媒体源 + 映射数/
         不可达双态 mono 副行 + 新增/保存动作)→ 领域说明行 → 映射行卡(源列 = 文本
         输入(*arr 容器视角,picker 浏览的是 Lingarr 自己的文件系统会误导);
         目标列保留目录选择)→ 测试映射行(对已保存映射出 verdict)。 -->
    <DrillDownPage
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

        <div
            class="border-line bg-secondary shell-scroll min-h-0 flex-1 overflow-y-auto rounded-md border"
            data-test="mapping-card">
            <!-- 领域说明:回答首配用户「源和目标各填什么」的第一问 -->
            <p
                class="text-secondary-content border-line border-b px-4 py-2.5 text-[12px] leading-snug"
                data-test="mapping-explain">
                {{ t('services.mapping.explain') }}
            </p>
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
                <!-- 源路径:文本输入 —— 这是 *arr 容器的视角,Lingarr 的文件浏览器
                     恰恰在此场景下选不到它,picker 只会误导 -->
                <div class="flex min-w-0 flex-1 flex-wrap items-center gap-2">
                    <span class="font-semibold md:hidden">
                        {{ t('services.mapping.source') }}:&nbsp;
                    </span>
                    <input
                        v-model="pair.sourcePath"
                        type="text"
                        class="border-line bg-primary text-primary-content mono focus:border-accent min-w-0 max-w-full flex-1 rounded-md border px-2.5 py-1.5 text-xs outline-none"
                        :placeholder="t('services.mapping.sourcePlaceholder')"
                        :data-test="`mapping-source-input-${index}`" />
                </div>

                <!-- 目标路径:Lingarr 自己的文件系统,目录选择成立 -->
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

            <!-- 测试映射:对已保存映射出 verdict(与同步同一转换路径) -->
            <div
                class="flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-2.5 text-[13px]"
                data-test="mapping-test">
                <b class="flex-none">{{ t('services.mapping.test') }}</b>
                <input
                    v-model="testPath"
                    type="text"
                    class="border-line bg-primary text-primary-content mono focus:border-accent min-w-0 flex-1 rounded-md border px-2.5 py-1.5 text-xs outline-none"
                    :placeholder="t('services.mapping.testPlaceholder')"
                    data-test="mapping-test-input"
                    @keydown.enter="runTest" />
                <SelectComponent
                    v-model:selected="testMediaType"
                    :options="[
                        { value: MEDIA_TYPE.MOVIE, label: t('services.mapping.movie') },
                        { value: MEDIA_TYPE.SHOW, label: t('services.mapping.show') }
                    ]" />
                <button
                    type="button"
                    class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors disabled:cursor-not-allowed disabled:opacity-40"
                    :disabled="testing"
                    data-test="mapping-test-run"
                    @click="runTest">
                    {{ t('services.mapping.test') }}
                </button>
                <span
                    v-if="testResult"
                    class="mono w-full truncate text-xs"
                    :class="testResultClass"
                    :title="testResultText"
                    data-test="mapping-test-result">
                    {{ testResultText }}
                </span>
            </div>
        </div>
    </DrillDownPage>

    <DirectoryModal :is-open="isModalOpen" @close="isModalOpen = false" @select="handleSelection" />
</template>

<script setup lang="ts">
import { computed, ref, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { useMappingStore } from '@/cells/settings/stores/mapping'
import { mappingService } from '@/cells/settings/services/mappingService'
import { ITestMappingResult, MEDIA_TYPE } from '@/types'
import axios from 'axios'
import SelectComponent from '@/cells/kernel/components/SelectComponent.vue'
import Icon from '@/cells/kernel/components/icons/Icon.vue'
import DirectoryModal from '@/cells/settings/components/DirectoryModal.vue'
import DrillDownPage from '@/cells/app/components/DrillDownPage.vue'
import { delay } from '@/cells/kernel/utils/delay'

const mappingStore = useMappingStore()
const { t } = useI18n()
const mappingApi = mappingService(axios)

const loading = ref(false)
const isModalOpen = ref(false)
const currentSide = ref<'source' | 'target'>('source')
const currentPairIndex = ref(0)

/// mono 副行 = 真实映射数 + 不可达体检警示(双态,M=0 时警示不存在)。
const subLine = computed(() => {
    const base = t('settings.sources.mappingHint', { count: mappingStore.mappings.length })
    if (mappingStore.unreachableCount > 0) {
        return `${base} · ${t('settings.sources.mappingUnreachable', { count: mappingStore.unreachableCount })}`
    }
    return base
})

// ── 测试映射:三分支 verdict(ok 可达 / matched+!exists 不可达 / !matched 未命中) ──
const testPath = ref('')
const testMediaType = ref<string>(MEDIA_TYPE.MOVIE)
const testing = ref(false)
const testResult = ref<ITestMappingResult | null>(null)

const testResultText = computed(() => {
    if (!testResult.value) return ''
    if (testResult.value.ok) {
        return t('services.mapping.testOk', { path: testResult.value.mappedPath })
    }
    if (testResult.value.matched) {
        return t('services.mapping.testMissing', { path: testResult.value.mappedPath })
    }
    return t('services.mapping.testNoHit')
})

const testResultClass = computed(() => {
    if (!testResult.value) return ''
    if (testResult.value.ok) return 'text-success-content'
    return 'text-danger'
})

async function runTest(): Promise<void> {
    if (testing.value || testPath.value.trim() === '') return
    testing.value = true
    try {
        testResult.value = await mappingApi.testMapping(testPath.value.trim(), testMediaType.value)
    } catch {
        testResult.value = null
    } finally {
        testing.value = false
    }
}

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
    await mappingStore.fetchStatus()
    await delay(1000)
    loading.value = false
}

const addMapping = () => {
    // mediaType defaults to movie — an unset type makes the save a silent 400
    mappingStore.addMapping({
        sourcePath: '',
        destinationPath: '',
        mediaType: MEDIA_TYPE.MOVIE
    })
}

const removeMapping = (index: number) => {
    mappingStore.removeMapping(index)
}

onMounted(async () => {
    await mappingStore.fetchMappings()
    void mappingStore.fetchStatus()
})
</script>
