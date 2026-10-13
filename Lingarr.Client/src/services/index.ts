import axios, { AxiosStatic } from 'axios'
import { Services } from '@/types'
import { translationRequestService } from '@/services/translationRequestService'
import { subtitleService } from './subtitleService'
import { importService } from './importService'
import { translateService } from './translateService'
import { mediaService } from './mediaService'
import { versionService } from './versionService'
import { directoryService } from '@/services/directoryService'
import { statisticsService } from '@/services/statisticsService'
import { glossaryService } from '@/services/glossaryService'
// Axios assembly side effects (baseURL, ApiError normalisation, 401 → login
// redirect) live in the app cell; importing them here keeps the barrel's
// load-on-import behaviour identical to before the move.
import '@/cells/app/http'

const services = (axios: AxiosStatic): Services => ({
    subtitle: subtitleService(axios),
    import: importService(axios),
    translate: translateService(axios),
    translationRequest: translationRequestService(axios),
    version: versionService(axios),
    media: mediaService(axios),
    directory: directoryService(axios),
    statistics: statisticsService(axios),
    glossary: glossaryService(axios)
})

export default services(axios)
