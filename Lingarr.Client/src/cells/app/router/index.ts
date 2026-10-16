import { createRouter, createWebHistory, RouteLocationNormalized, RouteLocationRaw, RouteRecordRaw } from 'vue-router'
import { useInstanceStore } from '@/cells/app/stores/instance'
import { baseUrl } from '@/cells/app/utils/baseUrl'

/// Exported for the redirect regression spec — the table itself stays the
/// single source of truth for every route and legacy bookmark redirect.
export const routes: RouteRecordRaw[] = [
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
                // Screen 13: subtitle download history (keyset-paginated
                // subtitle_downloads view). Lives with the library group in
                // the nav; reached from the dashboard backlog KPI too.
                path: '/subtitle-history',
                component: () => import('@/cells/library/pages/SubtitleSearchHistoryPage.vue'),
                name: 'subtitle-history'
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
                // IA 重构批 3:定时任务页收编为自动化 tab 的任务卡,旧书签
                // 落到 #jobs 锚点直接定位任务表
                path: '/schedule',
                redirect: { name: 'settings-automation', hash: '#jobs' }
            },
            {
                // settings sub-drill-downs promoted to top level (same paths,
                // no settings-tab chrome) so every drill-down shares the
                // DrillDownPage skeleton edges — same treatment logs/schedule got
                path: '/settings/plugins',
                name: 'settings-plugins',
                component: () => import('@/cells/settings/pages/PluginsPage.vue')
            },
            {
                path: '/settings/mapping',
                name: 'settings-mapping',
                component: () => import('@/cells/settings/pages/MappingPage.vue')
            },
            {
                path: '/settings/authentication',
                name: 'settings-authentication',
                component: () => import('@/cells/settings/pages/AuthenticationPage.vue')
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
                        component: () => import('@/cells/settings/pages/SettingsMediaPage.vue')
                    },
                    {
                        path: 'automation',
                        name: 'settings-automation',
                        component: () => import('@/cells/settings/pages/SettingsAutomationPage.vue')
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
                    // legacy settings paths -> grouped panels (bookmarks kept alive)
                    {
                        path: 'services',
                        redirect: { name: 'settings-providers' }
                    },
                    {
                        path: 'integration',
                        redirect: { name: 'settings-sources' }
                    },
                    {
                        path: 'subtitle',
                        redirect: { name: 'settings-quality' }
                    },
                    {
                        // logs was promoted from a settings child to a
                        // top-level drill-down route (screen 11) — the old
                        // bookmark follows it. The tasks bookmark lands on
                        // the automation tab's job table (IA refactor batch 3).
                        path: 'tasks',
                        redirect: { name: 'settings-automation', hash: '#jobs' }
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
    routes,
    // Back/forward navigations restore the previous window scroll position
    // (list screens pair this with their own inner-container persistence,
    // e.g. LibraryPage's sessionStorage scroll top). Every other navigation
    // keeps vue-router's default of not scrolling at all — screens own their
    // inner scroll containers.
    scrollBehavior(_to, _from, savedPosition) {
        return savedPosition ?? false
    }
})

router.beforeEach(authGuard)

export default router
