<template>
    <CardComponent :title="t('services.preview.title')">
        <template #description>
            {{ t('services.preview.description') }}
        </template>
        <template #content>
            <div class="space-y-3">
                <div class="flex flex-wrap gap-2">
                    <input
                        v-model="text"
                        type="text"
                        class="border-line bg-secondary text-primary-content placeholder:text-secondary-content/50 min-w-0 flex-1 rounded-md border px-3 py-1.5 text-sm outline-none focus:border-accent"
                        :placeholder="t('services.preview.sample')"
                        :aria-label="t('services.preview.inputLabel')"
                        @keydown.enter="runPreview" />
                    <ButtonComponent
                        size="sm"
                        :loading="running"
                        :disabled="!text.trim()"
                        @click="runPreview">
                        {{ t('services.preview.run') }}
                    </ButtonComponent>
                </div>

                <!-- 失败态:结构化错误信息 -->
                <div
                    v-if="result && !result.ok"
                    class="text-danger border-danger/40 bg-danger/10 rounded-md border px-3 py-2 text-[12.5px] leading-relaxed break-words"
                    data-testid="preview-error">
                    {{ t('services.preview.failed', { message: result.message ?? '' }) }}
                </div>

                <template v-if="result?.ok">
                    <!-- 译文 -->
                    <div
                        class="border-line bg-tertiary text-primary-content rounded-md border px-3 py-2.5 text-sm leading-relaxed"
                        data-testid="preview-output">
                        {{ result.translated }}
                    </div>

                    <!-- 术语命中 chips -->
                    <div
                        v-if="result.glossaryHits.length > 0"
                        class="flex flex-wrap gap-1.5"
                        data-testid="preview-glossary">
                        <span
                            v-for="hit in result.glossaryHits"
                            :key="hit.sourceTerm"
                            class="mono inline-flex items-center gap-1 rounded border px-1.5 py-px text-[11px]"
                            :class="
                                hit.isLocked
                                    ? 'text-success border-success/40'
                                    : 'text-accent border-accent/45'
                            "
                            :title="hit.isLocked ? t('services.preview.lockedTerm') : ''">
                            {{ hit.sourceTerm }} → {{ hit.targetTerm }}
                        </span>
                    </div>

                    <!-- meta 行:延迟 · 成本(后端给什么显示什么)-->
                    <div class="mono text-secondary-content text-[11px]" data-testid="preview-meta">
                        {{ Math.round(result.latencyMs) }} ms<template v-if="result.cost != null">
                            · {{ formatCost(result.cost) }}</template
                        >
                    </div>
                </template>
            </div>
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import axios from 'axios'
import { translateService } from '@/cells/app/api/translateService'
import ButtonComponent from '@/cells/kernel/components/ButtonComponent.vue'
import CardComponent from '@/cells/kernel/components/CardComponent.vue'
import { ITranslationPreviewResponse } from '@/types'

const props = defineProps<{ service: string }>()

const translateApi = translateService(axios)
const { t, locale } = useI18n()

const text = ref(t('services.preview.sample'))
const running = ref(false)
const result = ref<ITranslationPreviewResponse | null>(null)

async function runPreview(): Promise<void> {
    const line = text.value.trim()
    if (!line || running.value) return
    running.value = true
    result.value = null
    try {
        result.value = await translateApi.previewTranslation({
            serviceType: props.service,
            text: line
        })
    } catch (error) {
        result.value = {
            ok: false,
            translated: null,
            latencyMs: 0,
            cost: null,
            glossaryHits: [],
            message: error instanceof Error ? error.message : String(error)
        }
    } finally {
        running.value = false
    }
}

function formatCost(value: number): string {
    return new Intl.NumberFormat(locale.value, {
        style: 'currency',
        currency: 'USD',
        maximumFractionDigits: 4
    }).format(value)
}
</script>
