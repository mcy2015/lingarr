import type { IconName } from '@/cells/kernel/components/icons'

export interface MenuItem {
    label: string
    icon: IconName
    route: string
    children: string[]
}

export interface MenuItemGroup {
    title: string
    items: MenuItem[]
}
