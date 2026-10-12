import { MediaType } from '@/types'

export interface IPathMapping {
    sourcePath: string
    destinationPath: string
    mediaType: MediaType | undefined
}

export interface DirectoryItem {
    name: string
    fullPath: string
    lastModified: string
}
