import { AxiosStatic } from 'axios'
import { IBilingualMergeRequest, ISubtitleService } from '@/types'

const service = (http: AxiosStatic, resource = '/api/subtitle'): ISubtitleService => ({
    collect<T>(path: string): Promise<T> {
        return http
            .post(
                `${resource}/all`,
                { path: path },
                {
                    headers: {
                        'Cache-Control': 'no-cache, no-store, must-revalidate',
                        Pragma: 'no-cache',
                        Expires: '0'
                    }
                }
            )
            .then((response) => response.data)
    },
    /// B06: POST api/subtitle/bilingual-merge/merge — the two-file bilingual
    /// merge (separate controller; deliberately NOT under ${resource}/all's
    /// path-whitelist contract since it validates media ownership instead).
    mergeBilingual<T>(request: IBilingualMergeRequest): Promise<T> {
        return http
            .post(`${resource}/bilingual-merge/merge`, request)
            .then((response) => response.data)
    }
})

export const subtitleService = (axios: AxiosStatic): ISubtitleService => {
    return service(axios)
}
