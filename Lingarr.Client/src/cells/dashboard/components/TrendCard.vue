<template>
    <!-- 产量与成本卡(原型 screen-01 .trend):双序列微柱图,纯 div 柱,
         两序列各自按自身峰值归一 —— 图例注明峰值,数字全部来自服务端聚合。
         meta 追加「本月累计」段(月度写回数与成本,QA P3-3),空库时为 0/$0。 -->
    <div class="rounded-md border border-line bg-secondary flex min-h-[120px] flex-col">
        <div class="flex shrink-0 flex-wrap items-center gap-2.5 border-b border-line px-4 py-3">
            <h2 class="text-[13px] font-semibold">{{ t('overview.trend.title') }}</h2>
            <span class="mono text-secondary-content ml-auto text-xs">
                <template v-if="trend">
                    {{
                        trend.totalCost != null
                            ? t('overview.trend.meta', {
                                  count: trend.totalWritten,
                                  cost: formatCost(trend.totalCost)
                              })
                            : t('overview.trend.metaUnknownCost', { count: trend.totalWritten })
                    }}
                    <template v-if="month">
                        ·
                        {{
                            month.cost != null
                                ? t('overview.trend.monthMeta', {
                                      count: month.written,
                                      cost: formatCost(month.cost)
                                  })
                                : t('overview.trend.monthMetaUnknownCost', { count: month.written })
                        }}
                    </template>
                </template>
                <template v-else>—</template>
            </span>
        </div>

        <div v-if="!trend || !series.hasData" class="text-secondary-content/60 my-auto px-4 py-5 text-center text-sm">
            {{ t('overview.trend.empty') }}
        </div>

        <div v-else class="flex min-h-0 flex-1 flex-col justify-end gap-2 p-4 pt-3">
            <!-- 双柱日组:写回 = accent,成本 = info 75%,高度各自归一 -->
            <div class="flex h-[64px] items-end justify-between gap-[3px]" data-test="trend-bars">
                <div
                    v-for="bar in series.bars"
                    :key="bar.date"
                    class="flex h-full min-w-0 flex-1 items-end justify-center gap-[2px]"
                    :title="barTitle(bar)">
                    <div
                        class="w-full max-w-[9px] rounded-t-[2px] bg-accent"
                        :style="{ height: Math.max(bar.writtenPct, bar.writtenPct > 0 ? 4 : 0) + '%' }"></div>
                    <div
                        v-if="bar.costPct !== null"
                        class="w-full max-w-[9px] rounded-t-[2px] bg-info/75"
                        :style="{ height: Math.max(bar.costPct, bar.costPct > 0 ? 4 : 0) + '%' }"></div>
                </div>
            </div>
            <div class="text-secondary-content flex flex-wrap items-center gap-x-4 gap-y-1 text-[11px]">
                <span class="flex items-center gap-1.5">
                    <i class="h-[9px] w-[9px] rounded-[2px] bg-accent"></i>
                    {{ t('overview.trend.written') }}
                </span>
                <span v-if="series.peakCost !== null" class="flex items-center gap-1.5">
                    <i class="h-[9px] w-[9px] rounded-[2px] bg-info/75"></i>
                    {{ t('overview.trend.cost') }}
                </span>
                <span class="ml-auto"></span>
                <span v-if="series.peakCost !== null">
                    {{
                        t('overview.trend.legendPeaks', {
                            count: series.peakWritten,
                            cost: formatCost(series.peakCost)
                        })
                    }}
                </span>
                <span v-else>
                    {{ t('overview.trend.legendPeakWritten', { count: series.peakWritten }) }}
                </span>
            </div>
            <div v-if="trend && !trend.ratesConfigured" class="text-warning text-[11px]">
                {{ t('overview.trend.costUnknown') }}
            </div>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { IDailyTrendStatistics } from '@/types'
import { buildTrendSeries, TrendBarGroup } from '@/cells/dashboard/services/trendSeries'

const props = defineProps<{
    trend: IDailyTrendStatistics | null
    /** Month-to-date totals for the meta line; null while the pipeline stats have not loaded. */
    month?: { written: number; cost: number | null } | null
}>()

const { t, locale } = useI18n()

const series = computed(() => buildTrendSeries(props.trend))

const costFormatter = new Intl.NumberFormat(locale.value, {
    style: 'currency',
    currency: 'USD',
    maximumFractionDigits: 2
})

const formatCost = (value: number): string => costFormatter.format(value)

const barTitle = (bar: TrendBarGroup): string => {
    const day = props.trend?.days.find((entry) => entry.date === bar.date)
    if (!day) return bar.date
    return day.cost != null
        ? `${bar.date} · ${day.writtenCount} / ${costFormatter.format(day.cost)}`
        : `${bar.date} · ${day.writtenCount}`
}
</script>
