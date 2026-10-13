import { createApp } from 'vue'
import { createPinia } from 'pinia'

import router from '@/router'
import App from './App.vue'

import { showTitle } from '@/directives'
import i18n from '@/i18n'
import '@/assets/style.css'
import './utils'
// Axios assembly side effects (baseURL, ApiError normalisation, 401 → login
// redirect) live in the app cell; loading them here keeps them active from
// app startup, before any lazily loaded route chunk makes a request.
import '@/cells/app/http'

const pinia = createPinia()
const app = createApp(App)

app.directive('show-title', showTitle)
app.use(pinia)
app.use(router)
app.use(i18n)
app.mount('#app')
