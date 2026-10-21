import { AxiosStatic } from 'axios'

/// POST /api/subtitle/post-process/preview — how many existing library
/// subtitle files the configured variant mode would rewrite. Never writes.
export interface IPostProcessPreviewResponse {
    mode: string
    scanned: number
    wouldChange: number
}

export interface IPostProcessFailedFile {
    path: string
    error: string
}

/// POST /api/subtitle/post-process/execute — per-file idempotent conversion
/// of the existing library subtitles; one failure never blocks the rest.
export interface IPostProcessExecuteResponse {
    mode: string
    scanned: number
    converted: number
    skipped: number
    failed: IPostProcessFailedFile[]
}

const service = (http: AxiosStatic, resource = '/api/subtitle/post-process') => ({
    preview(): Promise<IPostProcessPreviewResponse> {
        return http.post(`${resource}/preview`).then((response) => response.data)
    },
    execute(): Promise<IPostProcessExecuteResponse> {
        return http.post(`${resource}/execute`).then((response) => response.data)
    }
})

export const postProcessService = (http: AxiosStatic) => service(http)
