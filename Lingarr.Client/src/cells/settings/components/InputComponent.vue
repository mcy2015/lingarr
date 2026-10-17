<template>
    <!-- password 态根为 form + submit.prevent:Enter 不再触发隐式提交导航
         (#6),并以组件级 submit 事件把「Enter 提交」交给调用方(如 secret
         的显式保存语义 #8)。trailing 具名插槽占据输入框右端:有插槽内容时
         本地显隐按钮与 ValidationIcon 不渲染(#1B/#7 —— reveal 等尾部语义
         留在调用方,本组件保持哑组件)。 -->
    <component
        :is="type === INPUT_TYPE.PASSWORD ? 'form' : 'div'"
        class="relative"
        @submit.prevent="emit('submit')">
        <label v-if="label" :for="id" class="mb-1 block text-sm">
            {{ label }}
        </label>
        <div v-if="$slots.default" class="mb-1">
            <slot></slot>
        </div>
        <div class="relative">
            <input
                :id="id"
                autocomplete="off"
                :value="modelValue"
                :type="type == INPUT_TYPE.PASSWORD ? (showPassword ? 'text' : 'password') : type"
                :class="inputClasses"
                :placeholder="placeholder"
                @input="handleInput"
                @blur="emit('blur')" />
            <ValidationIcon
                v-if="validationType && !hasTrailing"
                :is-valid="isValid"
                :is-invalid="isInvalid"
                :size="size"
                :class="{
                    'pr-10': type === INPUT_TYPE.PASSWORD,
                    'pr-3': size === 'md',
                    'pr-1': size === 'sm'
                }" />
            <button
                v-if="type === INPUT_TYPE.PASSWORD && !hasTrailing"
                type="button"
                class="absolute inset-y-0 right-0 flex cursor-pointer items-center pr-3"
                @click="togglePassword">
                <Icon v-if="showPassword" name="eyeOn" class="h-5 w-5" />
                <Icon v-else name="eyeOff" class="h-5 w-5" />
            </button>
            <div
                v-if="hasTrailing"
                class="absolute inset-y-0 right-0 flex items-center pr-3">
                <slot name="trailing"></slot>
            </div>
        </div>
        <p v-if="validationType && isInvalid" class="mt-1 text-sm text-danger" v-html="error" />
    </component>
</template>

<script setup lang="ts">
import { computed, ref, useSlots } from 'vue'
import { InputType, INPUT_TYPE, InputValidationType } from '@/types'
import useValidation from '@/cells/settings/composables/useValidation'
import useDebounce from '@/cells/kernel/composables/useDebounce'
import ValidationIcon from '@/cells/kernel/components/ValidationIcon.vue'
import Icon from '@/cells/kernel/components/icons/Icon.vue'

const props = withDefaults(
    defineProps<{
        id?: string
        type?: InputType
        label?: string
        modelValue: string | number | null
        minLength?: number
        maxLength?: number
        placeholder?: string
        errorMessage?: string
        size?: 'sm' | 'md' | 'lg'
        validationType?: InputValidationType
        debounce?: number
    }>(),
    {
        size: 'md',
        debounce: 1000
    }
)

const emit = defineEmits<{
    (e: 'update:modelValue', value: string): void
    (e: 'update:validation', isValid: boolean): void
    (e: 'update:value', value: string): void
    /// blur 直通(password 态另有组件级 submit = 被阻止默认行为的 Enter)
    (e: 'blur'): void
    (e: 'submit'): void
}>()
const { isValid, isInvalid, error, validate } = useValidation(props)

/// trailing 插槽占据输入框右端:存在时本地显隐按钮与 ValidationIcon 让位
const slots = useSlots()
const hasTrailing = computed(() => !!slots.trailing)

const showPassword = ref(false)
const inputClasses = computed(() => [
    '[appearance:textfield] [&::-webkit-outer-spin-button]:appearance-none [&::-webkit-inner-spin-button]:appearance-none',
    'w-full rounded-md border bg-transparent outline-hidden transition-colors duration-200 focus:border-accent ',
    {
        'px-6 py-3 text-lg': props.size === 'lg',
        'px-4 py-2': props.size === 'md',
        'px-2 py-0.5 leading-3 text-sm': props.size === 'sm'
    },
    { 'border-success': props.validationType && isValid.value },
    { 'border-danger': props.validationType && isInvalid.value },
    { 'border-line': !props.validationType || (!isValid.value && !isInvalid.value) },
    // 右端有 trailing 控件时文本同样需要让位(含 reveal 后的 text 态)
    { 'pr-10': props.type === 'password' || hasTrailing.value }
])

const emitValue = (event: Event) => {
    const value = (event.target as HTMLInputElement).value
    if (props.validationType) {
        validate(value)
        emit('update:validation', isValid.value)
    }
    emit('update:modelValue', value)
    emit('update:value', value)
}

const debouncedValue = useDebounce(emitValue, props.debounce)

const handleInput = (event: Event) => {
    if (props.debounce > 0) {
        debouncedValue(event)
    } else {
        emitValue(event)
    }
}

const togglePassword = () => {
    showPassword.value = !showPassword.value
}
</script>
