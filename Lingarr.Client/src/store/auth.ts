import { acceptHMRUpdate, defineStore } from 'pinia'
import { IUser, IAuthStore } from '@/types'
import services from '@/services'
import i18n from '@/i18n'

export const useAuthStore = defineStore('auth', {
    state: (): IAuthStore => ({
        users: [],
        loading: false,
        error: '',
        success: '',
        isCreating: false,
        editingUserId: null,
        deletingUserId: null
    }),
    getters: {
        getUsers: (state): IUser[] => state.users,
        isCreatingUser: (state): boolean => state.isCreating
    },
    actions: {
        async loadUsers(): Promise<void> {
            this.loading = true
            this.users = await services.auth.getUsers()
            this.loading = false
        },

        startCreate(): void {
            this.isCreating = true
            this.editingUserId = null
        },

        startEdit(user: IUser): void {
            this.isCreating = false
            this.editingUserId = user.id
        },

        cancelEdit(): void {
            this.isCreating = false
            this.editingUserId = null
            this.error = ''
        },

        async saveUser(payload: { username: string; password: string }): Promise<void> {
            this.loading = true
            this.error = ''
            this.success = ''

            try {
                if (this.isCreating) {
                    // Creating a new user
                    await services.auth.signup(payload)
                    this.success = i18n.global.t('toast.user.created')
                } else {
                    // Updating a user
                    await services.auth.updateUser(this.editingUserId!, payload)
                    this.success = i18n.global.t('toast.user.updated')
                }

                setTimeout(() => {
                    this.success = ''
                }, 3000)

                this.cancelEdit()
                await this.loadUsers()
            } catch (err: any) {
                console.error('Error saving user:', err)
                this.error =
                    err?.data?.message ||
                    (this.isCreating
                        ? i18n.global.t('toast.user.createFailed')
                        : i18n.global.t('toast.user.updateFailed'))
            } finally {
                this.loading = false
            }
        },

        async deleteUser(user: IUser): Promise<void> {
            this.deletingUserId = user.id
            this.error = ''
            this.success = ''

            try {
                await services.auth.deleteUser(user.id)

                this.success = i18n.global.t('toast.user.deleted')
                setTimeout(() => {
                    this.success = ''
                }, 3000)

                await this.loadUsers()
            } catch (err: any) {
                console.error('Error deleting user:', err)
                this.error = err?.data?.message || i18n.global.t('toast.user.deleteFailed')
            } finally {
                this.deletingUserId = null
            }
        }
    }
})

if (import.meta.hot) {
    import.meta.hot.accept(acceptHMRUpdate(useAuthStore, import.meta.hot))
}
