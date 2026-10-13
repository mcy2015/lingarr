import axios, { AxiosStatic } from 'axios'
import { Services } from '@/types'
import { subtitleService } from './subtitleService'
import { importService } from './importService'
import { mediaService } from './mediaService'
import { directoryService } from '@/services/directoryService'
// Axios assembly side effects (baseURL, ApiError normalisation, 401 → login
// redirect) live in the app cell; importing them here keeps the barrel's
// load-on-import behaviour identical to before the move.
import '@/cells/app/http'

const services = (axios: AxiosStatic): Services => ({
    subtitle: subtitleService(axios),
    import: importService(axios),
    media: mediaService(axios),
    directory: directoryService(axios)
})

export default services(axios)
