import { createApp } from 'vue'
import { createPinia } from 'pinia'

import router from '@/router'
import App from './App.vue'

import { highlight, showTitle } from '@/directives'
import i18n from '@/i18n'
import '@/assets/style.css'
import './utils'

const pinia = createPinia()
const app = createApp(App)

new Promise((resolve) => resolve(true))
    .then(() => {
        app.directive('highlight', highlight)
        app.directive('show-title', showTitle)
    })
    .then(() => {
        app.use(pinia)
        app.use(router)
        app.use(i18n)
    })
    .finally(() => {
        app.mount('#app')
    })
