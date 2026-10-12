<template>
    <svg
        xmlns="http://www.w3.org/2000/svg"
        v-bind="{ ...svgAttrs, ...attrs }"
        :width="size"
        :height="size"
        v-html="ICONS[name]"></svg>
</template>

<script setup lang="ts">
import { computed, useAttrs, type SVGAttributes } from 'vue'
import { ICONS, ICON_ATTRS, type IconName } from './index'

defineOptions({ inheritAttrs: false })

const attrs = useAttrs()

const props = withDefaults(
    defineProps<{
        name: IconName
        size?: number | string
    }>(),
    {
        size: 24
    }
)

const svgAttrs = computed<SVGAttributes>(() => ({
    viewBox: '0 0 24 24',
    fill: 'none' as string | undefined,
    stroke: 'currentColor',
    'stroke-width': 2,
    'stroke-linecap': 'round',
    'stroke-linejoin': 'round',
    ...ICON_ATTRS[props.name]
}))
</script>
