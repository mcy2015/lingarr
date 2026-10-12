// TEMP DEBUG (IA migration): surface the error killing the app
window.addEventListener('unhandledrejection', (e) => {
    const reason = (e as PromiseRejectionEvent).reason
    document.title = 'REJECTION: ' + String(reason?.message ?? reason)
})
window.addEventListener('error', (e) => {
    document.title = 'ERROR: ' + e.message
})
// TEMP DEBUG END

import { createApp } from 'vue'
import { createPinia } from 'pinia'

import router from '@/router'
import App from './App.vue'

import { showTitle } from '@/directives'
import i18n from '@/i18n'
import '@/assets/style.css'
import './utils'

const pinia = createPinia()
const app = createApp(App)

app.directive('show-title', showTitle)
app.use(pinia)
app.use(router)
app.use(i18n)
app.mount('#app')
