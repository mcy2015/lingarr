import { AxiosStatic } from 'axios'
import {
    IBilingualMergeRequest,
    ISubtitleService,
    ISubtitleShiftRequest,
    ISubtitleVariantRequest,
    ITimelineSyncRequest
} from '@/types'

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
    },
    /// B05: POST api/timeline-sync/sync — separate controller route, so the
    /// path is absolute; the B10 tools panel is its second consumer.
    timelineSync<T>(request: ITimelineSyncRequest): Promise<T> {
        return http.post('/api/timeline-sync/sync', request).then((response) => response.data)
    },
    /// B05: POST api/timeline-sync/restore — moves the .lingarr-tl-bk backup
    /// back over the file (undoes a correction AND a manual shift).
    timelineRestore<T>(request: ITimelineSyncRequest): Promise<T> {
        return http.post('/api/timeline-sync/restore', request).then((response) => response.data)
    },
    /// B10: POST api/subtitle/tools/shift — manual ±ms cue shift (rewriter
    /// reuse, ratio pinned to 1 server-side).
    shiftSubtitle<T>(request: ISubtitleShiftRequest): Promise<T> {
        return http
            .post(`${resource}/tools/shift`, request)
            .then((response) => response.data)
    },
    /// B10: POST api/subtitle/tools/variant — single-file 简繁 conversion
    /// through the B01 batch's core; no-change answers 200 changed=false.
    convertVariant<T>(request: ISubtitleVariantRequest): Promise<T> {
        return http
            .post(`${resource}/tools/variant`, request)
            .then((response) => response.data)
    }
})

export const subtitleService = (axios: AxiosStatic): ISubtitleService => {
    return service(axios)
}
