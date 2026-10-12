import type { IconName } from '@/components/icons'

export interface MenuItem {
    label: string
    icon: IconName
    route: string
    children: string[]
}
