import { createRouter, createWebHistory, RouteRecordRaw } from 'vue-router'
import { useInstanceStore } from '@/store/instance'
import { baseUrl } from '@/utils/baseUrl'

const routes: RouteRecordRaw[] = [
    // Auth
    {
        path: '/auth',
        component: () => import('@/components/layout/AuthLayout.vue'),
        children: [
            {
                path: '/auth/onboarding',
                component: () => import('@/pages/OnboardingPage.vue'),
                name: 'onboarding',
                meta: { authenticated: false }
            },
            {
                path: '/auth/login',
                component: () => import('@/pages/LoginPage.vue'),
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
                component: () => import('@/pages/DashboardPage.vue'),
                name: 'dashboard'
            },
            {
                path: '/library',
                component: () => import('@/pages/LibraryPage.vue'),
                name: 'library'
            },
            {
                path: '/queue',
                component: () => import('@/pages/QueuePage.vue'),
                name: 'queue'
            },
            {
                path: '/queue/:id',
                component: () => import('@/pages/QueueDetailPage.vue'),
                name: 'queue-detail',
                props: true
            },
            {
                path: '/proofread/:id',
                component: () => import('@/pages/ProofreadPage.vue'),
                name: 'proofread',
                props: true
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
                component: () => import('@/pages/SettingPage.vue'),
                name: 'settings',
                children: [
                    {
                        path: '',
                        redirect: { name: 'settings-providers' }
                    },
                    {
                        path: 'providers',
                        name: 'settings-providers',
                        component: () => import('@/pages/settings/SettingsProvidersPage.vue')
                    },
                    {
                        path: 'sources',
                        name: 'settings-sources',
                        component: () => import('@/pages/settings/SettingsSourcesPage.vue')
                    },
                    {
                        path: 'quality',
                        name: 'settings-quality',
                        component: () => import('@/pages/settings/SettingsQualityPage.vue')
                    },
                    {
                        path: 'system',
                        name: 'settings-system',
                        component: () => import('@/pages/settings/SettingsSystemPage.vue')
                    },
                    {
                        path: 'request-template/:service',
                        name: 'request-template-settings',
                        component: () => import('@/pages/settings/RequestTemplatePage.vue'),
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
                    },
                    {
                        path: 'telemetry',
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
