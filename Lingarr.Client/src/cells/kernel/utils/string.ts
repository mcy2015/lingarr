export type QueryParamValue =
    | string
    | number
    | boolean
    | string[]
    | number[]
    | Record<string, unknown>

/**
 * Builds a URL by appending query parameters to the given path.
 *
 * New parameters are appended to an existing query string when the path
 * already contains one.
 *
 * @param path The base URL path.
 * @param params An object where each key-value pair represents a query parameter.
 * @returns The URL with the updated query parameters.
 *
 * @example
 * const url = buildUrl('https://api.example.com/translate', {
 *   subtitle: 'path/to/subtitle.srt',
 *   source: 'en',
 *   target: 'es'
 * });
 *
 * console.log(url);
 * // Output: "https://api.example.com/translate?subtitle=path%2Fto%2Fsubtitle.srt&source=en&target=es"
 */
export const buildUrl = (path: string, params: Record<string, QueryParamValue>): string => {
    const hasQueryString = path.includes('?')

    const queryString = Object.keys(params)
        .flatMap((key) => {
            const value = params[key]
            if (Array.isArray(value)) {
                // For array values
                return value.map((v) => `${encodeURIComponent(key)}=${encodeURIComponent(v)}`)
            } else if (typeof value === 'object' && value !== null) {
                // For objects, serialize to JSON
                return `${encodeURIComponent(key)}=${encodeURIComponent(JSON.stringify(value))}`
            } else {
                // For single values, return one entry
                return `${encodeURIComponent(key)}=${encodeURIComponent(value)}`
            }
        })
        .join('&')

    if (!queryString) {
        return path
    }

    if (hasQueryString) {
        return `${path}&${queryString}`
    } else {
        return `${path}?${queryString}`
    }
}

/**
 * Masks a secret for display: first and last `edge` characters stay visible,
 * the middle collapses to a fixed bullet run. Values too short to safely
 * expose an edge (<= edge * 2 characters, including the empty string) are
 * masked entirely with a fixed-width bullet run so the mask never leaks the
 * original length.
 *
 * @param value - The secret to mask.
 * @param edge - How many characters remain visible at each end (default 4).
 * @returns The masked display string, never the full secret.
 *
 * @example
 * maskSecret('abcdefghij')   // 'abcd•••ghij'
 * maskSecret('short')        // '••••••••'
 * maskSecret('')             // '••••••••'
 */
export const maskSecret = (value: string, edge: number = 4): string => {
    const FULL_MASK = '••••••••'
    if (value.length <= edge * 2) {
        return FULL_MASK
    }
    return `${value.slice(0, edge)}•••${value.slice(-edge)}`
}

declare global {
    // oxlint-disable-next-line no-unused-vars - global String prototype
    interface String {
        /**
         * Formats a string by replacing placeholders e.g. `{key}` with values from the given object.
         *
         * @param {object} params - The object containing key-value pairs for replacement.
         * @returns {string} The formatted string.
         */
        format(params: Record<string, string | number>): string
    }
}

String.prototype.format = function (params: Record<string, string | number>): string {
    let string = this as string
    for (const key in params) {
        const value = params[key]
        string = string.replace(new RegExp('\\{' + key + '\\}', 'gm'), value.toString())
    }
    return string
}
