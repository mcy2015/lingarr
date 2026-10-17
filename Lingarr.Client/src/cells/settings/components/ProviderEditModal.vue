<template>
    <!-- ═══ 服务凭据编辑模态(原型 screen-06 交互 2「编辑」/交互 6「配置」):
         内容 = 该服务的 DynamicPluginForm(自 ServicesSettings 展开区迁入)。 -->
    <Teleport to="body">
        <Transition
            enter-active-class="transition-opacity duration-200 ease-in-out"
            leave-active-class="transition-opacity duration-200 ease-in-out"
            enter-from-class="opacity-0"
            leave-to-class="opacity-0">
            <div
                v-if="isOpen"
                class="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4"
                @click="emit('close')"
                @keydown.escape="emit('close')">
                <div
                    class="bg-secondary border-line shell-scroll max-h-[85vh] w-full max-w-lg overflow-y-auto rounded-lg border p-5"
                    role="dialog"
                    aria-modal="true"
                    :aria-label="title"
                    data-test="provider-edit-modal"
                    @click.stop>
                    <div class="mb-4 flex items-center gap-2.5">
                        <h2 class="text-[15px] font-semibold">{{ title }}</h2>
                        <button
                            type="button"
                            class="border-line text-primary-content hover:bg-tertiary ml-auto cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                            data-test="provider-edit-close"
                            @click="emit('close')">
                            {{ t('common.close') }}
                        </button>
                    </div>

                    <div v-if="loading" class="text-secondary-content py-6 text-center text-sm">
                        {{ t('common.loading') }}
                    </div>
                    <p v-else-if="error" class="text-danger py-6 text-center text-sm">{{ error }}</p>
                    <!-- #2:PluginField 只在加密写真实落库后才 emit('save'),
                         此处 toast 因此是真成功反馈(非乐观);写失败由
                         setting store 统一弹「保存失败」,本模态零重复 toast。 -->
                    <DynamicPluginForm
                        v-else-if="manifest"
                        :manifest="manifest"
                        @save="toastStore.push({ message: t('common.saved'), type: 'success' })" />
                </div>
            </div>
        </Transition>
    </Teleport>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import axios from 'axios'
import { IPluginManifest } from '@/types'
import { pluginService } from '@/cells/settings/services/pluginService'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import DynamicPluginForm from '@/cells/settings/components/DynamicPluginForm.vue'

const props = defineProps<{
    isOpen: boolean
    /// provider id to edit ('' = closed)
    provider: string
    displayName: string
}>()

const emit = defineEmits<{
    (e: 'close'): void
}>()

const { t } = useI18n()
const toastStore = useToastStore()
const settingStore = useSettingStore()
const pluginApi = pluginService(axios)

const manifest = ref<IPluginManifest | null>(null)
const loading = ref(false)
const error = ref<string | null>(null)

const title = computed(() => props.displayName || props.provider)

/// 打开时拉取 manifest 并把其设置键装载进 store(与原 ServicesSettings 一致)
watch(
    () => [props.isOpen, props.provider] as const,
    async ([open]) => {
        if (!open) return
        manifest.value = null
        error.value = null
        loading.value = true
        try {
            const loaded = await pluginApi.getManifest(props.provider)
            await settingStore.setPluginSettings(loaded.settings)
            manifest.value = loaded
        } catch (err) {
            console.error('Failed to load manifest', err)
            error.value = t('services.noManifest', { provider: props.displayName || props.provider })
        } finally {
            loading.value = false
        }
    },
    { immediate: true }
)
</script>
