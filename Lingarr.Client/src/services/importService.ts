import { AxiosStatic } from 'axios'
import { IImportService } from '@/types'

const service = (http: AxiosStatic, resource = '/api/subtitle'): IImportService => ({
    parseFilenames: async (filenames: string[]) => {
        const response = await http.post(`${resource}/parse-filenames`, { filenames })
        return response.data
    },

    importFiles: async (
        files: File[],
        sourceLanguage: string,
        targetLanguage: string,
        translate: boolean
    ) => {
        const formData = new FormData()
        files.forEach((file) => formData.append('files', file))
        formData.append('sourceLanguage', sourceLanguage)
        formData.append('targetLanguage', targetLanguage)
        formData.append('translate', String(translate))

        const response = await http.post(`${resource}/import`, formData, {
            headers: { 'Content-Type': 'multipart/form-data' }
        })
        return response.data
    }
})

export const importService = (axios: AxiosStatic): IImportService => {
    return service(axios)
}
