import { acceptHMRUpdate, defineStore } from 'pinia'
import axios from 'axios'
import i18n from '@/i18n'
import { IPathMapping } from '@/types'
import { mappingService } from '@/cells/settings/services/mappingService'

const mappingApi = mappingService(axios)

export const useMappingStore = defineStore('mapping', {
    state: () => ({
        mappings: [] as IPathMapping[],
        error: null as string | null,
        /// Ground truth from /api/mapping/status — count of stored media
        /// directories that are not on disk (the silent-skip condition).
        unreachableCount: 0
    }),
    actions: {
        async fetchMappings() {
            this.error = null
            try {
                this.mappings = await mappingApi.getMappings()
            } catch (error) {
                this.error = i18n.global.t('toast.mapping.fetchFailed')
                console.error('Error fetching mappings:', error)
            }
        },
        async setMappings(newMappings: IPathMapping[]) {
            this.error = null
            try {
                await mappingApi.setMappings(newMappings)
                this.mappings = newMappings
            } catch (error) {
                this.error = i18n.global.t('toast.mapping.saveFailed')
                console.error('Error setting mappings:', error)
            }
        },
        async fetchStatus() {
            try {
                const status = await mappingApi.status()
                this.unreachableCount = status.unreachable.length
            } catch (error) {
                console.error('Error fetching mapping status:', error)
            }
        },
        addMapping(mapping: IPathMapping) {
            this.mappings.push(mapping)
        },
        removeMapping(index: number) {
            this.mappings.splice(index, 1)
        },
        updateMapping(index: number, updatedMapping: IPathMapping) {
            this.mappings[index] = updatedMapping
        }
    },
    getters: {
        getMappingByIndex: (state) => (index: number) => state.mappings[index],
        getMappingsByMediaType: (state) => (mediaType: string) =>
            state.mappings.filter((mapping) => mapping.mediaType === mediaType)
    }
})

if (import.meta.hot) {
    import.meta.hot.accept(acceptHMRUpdate(useMappingStore, import.meta.hot))
}
