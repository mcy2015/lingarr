import { AxiosStatic } from 'axios'
import { IRequestTemplateService } from '@/ts'

const service = (
    http: AxiosStatic,
    resource = '/api/requesttemplate'
): IRequestTemplateService => ({
    getDefaults<T>(): Promise<T> {
        return http.get(`${resource}/defaults`).then((response) => response.data)
    }
})

export const requestTemplateService = (axios: AxiosStatic): IRequestTemplateService => {
    return service(axios)
}
