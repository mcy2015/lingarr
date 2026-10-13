# 细胞级重构 · 阶段1 前端 cells 迁移方案(定案)

配套:施工图=[prototype-spec](prototype-spec/README.zh-CN.md),保护网=84 个回归 spec。四决策见重构方案(i18n 双语不动 / 单用户认证(阶段0 已落地) / 后端模块化(阶段2) / DB squash(阶段3))。

## 目标结构

```
src/
  cells/
    kernel/                  # 零业务依赖层:纯 UI 基元、纯 composable、纯工具
      components/  composables/  utils/
    app/                     # 应用装配层:可引用 kernel
      http.ts                # axios 实例 + toApiError + 401 拦截器
      stores/                # 全局态:instance / ui / toast
      (layout/router 骨架后期并入)
    settings/                # 业务 cell:每屏一仓,自含 pages/components/stores/services
    auth/  dashboard/  proofread/  queue/  library/  import/ ...
  components/  pages/  store/  services/   # 未迁移的旧结构原地工作,随屏清空
  types/                     # 共享契约层(前后端 DTO/枚举),长期保留
  i18n/  router/  directives/  assets/     # 全局件,i18n 双语不动(决策)
```

## 依赖规则(硬约束)

1. **kernel** 只许 import:vue / vue-i18n / `@/types` / kernel 内部。禁:store、services、router、任何 cell。
2. **app** 可引用 kernel;**禁**引用任何业务 cell。
3. **业务 cell** 可引用 kernel、app、`@/types`;**cell 之间互不引用**——跨屏共享的 store/service 归 app(或独立共享 cell),不塞进某个屏。
4. 旧目录(`components/common` 等)只许被未迁移的旧代码引用;某屏迁移完成后,其旧文件删除,grep 零残留。

## 迁移机制

- **不用 shim,全局路径重写**:移动文件后全库重写 import 路径(`@/components/common/X.vue` → `@/cells/kernel/components/X.vue`),grep 验证零旧路径残留,`vue-tsc` 兜底(漏一个 import 立即红)。
- **kernel 准入判据(纯度规则)**:组件的 import 集 ⊆ {vue、vue-i18n、@/types、kernel 内部} 才可进 kernel;带 store/service/router 依赖的域组件留在旧位,随所属屏迁移。
- **每批门禁**:vitest(84 spec)+ oxlint + vue-tsc 全绿才落盘;提交粒度=批次。

## 批次顺序与状态

| 批次 | 内容 | 状态 |
|---|---|---|
| F 地基 | kernel(纯 UI/composable/工具)+ app(http/全局 store)+ 全库路径重写 | ✅ 2026-10-13 完成:kernel=20 组件+icons 整目录+4 composable+string 工具;app=http.ts+ui/toast/instance store;88+33 处路径重写零残留 |
| S1 settings | SettingPage+14 子页+features/settings 组件+mapping/schedule store 与 service 集群 → `cells/settings/`(51 文件) | ✅ 2026-10-13 完成:**setting store 与 settingService 归 app**(跨屏全局态);TriggerJob/InputComponent/useValidation 随屏迁入;过渡态 8 处旧 barrel/store 引用留后续批(见 S5/Z) |
| S2 auth | LoginPage/AuthLayout → `cells/auth/`(阶段0 后已很薄) | ✅ 2026-10-13 完成:LoginPage→`cells/auth/pages/`、AuthLayout→`cells/auth/layouts/`,引用 app/api/authService 与 kernel 基元 |
| S3 dashboard | DashboardPage → `cells/dashboard/` | ✅ 2026-10-13 完成:DashboardPage→`cells/dashboard/pages/`,统计/翻译服务走 app/api |
| S4 proofread | ProofreadPage+键盘流 → `cells/proofread/` | ✅ 2026-10-13 完成:ProofreadPage→`cells/proofread/pages/`,useProofreadKeyboard→`cells/proofread/composables/`(含 spec) |
| S5 queue | QueuePage+QueueDetailPage+Translation* 域组件+translationRequest/translate store → `cells/queue/`(translate 跨 library 使用,若仍跨屏则归 app) | ✅ 2026-10-13 完成:页面+Translation* 组件+usePagedList/useTranslationDetailHub → `cells/queue/`;**translate/translationRequest store 均归 app**(library/proofread/设置页共用) |
| S6 library | LibraryPage+MediaDetailPage → `cells/library/` | ✅ 2026-10-13 完成:两页+NoMediaNotification+subtitleService → `cells/library/`(含 LibraryPage spec) |
| S7 import | ImportPage → `cells/import/` | ✅ 2026-10-13 完成:ImportPage+importService → `cells/import/` |
| Z 收尾 | 旧目录清空删除、layout/router 并入 app、services 总桶解散完毕、ci.yml 加 cells 结构守护(可选 lint 规则) | ✅ 2026-10-13 完成:layout 四件(MainLayout/AsideNavigation/ContextMenu/LanguageDropdown)→`cells/app/layout/`;router→`cells/app/router/`;directives→`cells/app/directives/`;baseUrl/date→`cells/app/utils/`、delay→`cells/kernel/utils/`、utils barrel 删除(String.format 副作用由 main.ts 直挂 kernel/string);死码 ActiveTranslationBadge 删除;旧目录(components/composables/utils/directives/router 等)全删零残留;新增 `cells/architecture.spec.ts` 架构守护 4 用例(vitest 88 全绿) |

## 已知跨屏共享物(暂定归 app,迁移时逐一确认)

translate store(bulkTranslate:library 勾选与设置页共用)、Context UI(toast 服务)、`usePagedList`(queue 在用,library 自管)。判据见依赖规则 3。

## 完成态结构树(阶段1 收官,Z 批后)

```
src/
  App.vue                  # 仅 <router-view>
  main.ts                  # 装配:pinia + app/router + app/directives + i18n
                            # + kernel/string 副作用 + app/http
  env.d.ts  assets/  i18n/  types/   # 全局件:i18n 双语不动(决策),types=共享契约层
  cells/
    architecture.spec.ts    # 架构守护(4 用例:kernel 纯度 / app 隔离 / cell 互不引用 / 旧目录零引用)
    kernel/                 # 零业务依赖层
      components/ (+icons/)  composables/  utils/(string、delay)
    app/                    # 应用装配层(唯一可引用全部 cell 的入口 = router 路由表)
      http.ts  api/  stores/  components/  composables/
      layout/  router/  directives/  utils/(baseUrl、date)
    auth/      layouts/  pages/
    dashboard/ pages/
    proofread/ composables/  pages/
    queue/     components/ (+translation/)  composables/  pages/
    library/   components/  pages/  services/
    import/    pages/  services/
    settings/  components/ (+authentication/ +template/)  composables/  pages/  services/  stores/
```

依赖方向恒定:业务 cell → app → kernel;`@/types`/`@/i18n`/`@/assets` 为全局保留件;业务 cell 之间互不引用,由 `cells/architecture.spec.ts` 持久守护。
