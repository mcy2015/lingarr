<template>
    <!-- 多选语言选择器(改造自 SourceAndTarget 遗留的孤儿组件):选项全集
         来自文化表(translateStore.setLanguages / GET /api/translate/languages),
         行格式「code · 名称」;顶部输入框仅过滤(code 或名称,不区分大小写),
         不产生表外代码;已选值在触发器内以 chips 展示,chip 上 × 单个移除;
         ESC / 外点关闭;a11y 按 DropdownComponent 惯例(aria-haspopup/
         aria-expanded/键盘可达/role=listbox)。 -->
    <div class="relative w-full">
        <div
            ref="excludeClickOutside"
            class="border-line bg-primary flex min-h-12 w-full cursor-pointer flex-wrap items-center gap-1.5 rounded-md border px-3 py-2 transition-colors"
            :class="isOpen ? 'border-accent' : 'hover:border-accent/60'"
            role="combobox"
            aria-haspopup="listbox"
            :aria-expanded="isOpen"
            :aria-controls="listboxId"
            :aria-disabled="disabled"
            :tabindex="disabled ? -1 : 0"
            data-test="language-select-trigger"
            @click="toggleDropdown"
            @keydown.enter.prevent="toggleDropdown"
            @keydown.space.prevent="toggleDropdown"
            @keydown.down.prevent="openDropdown"
            @keydown.escape.stop="close">
            <span v-if="selected.length === 0" class="text-secondary-content/60 text-sm">
                {{ t('settings.languageSelect.placeholder') }}
            </span>
            <template v-else>
                <span
                    v-for="item in selected"
                    :key="item.code"
                    class="bg-accent/15 border-accent/45 text-accent-content mono inline-flex flex-none items-center gap-1 rounded border px-1.5 py-0.5 text-[11px]"
                    :data-test="`language-chip-${item.code.toUpperCase()}`">
                    {{ item.code.toUpperCase() }}
                    <button
                        type="button"
                        class="hover:text-danger cursor-pointer transition-colors"
                        :aria-label="
                            t('settings.languageSelect.remove', { code: item.code.toUpperCase() })
                        "
                        :data-test="`language-chip-remove-${item.code.toUpperCase()}`"
                        @click.stop="removeItem(item)">
                        <Icon name="times" class="h-3 w-3" />
                    </button>
                </span>
            </template>
            <Icon
                name="caretRight"
                :class="{ 'rotate-90': isOpen }"
                class="text-secondary-content ml-auto h-4 w-4 flex-none transition-transform duration-200" />
        </div>
        <ul
            v-show="isOpen"
            :id="listboxId"
            ref="clickOutside"
            role="listbox"
            aria-multiselectable="true"
            class="border-line bg-primary absolute z-20 max-h-72 w-full overflow-auto rounded-md border shadow-lg"
            :class="openUpward ? 'bottom-full mb-1' : 'top-full mt-1'"
            data-test="language-select-panel"
            @keydown.escape.stop="close">
            <li
                class="border-line bg-primary sticky top-0 z-10 flex items-center border-b"
                role="none">
                <input
                    ref="searchInput"
                    v-model="searchTerm"
                    type="text"
                    class="bg-primary text-primary-content placeholder:text-secondary-content/50 w-full p-2 text-sm outline-none"
                    :placeholder="t('settings.languageSelect.search')"
                    :aria-label="t('settings.languageSelect.search')"
                    data-test="language-select-filter"
                    @keydown.enter.prevent />
                <button
                    v-if="searchTerm"
                    type="button"
                    class="hover:text-primary-content/50 text-secondary-content flex-none cursor-pointer p-2 transition-colors"
                    :aria-label="t('settings.languageSelect.clearSearch')"
                    data-test="language-select-clear-search"
                    @click="searchTerm = ''">
                    <Icon name="times" class="h-4 w-4" />
                </button>
            </li>
            <li
                v-if="options.length === 0"
                class="text-secondary-content px-4 py-3 text-sm"
                data-test="language-select-no-options">
                {{ t('settings.languageSelect.noOptions') }}
            </li>
            <li
                v-else-if="filteredLanguages.length === 0"
                class="text-secondary-content px-4 py-3 text-sm"
                data-test="language-select-no-results">
                {{ t('settings.languageSelect.noResults') }}
            </li>
            <template v-else>
                <li
                    v-for="language in filteredLanguages"
                    :key="language.code"
                    role="option"
                    :aria-selected="isSelected(language)"
                    class="hover:bg-tertiary flex cursor-pointer items-center gap-2 px-4 py-2 text-sm"
                    :class="{ 'bg-accent/15': isSelected(language) }"
                    :data-test="`language-option-${language.code}`"
                    @click="selectItem(language)">
                    <span class="mono text-secondary-content flex-none text-[11px]">
                        {{ language.code }}
                    </span>
                    <span class="text-primary-content truncate">· {{ language.name }}</span>
                    <Icon
                        v-if="isSelected(language)"
                        name="checkMark"
                        class="text-accent ml-auto h-4 w-4 flex-none" />
                </li>
            </template>
        </ul>
    </div>
</template>

<script setup lang="ts">
import { computed, nextTick, ref, useId } from 'vue'
import { useI18n } from 'vue-i18n'
import { ILanguage } from '@/types'
import Icon from '@/cells/kernel/components/icons/Icon.vue'
import useClickOutside from '@/cells/kernel/composables/useClickOutside'

// 注意:不要套 withDefaults——它会关闭 3.5 的响应式解构,让 selected 变成
// setup 时的快照,连续选择/移除会基于过期数组计算。
const {
    options,
    selected = [],
    disabled = false
} = defineProps<{
    options: ILanguage[]
    selected?: ILanguage[]
    disabled?: boolean
}>()

const emit = defineEmits<{ (e: 'update:selected', value: ILanguage[]): void }>()

const { t } = useI18n()

const listboxId = useId()
const isOpen = ref(false)
const openUpward = ref(false)
const searchInput = ref<HTMLInputElement | undefined>()
const clickOutside = ref<HTMLElement | undefined>()
const excludeClickOutside = ref<HTMLElement | undefined>()
const searchTerm = ref('')

/// 输入仅过滤:命中 code 或名称(不区分大小写);任何文本都不会成为已选值。
const filteredLanguages = computed(() => {
    const needle = searchTerm.value.trim().toLowerCase()
    return options
        .filter(
            (language) =>
                needle === '' ||
                language.code.toLowerCase().includes(needle) ||
                language.name.toLowerCase().includes(needle)
        )
        .sort((a, b) => a.code.localeCompare(b.code))
})

function isSelected(item: ILanguage): boolean {
    return selected.some((language) => language.code === item.code)
}

/// 受控多选:切换某项并保持面板开启,把完整新数组一次性 emit。
function selectItem(item: ILanguage): void {
    emit('update:selected', toggle(item))
}

function removeItem(item: ILanguage): void {
    emit(
        'update:selected',
        selected.filter((language) => language.code !== item.code)
    )
}

function toggle(item: ILanguage): ILanguage[] {
    return isSelected(item)
        ? selected.filter((language) => language.code !== item.code)
        : [...selected, item]
}

async function openDropdown(): Promise<void> {
    if (disabled || isOpen.value) return
    if (excludeClickOutside.value) {
        const rect = excludeClickOutside.value.getBoundingClientRect()
        openUpward.value = window.innerHeight - rect.bottom < rect.top
    }
    isOpen.value = true
    await nextTick()
    searchInput.value?.focus()
}

function close(): void {
    isOpen.value = false
    searchTerm.value = ''
}

async function toggleDropdown(): Promise<void> {
    if (disabled) return
    if (isOpen.value) {
        close()
        return
    }
    await openDropdown()
}

useClickOutside(
    clickOutside,
    () => {
        close()
    },
    excludeClickOutside
)
</script>
