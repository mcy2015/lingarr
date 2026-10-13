<template>
    <div class="border-line bg-primary/50 rounded-md border p-3">
        <div v-if="parseError" class="mb-3">
            <StatusMessage :message="parseError" type="error" />
        </div>
        <div v-else>
            <JsonTreeNode
                v-for="(node, index) in tree"
                :key="node.id"
                :node="node"
                :depth="0"
                parent-type="root"
                :array-index="index"
                :next-id="nextId"
                @update="onTreeChange"
                @delete="removeRoot(index)" />
            <ButtonComponent variant="ghost" size="xs" class="mt-2" @click="addRootProperty">
                <Icon name="plus" class="mr-1 h-3 w-3" />
                {{ t('services.template.addProperty') }}
            </ButtonComponent>
        </div>
        <div class="border-line mt-3 flex flex-wrap gap-3 border-t pt-3" @mousedown.prevent>
            <PlaceholderButton
                v-for="item in placeholders"
                :key="item.placeholder"
                :placeholder="item.placeholder"
                :placeholder-text="item.placeholderText"
                :title="item.title"
                :description="item.description"
                @insert="insertPlaceholder" />
        </div>
    </div>
</template>

<script setup lang="ts">
import { nextTick } from 'vue'
import { useI18n } from 'vue-i18n'
import { IPlaceholder } from '@/types'
import { useJsonTree } from '@/cells/settings/composables/useJsonTree'
import JsonTreeNode from './JsonTreeNode.vue'
import ButtonComponent from '@/cells/kernel/components/ButtonComponent.vue'
import PlaceholderButton from '@/cells/kernel/components/PlaceholderButton.vue'
import Icon from '@/cells/kernel/components/icons/Icon.vue'
import StatusMessage from '@/cells/kernel/components/StatusMessage.vue'

const props = defineProps<{
    modelValue: string
    placeholders: IPlaceholder[]
}>()

const { t } = useI18n()

const emit = defineEmits<{
    'update:modelValue': [value: string]
}>()

const { tree, parseError, nextId, onTreeChange, addRootProperty, removeRoot, watchModelValue } =
    useJsonTree(emit)

watchModelValue(() => props.modelValue)

function insertPlaceholder(placeholder: string) {
    const element = document.activeElement as HTMLInputElement | null
    if (!element || element.tagName !== 'INPUT') {
        return
    }

    const start = element.selectionStart ?? element.value.length
    const end = element.selectionEnd ?? element.value.length
    element.value = element.value.substring(0, start) + placeholder + element.value.substring(end)
    element.dispatchEvent(new Event('input', { bubbles: true }))

    nextTick(() => {
        element.focus()
        const newPos = start + placeholder.length
        element.setSelectionRange(newPos, newPos)
    })
}
</script>
