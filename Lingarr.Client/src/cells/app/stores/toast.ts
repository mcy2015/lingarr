import { acceptHMRUpdate, defineStore } from 'pinia'

export type ToastType = 'success' | 'error' | 'info'

export interface IToast {
    id: number
    message: string
    type: ToastType
}

interface IPushToast {
    message: string
    type?: ToastType
}

const TOAST_DURATION = 5000

let nextToastId = 0

export const useToastStore = defineStore('toast', {
    state: (): { toasts: IToast[] } => ({
        toasts: []
    }),
    actions: {
        push({ message, type = 'info' }: IPushToast): void {
            const id = ++nextToastId
            this.toasts.push({ id, message, type })
            setTimeout(() => this.dismiss(id), TOAST_DURATION)
        },

        dismiss(id: number): void {
            this.toasts = this.toasts.filter((toast) => toast.id !== id)
        }
    }
})

if (import.meta.hot) {
    import.meta.hot.accept(acceptHMRUpdate(useToastStore, import.meta.hot))
}
