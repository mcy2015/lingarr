import { computed } from 'vue'
import { useSettingStore } from '@/cells/app/stores/setting'
import { parseServiceChain } from '@/cells/app/utils/serviceChain'
import { SETTINGS } from '@/types'

/**
 * service_type 回退链的共享视图(settings cell 内收敛):
 * 服务卡网格的展示过滤/删除可见性/「＋添加服务」候选与 FallbackOrderCard
 * 的行渲染/快捷添加,都从这一个 store 派生的 computed 出发,不再各自
 * 重复 parseServiceChain。链本身仍是数组读写(首位 = 默认服务)。
 */
export default function useServiceChain() {
    const settingStore = useSettingStore()

    const chain = computed(() =>
        parseServiceChain(settingStore.getSetting(SETTINGS.SERVICE_TYPE) as string)
    )

    /// 链上成员判定:卡可见性(在链 || configured)与删除按钮(仅在链)共用。
    function isOnChain(provider: string): boolean {
        return chain.value.includes(provider)
    }

    /// 未上链行过滤 —— 两个添加入口的公共核心:
    /// 网格「＋添加服务」取全集(内置+插件,含已配置的免配置服务);
    /// FallbackOrderCard 快捷添加在此基础上收紧到 configured。
    function offChain<T extends { provider: string }>(rows: readonly T[]): T[] {
        const onChain = new Set(chain.value)
        return rows.filter((row) => !onChain.has(row.provider))
    }

    return { chain, isOnChain, offChain }
}
