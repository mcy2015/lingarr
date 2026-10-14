import { createRouter, createWebHistory, RouteLocationNormalized, RouteLocationRaw, RouteRecordRaw } from 'vue-router'
import { useInstanceStore } from '@/cells/app/stores/instance'
import { baseUrl } from '@/cells/app/utils/baseUrl'

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
            },
            {
                // P0-3 forced password change: reached from the login flow or
                // the router guard while must_change_password is set. The
                // guard funnels every other authenticated route here until the
                // default credentials are replaced.
                path: '/auth/change-password',
                component: () => import('@/cells/auth/pages/ChangePasswordPage.vue'),
                name: 'change-password',
                meta: { authenticated: true }
            }
        ]
    },

    // Main
    {
        path: '/',
        component: () => import('@/cells/app/layout/MainLayout.vue'),
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
                component: () => import('@/cells/import/pages/ImportPage.vue'),
                name: 'import'
            },
            // ── drill-down screens (prototype screens 09–12): top-level
            // routes without a sidebar entry; the source screen is reached
            // through the pb-head back button.
            {
                path: '/request-template/:service',
                name: 'request-template',
                component: () => import('@/cells/settings/pages/RequestTemplatePage.vue'),
                props: true
            },
            {
                path: '/logs',
                name: 'logs',
                component: () => import('@/cells/settings/pages/LogsPage.vue')
            },
            {
                path: '/schedule',
                name: 'schedule',
                component: () => import('@/cells/settings/pages/SchedulePage.vue')
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
                        // legacy settings paths -> grouped panels (bookmarks kept alive)
                        path: 'services',
                        redirect: { name: 'settings-providers' }
                    },
                    {
                        // external plugins drill-down (prototype screen-06 A):
                        // kept as its own page behind the providers group row
                        path: 'plugins',
                        name: 'settings-plugins',
                        component: () => import('@/cells/settings/pages/PluginsPage.vue')
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
                        // path mapping drill-down (prototype screen-06 B):
                        // kept as its own page behind the media sources card row
                        path: 'mapping',
                        name: 'settings-mapping',
                        component: () => import('@/cells/settings/pages/MappingPage.vue')
                    },
                    {
                        path: 'subtitle',
                        redirect: { name: 'settings-quality' }
                    },
                    {
                        // authentication management drill-down (screen-06 D):
                        // auth toggle + API key management page
                        path: 'authentication',
                        name: 'settings-authentication',
                        component: () => import('@/cells/settings/pages/AuthenticationPage.vue')
                    },
                    {
                        // logs / schedule were promoted from settings children
                        // to top-level drill-down routes (screens 11/12) — the
                        // old bookmarks follow them
                        path: 'tasks',
                        redirect: { name: 'schedule' }
                    },
                    {
                        path: 'logs',
                        redirect: { name: 'logs' }
                    },
                    {
                        path: 'request-template/:service',
                        redirect: (to) => ({
                            name: 'request-template',
                            params: { service: String(to.params.service) }
                        })
                    }
                ]
            }
        ]
    }
]

/**
 * Global guard (P0-2/P0-3): authenticated routes require a session, and
 * while the server-side must_change_password flag is set every authenticated
 * route except the forced change screen funnels into it.
 */
export const authGuard = async (to: RouteLocationNormalized): Promise<boolean | RouteLocationRaw> => {
    if (!to.meta.authenticated) {
        return true
    }

    const instanceStore = useInstanceStore()
    const isAuthenticated = await instanceStore.ensureAuthenticated()

    if (!isAuthenticated && to.name !== 'login') {
        return { name: 'login' }
    }

    // P0-3: until the seeded default credentials are replaced, everything
    // except the change screen itself funnels into the forced change.
    if (instanceStore.mustChangePassword && to.name !== 'change-password') {
        return { name: 'change-password' }
    }

    return true
}

const router = createRouter({
    history: createWebHistory(baseUrl()),
    routes
})

router.beforeEach(authGuard)

export default router
