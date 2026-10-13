import axios, { AxiosStatic } from 'axios'
import { Services } from '@/types'
import { importService } from './importService'
import { directoryService } from '@/services/directoryService'
// Axios assembly side effects (baseURL, ApiError normalisation, 401 → login
// redirect) live in the app cell; importing them here keeps the barrel's
// load-on-import behaviour identical to before the move.
import '@/cells/app/http'

const services = (axios: AxiosStatic): Services => ({
    import: importService(axios),
    directory: directoryService(axios)
})

export default services(axios)
