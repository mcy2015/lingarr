import { createRouter, createWebHistory, RouteRecordRaw } from 'vue-router'
import { useInstanceStore } from '@/cells/app/stores/instance'
import { baseUrl } from '@/utils/baseUrl'

const routes: RouteRecordRaw[] = [
    // Auth
    {
        path: '/auth',
        component: () => import('@/cells/auth/layouts/AuthLayout.vue'),
        children: [
            {
                path: '/auth/login',
                component: () => import('@/cells/auth/pages/LoginPage.vue'),
                name: 'login',
                meta: { authenticated: false }
            }
        ]
    },

    // Main
    {
        path: '/',
        component: () => import('@/components/layout/MainLayout.vue'),
        meta: { authenticated: true },
        children: [
            {
                path: '',
                component: () => import('@/cells/dashboard/pages/DashboardPage.vue'),
                name: 'dashboard'
            },
            {
                path: '/library',
                component: () => import('@/cells/library/pages/LibraryPage.vue'),
                name: 'library'
            },
            {
                path: '/queue',
                component: () => import('@/cells/queue/pages/QueuePage.vue'),
                name: 'queue'
            },
            {
                path: '/queue/:id',
                component: () => import('@/cells/queue/pages/QueueDetailPage.vue'),
                name: 'queue-detail',
                props: true
            },
            {
                // :id optional — bare /proofread lands on the latest
                // completed request (resolved by the page itself)
                path: '/proofread/:id?',
                component: () => import('@/cells/proofread/pages/ProofreadPage.vue'),
                name: 'proofread',
                props: true
            },
            {
                // media detail drill-downs from the library matrix
                path: '/library/show/:id',
                component: () => import('@/cells/library/pages/MediaDetailPage.vue'),
                name: 'media-detail-show',
                props: (route) => ({ type: 'show', id: String(route.params.id) })
            },
            {
                path: '/library/movie/:id',
                component: () => import('@/cells/library/pages/MediaDetailPage.vue'),
                name: 'media-detail-movie',
                props: (route) => ({ type: 'movie', id: String(route.params.id) })
            },
            {
                // legacy paths -> new IA (bookmarks kept alive)
                path: '/shows',
                redirect: '/library?type=show'
            },
            {
                path: '/movies',
                redirect: '/library?type=movie'
            },
            {
                path: '/translations',
                redirect: '/queue'
            },
            {
                path: '/translations/:id',
                redirect: (to) => ({ path: `/queue/${String(to.params.id)}` })
            },
            {
                path: '/import',
                component: () => import('@/pages/ImportPage.vue'),
                name: 'import'
            },
            {
                path: '/settings',
                component: () => import('@/cells/settings/pages/SettingPage.vue'),
                name: 'settings',
                // parent-level redirect: the empty-child redirect did not
                // fire on SPA navigation, leaving the tab content empty
                redirect: { name: 'settings-providers' },
                children: [
                    {
                        path: '',
                        redirect: { name: 'settings-providers' }
                    },
                    {
                        path: 'providers',
                        name: 'settings-providers',
                        component: () => import('@/cells/settings/pages/SettingsProvidersPage.vue')
                    },
                    {
                        path: 'sources',
                        name: 'settings-sources',
                        component: () => import('@/cells/settings/pages/SettingsSourcesPage.vue')
                    },
                    {
                        path: 'quality',
                        name: 'settings-quality',
                        component: () => import('@/cells/settings/pages/SettingsQualityPage.vue')
                    },
                    {
                        path: 'system',
                        name: 'settings-system',
                        component: () => import('@/cells/settings/pages/SettingsSystemPage.vue')
                    },
                    {
                        path: 'request-template/:service',
                        name: 'request-template-settings',
                        component: () => import('@/cells/settings/pages/RequestTemplatePage.vue'),
                        props: true
                    },
                    {
                        // legacy settings paths -> grouped panels (bookmarks kept alive)
                        path: 'services',
                        redirect: { name: 'settings-providers' }
                    },
                    {
                        path: 'plugins',
                        redirect: { name: 'settings-providers' }
                    },
                    {
                        path: 'integration',
                        redirect: { name: 'settings-sources' }
                    },
                    {
                        path: 'automation',
                        redirect: { name: 'settings-sources' }
                    },
                    {
                        path: 'mapping',
                        redirect: { name: 'settings-sources' }
                    },
                    {
                        path: 'subtitle',
                        redirect: { name: 'settings-quality' }
                    },
                    {
                        path: 'authentication',
                        redirect: { name: 'settings-system' }
                    },
                    {
                        path: 'tasks',
                        redirect: { name: 'settings-system' }
                    },
                    {
                        path: 'logs',
                        redirect: { name: 'settings-system' }
                    }
                ]
            }
        ]
    }
]

const router = createRouter({
    history: createWebHistory(baseUrl()),
    routes
})

router.beforeEach(async (to) => {
    if (!to.meta.authenticated) {
        return true
    }

    const instanceStore = useInstanceStore()
    const isAuthenticated = await instanceStore.ensureAuthenticated()

    if (!isAuthenticated && to.name !== 'login') {
        return { name: 'login' }
    }

    return true
})

export default router
