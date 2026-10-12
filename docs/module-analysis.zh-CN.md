# Lingarr 全模块分析报告（重构准备）

> 分析日期：2026-10-12 · 分支：`feat/visual-refresh`（head `624567d`）
> 范围：Lingarr.Client（前端）、Lingarr.Core / Server / Contracts / Migrations（后端）、Tests、CI 与工具链
> 方法：全量目录勘察 + 逐文件抽样阅读 + 脚本统计；关键数字（57 个 Promise 包装、18 个重试循环、4 处 `new HttpClient()`、Hub 无鉴权、i18n 490/490 键）均经 grep 复核。

---

## 1. 总览

### 1.1 项目构成

| 项目 | 类型 | 规模 | 职责 |
|---|---|---|---|
| `Lingarr.Client` | Vue 3 SPA | ~13,300 LOC（111 个 SFC） | 全部 UI：仪表盘、媒体库、翻译队列、设置、Onboarding |
| `Lingarr.Server` | ASP.NET Core (net10.0) | 18,538 LOC / 218 文件 | 宿主 + 控制器 + 服务 + Hangfire 作业 + SignalR + 10 个翻译 Provider + 插件加载器 + 字幕解析器 |
| `Lingarr.Core` | 类库 | 1,102 LOC | EF 实体（13 个）、DbContext、`SettingKeys`、数据库连接配置、日志格式化 |
| `Lingarr.Contracts` | 类库 | 322 LOC | **插件 ABI**（v1 版本门）：`ITranslationService` 等接口 + 插件清单模型 |
| `Lingarr.Migrations` | 类库 | 1,211 LOC | FluentMigrator 迁移 M0001–M0022（非 EF 迁移），一套迁移兼容 SQLite/MySQL/Postgres |
| `Lingarr.Server.Tests` | xUnit v3 + Moq | 5,818 LOC / 212 个测试方法 | 见 §5 |
| `Lingarr.Migrations.Tests` | xUnit v3 + Testcontainers | 528 LOC | 真实 MySQL/Postgres 容器跑全量迁移 |

引用关系：`Server → {Core, Contracts, Migrations}`；`Core → Contracts`（仅因实体 `TranslationRequestLine` 存了一个 `MatchTier` 字段——域模型意外依赖插件 ABI）；`samples/CloudflarePlugin → Contracts`（参考插件）。

### 1.2 关键技术事实（纠正直觉的地方）

- **调度器是 Hangfire 1.8.21，不是 Quartz**。6 个队列（movies/shows/system/translation/webhook/default），`MAX_CONCURRENT_JOBS` 默认 **1**。
- **认证是 Cookie + `X-Api-Key`，不是 JWT**。csproj 里引用了 `Microsoft.AspNetCore.Authentication.JwtBearer` 但源码从未使用（死引用）。
- **翻译服务共 10 个内置 + 插件通道**：OpenAI、Anthropic、Gemini、Mistral、xAI、DeepSeek、DeepL、LocalAI/Ollama、LibreTranslate、GTranslate 包装的 Google/Bing/Microsoft/Yandex 免费引擎。
- **schema 管理是 FluentMigrator，EF Core 只做查询侧**。设置项存 DB 行，所以 22 个迁移里 11 个是"加设置"的种子迁移。
- 密码哈希用 ASP.NET `KeyDerivation.Pbkdf2`（不是 BCrypt——`BCrypt.Net-Next` 也是死引用）。

### 1.3 模块地图（Lingarr.Server）

```
Lingarr.Server/
├── Controllers/      17 个，~1,930 LOC，统一 [ApiController]+[LingarrAuthorize]+Route
├── Services/         根目录 20 个服务（含 1,047 LOC 的 TranslationRequestService 巨型服务）
│   ├── Translation/  10 个 provider + Base 骨架 + TranslationFactory（~4,100 LOC，重复度最高）
│   ├── Sync/         Show/Season/Episode/Movie/Image 同步（~490 LOC，分层清晰）
│   ├── Integration/  Radarr/Sonarr HTTP 包装（API key 走 ?apikey= 查询串）
│   ├── Plugins/      PluginLoader/Registry + 13 个内置 manifest
│   └── Subtitle/     SrtParser/SsaParser/SrtWriter/SsaWriter/Formatter（~970 LOC）
├── Jobs/             9 个 Hangfire 作业（TranslationJob 378 / AutomatedTranslationJob 333 …）
├── Hubs/             3 个 SignalR Hub（仅 JoinGroup/LeaveGroup；⚠️ 未挂鉴权）
├── Listener/         SettingChangedListener（async void 事件处理）
├── Extensions/       ServiceCollectionExtensions(375) + ApplicationBuilderExtensions
├── Models/           ~60 个 DTO，9 个子目录（与 Contracts 存在重复）
└── Statics/          4 个语言 JSON，硬编码绝对路径 /app/Statics/…
```

---

## 2. 前端模块分析（Lingarr.Client）

### 2.1 技术栈与构建

Vue 3.5 + Pinia 4 + vue-router 5 + vue-i18n 11 + axios + @microsoft/signalr；Vite 8、TS strict、Tailwind 4、**oxlint/oxfmt**（非 ESLint/Prettier）。`vue-tsc --noEmit` 进 build，生产 drop console。整体工具链非常新且干净。

问题：
- `shims-vue.d.ts` 把所有 SFC 声明成 `DefineComponent<{},{},any>`——削弱了 vue-tsc 的按组件 prop 检查，属冗余甚至掩盖错误。
- `main.ts` 用无意义的 `new Promise().then().then()` 链注册指令。
- Tailwind **双通道配置**：`@tailwindcss/vite` 插件 + 残留的 `postcss.config.mjs`（`@tailwindcss/postcss`）；`tailwind.config.mjs` 是 v3 风格 JS 配置，v4 可改为 CSS-first `@theme`。`darkMode:'class'` 全项目 0 个 `dark:` 工具类，纯遗留。

### 2.2 `src/types/` 不是死代码——是契约层（命名误导）

`src/types/`（1,058 LOC）实为**类型 + 常量骨干**，~90 个文件引用它：

| 文件 | 作用 |
|---|---|
| `types/media/index.ts` | 领域模型 `IShow/IMovie/ISubtitle/ITranslationRequest`、`MEDIA_TYPE`、`TRANSLATION_STATUS` |
| `types/setting.ts` | `SETTINGS` 65 个键常量 + `ISettings`（70 字段）+ `ENCRYPTED_SETTINGS` |
| `types/services.ts` | `Services` 注册表 + 15 个服务接口 |
| `types/store/*` | 各 Pinia store 的形状接口（真实 store 在 `src/store/`） |
| `types/components/*` | 输入类型/校验枚举/PLACEHOLDER 目录 |
| `types/composables/signalR.ts` | SignalR 事件类型映射 |

问题：① 命名让人误以为遗留垃圾，建议改名 `src/types/`（或 `contracts/`）（✅ 已于 2026-10-12 落地为 `src/types/`）；② 大桶导入（`@/types`）与深路径导入混用，桶文件再导出自身形成环状卫生问题；③ 残留过时契约——`LOCALE = { ENGLISH:'en', DUTCH:'nl' }` 与 `ISettings.locale` 已无人使用（i18n 现在自管 en/zh-CN，存 `lingarr-locale`），是 zh-CN 改造前的死代码。

### 2.3 路由与页面

21 条路由全部懒加载；双布局（`/auth` → 登录/Onboarding，`/` → 主壳 + 11 个设置子页）。守卫 `beforeEach` 每次导航调 `ensureAuthenticated()` → `GET /api/auth/authenticated`。

超大页（>200 LOC）：`TranslationDetailPage.vue` **519**（详情 + SignalR 实时流 + 整套内联校对编辑状态机）、`TranslationPage.vue` 284、`LogsPage.vue` 272、`MoviePage.vue` 253、`ShowPage.vue` 240、`OnboardingPage.vue` 215。`MoviePage` 与 `ShowPage` 是 90% 双胞胎（diff 258 行几乎全是 movie→show 改名）。

### 2.4 状态管理（10 个 Pinia store，914 LOC）

问题清单：
- **选择/分页/过滤状态机三份拷贝**：`store/movie.ts`、`store/show.ts`、`store/translationRequest.ts` 各自实现 `clearSelection/toggleSelectAll/toggleSelect/setFilter+fetch`（movie 与 show 相似度 ~85%）。
- `store/auth.ts` 把**用户编辑表单状态**（editUsername/editPassword/3 个 valid 标志 + 30 行 `canSave` getter）放进了全局 store——表单状态是组件关注点。
- `store/setting.ts` 对 `source_languages/target_languages` JSON 字段在 getter 和 `applySettingsOnLoad` **两处、两种方式**手工解析。
- `store/instance.ts`：`const localStorage = useLocalStorage()` **遮蔽全局**；主题同时用 `documentElement.className` 和两个布局根 div 的 class **双机制**应用；持久化键 `lingarr_*`（下划线）与 i18n 的 `lingarr-locale`（连字符）两套约定。
- 持久化无插件，全手写。

### 2.5 服务层——最系统性的缺陷

`services/` 16 个文件（943 LOC），**57 处同一反模式**（已 grep 复核）：

```ts
return new Promise((resolve, reject) => {
    http.get(...).then(r => resolve(r.data)).catch((e: AxiosError) => reject(e.response))
})
```

后果：
1. ~400 LOC 可被 `return http.get(url).then(r => r.data)` 取代的样板；
2. **reject 的是 AxiosResponse 而非 Error**，`instanceof Error` 恒为 false → `StatisticsComponent.vue:171,203` 的错误 UI **静默永不显示**（用户可见 bug）；同时 `LoginPage/OnboardingPage/auth.ts` 读 `err?.data?.message` 又依赖这个非 Error 约定——三套错误约定并存；
3. `mediaService.movies` 与 `shows` 除资源段外逐字节相同；`pluginService` 4 份同构 GET 包装。

### 2.6 组件（111 个 SFC，6,944 LOC）

- 超 200 LOC 组件 7 个（见 §2.3 + `RequestTemplate` 267、`ServicesSettings` 254、`UserManagement` 244、`SubtitleSettings` 232、`LanguageChart` 218、`ValidationSettings` 216、`StatisticsComponent` 216）。
- **42 个手写单图标 SFC**（531 LOC + 数百行 import），应收敛为一个 `<Icon name>` 或换 `lucide-vue-next`。
- **3 个列表页结构性三胞胎**（Movie/Show/Translation）：工具栏（搜索+排序+刷新+分页）与表格→卡片塌缩模式重复三次，选择状态机同样三份（配合 §2.4）。
- `ServicesSettings.vue` 一段 140 字符的图标按钮 class 串**逐字重复 4 次**。
- 破坏性操作用原生 `window.confirm` 3 处（TranslationPage:276、UserManagement:235、StatisticsComponent:192），全项目仅 `DirectoryModal` 一个真模态。
- **XSS 面**：`directives/show-title.ts` 用未转义 `textContent` 构建 `el.innerHTML`（标题来自 Sonarr，外部数据）；`DynamicPluginForm.vue` `v-html="manifest.description"`（插件提供）。`highlight.ts` 同模式（低危）。
- 杂项：`CardComponent` 空 description 也渲染占位 `<p>`（永久缝隙）；`MainLayout` 的 `themeDropdown` ref 写而不读。

### 2.7 样式体系（视觉改版后现状）

- Token 定义在 `assets/style.css` `:root`（Timeline 默认主题），`tailwind.config.mjs` 映射为语义工具类，与 `docs/DESIGN.md` 一致。13 套旧主题以 class 覆写共存于同一文件。
- **原始调色板迁移基本完成**：`.vue` 中已无裸 hex；仅剩 `LogsPage.vue:141,155` 的 `text-orange-500` 等两处日志配色未走 `warning` token。
- **语义错用的 token 残留**（机械替换后遗症）：`border-secondary` 当发丝线用（`SeasonTable.vue:4`、`LogsPage` 多处，规范应 `border-line`）；`text-accent-content` 当正文用（`SeasonTable.vue:16`、`ShowPage.vue:35`，该 token 本意是 accent 填充上的文字）。

### 2.8 i18n

en/zh-CN 各 **490 键，完全同步**（脚本核验）。模板内无硬编码用户可见字符串（仅 `index.html <title>`）。两处**语言切换后文案冻结**的响应性 bug：`SettingPage.vue:95-137` 菜单和三个列表页的排序选项数组在 setup 时求值 `t()`，切语言需重挂载才更新——应改为 key + `computed`（`AsideNavigation` 已是正确范式）。

### 2.9 Composables / directives / utils

`useSignalR`（120 LOC，模块级单例 + 类型化事件表 + 重连，**全前端工程质量最好的部分**，4 处消费）；`useJsonTree`/`useTemplatePreview` 仅模板构建器用；`useValidation`（cron/url/number）经 `InputComponent` 复用良好；`useDebounce` 3 页用。`utils/string.ts` **全局猴补 `String.prototype`**（`'url'.addParams()`），应改普通函数。`LogsPage` 内 ~70 LOC 日志着色/导出逻辑应抽 composable。

---

## 3. 后端模块分析

### 3.1 Lingarr.Core

实体 13 个 + `LingarrDbContext`（全局 DateTimeOffset→DateTime 转换器；⚠️ **同步 `SaveChanges` 未覆写**，同步保存会静默跳过时间戳）。`SettingKeys`（148 行嵌套常量）即"设置 schema"。**连接串逻辑两处维护**：`DatabaseConfiguration` 与 `ServiceCollectionExtensions.ConfigureMySqlStorage/ConfigurePostgresStorage`（L300–350）各自从环境变量拼一遍。`Program.cs` 是**空文件**。

### 3.2 Services 根目录——`TranslationRequestService`（1,047 LOC 巨型服务）

注入 11 个依赖 + `static ConcurrentDictionary<int, CancellationTokenSource> _asyncTranslationJobs`。混杂职责：

1. 队列管理（enqueue/retry/resume/cancel；resume 时直接伸进 `JobStorage.Current.GetMonitoringApi()` 枚举并删除 Hangfire 队列项——深度基础设施耦合）；
2. CRUD/查询（分页/搜索/实体→DTO 映射内联）；
3. 批量自动化编排（`CreateBulkRequest`/`ProcessMediaSubtitles` 与 `MediaSubtitleProcessor` 逻辑重复）；
4. SignalR 实时推送；
5. 校对（4 个方法 + 工厂探测）；
6. **第二条完整翻译管线** `TranslateContentAsync`（~250 行，`/translate/content` 路径）：自建请求行、自管 CTS、内联 `new SubtitleTranslationService(...)`、绕过 EF 的 `ExecuteUpdateAsync`——与 `TranslationJob.Execute` 是语义有微妙分歧的双管线（resume 仅 job 路径可用）；
7. 杂项媒体修复。

其余大件：`SubtitleTranslationService`（621，行级翻译引擎，链式回退/上下文/进度，被 3 处 `new` 而非 DI，本质是函数对象；有 910 行测试锚定）、`SubtitleService`（490，文件 IO 门面，`new` 解析器/写入器）、`MediaService`（490，分页查询+级联写+按需 Arr 同步混装）、`StartupService`（312，第三个知道完整 provider 清单的地方）。`ProgressService` 是"会写 DB 的进度上报器"。`MediaSubtitleProcessor` 在 scoped 实例上持有每次调用可变的 `_media/_mediaType/_hash` 字段。

### 3.3 Services/Translation——重复度最高的区域（~4,100 LOC）

6 个 LLM provider（OpenAI 497 / Anthropic 551 / Gemini 503 / Mistral 501 / xAI 499 / LocalAI 635）骨架逐字复制：

- **完全相同的 35 行重试循环 × 18 处**（每文件 3 处，`for (var attempt = 1; attempt <= _maxRetries; attempt++)`，已复核）——指数退避、429/503 捕获、OCE 透传、TranslationException 包装；
- 10 份近似的 `InitializeAsync`（SemaphoreSlim 双检锁，~60 行/份，只差读哪些 SettingKeys、设哪些头）；
- `CompleteWithXxxApi` POST 模板 JSON + 响应反序列化。

**拷贝漂移实锤**：`DeepSeekService`（228 行）**一处重试都没有**——同一批兄弟，一个有 3 份重试一个 0 份，读着重试配置却不生效，是潜在 bug。

HttpClient 卫生差：`OpenAiService.cs:43`（ctor 兜底 `new HttpClient()`）、`:439`、`XAiService.cs:440`、`MistralService.cs:442`（`GetModels` 每次 new，永不 dispose）——socket 耗尽风险（已复核行号）。仅 `LibreService/ImageService/LingarrApiService/GTranslatorService` 正确使用 `IHttpClientFactory`。

`TranslationFactory`（153）用巨型 switch **手工 `new` 所有 provider**。`GTranslatorService` 硬编码 `/app/Statics/*_languages.json` 绝对路径——非容器本地开发直接挂。

**统一方案**（收益最大的单项重构）：初始化组件 + 共享 `ResilientInvoker`（吃掉 18 个循环）+ 一个覆盖 OpenAI/DeepSeek/Mistral/xAI/LocalAI 五胞胎的 ChatCompletions 客户端（这 5 个 ~85% 相同，可塌缩为配置记录）；Anthropic/Gemini 保留自定义请求映射；DeepL/GTranslate 走 SDK 但继承共享重试。现有 Gemini/LocalAI 测试可锚定行为。`samples/CloudflarePlugin` 里还第 4 种方式重写了同一重试循环——共享韧性抽象缺失的又一证据。

### 3.4 Sync / Integration / Plugins / Subtitle

- **Sync**（6 文件 ~490 LOC）：分层干净，风险低。
- **Integration**：`IntegrationService` 通用 GET + JSON；**API key 以 `?apikey=` 明文进查询串**（会落下游日志）；注入裸 `HttpClient`，无命名客户端、无超时策略。
- **Plugins**：`PluginLoader`（198）扫 `PLUGINS_PATH` DLL，major==1 版本门，keyed 注册；在容器构建前、用私有 LoggerFactory 启动——顺序耦合。13 个内置 provider 的 manifest 长在 Server（~450 LOC 纯声明），内置件走 manifest 系统但**不走** loader 路径（`IsBuiltIn` 靠"有没有源文件"推断——脆弱）。
- **Subtitle**（~970 LOC）：`SrtParser.ParseStream` **吞掉所有异常写 `Console.WriteLine` 并返回空列表**——损坏文件静默变"0 条字幕"。SRT 解析器无测试（仅 SSA 有）。

### 3.5 Jobs（Hangfire，9 文件 ~1,860 LOC）

- `TranslationJob`（378）：**主翻译管线**，含断点续传（`TranslationRequestLines` 回放）、批量/单行分支、后处理（重叠修复/翻译者信息/SSA 样式剥离）、统计、状态机。错误路径里 4 个横切调用（改行状态+SignalR+调度状态+保存）在成功/失败/取消三处重复。⚠️ 失败处理器里还可能在取消导致的失败后再次 `SaveChangesAsync`。**零测试**。
- `AutomatedTranslationJob`（333）：电影/剧集轮换游标存在 **IMemoryCache（NeverRemove）**——进程本地状态伪装持久化，重启即丢轮换周期；每轮把所有 include 的媒体载入内存。
- `ProofreadJob` 226 / `StatisticsJob` 125 / `WebhookJob` 109（Arr payload→同步→处理器，Retry 3）/ `SyncShow/SyncMovieJob` 72 / `CleanupJob` / `TelemetryJob`。
- `JobContextFilter` 用 **AsyncLocal 静态**传作业名/id（Hangfire 本可用 `PerformContext` 注入）。

### 3.6 Hub / 鉴权 / DI / 模型

- **3 个 SignalR Hub 完全没挂鉴权**（`MapHubs` 无 `RequireAuthorization`，已复核）——API 层有 `LingarrAuthorize`，实时通道裸奔。
- `LingarrAuthorizeAttribute` 是服务定位型过滤器：每请求查 DB/缓存读 onboarding/auth 开关；未用框架 `[Authorize]` 管线。
- `AuthController`（313）**直接注入 `LingarrDbContext` 做用户 CRUD**，绕过 `IAuthService`（持久化双头）。API key 用普通 `==` 比较（**非常数时间**）；最短密码 4 位；无角色——任何登录用户可改/删任意用户含改其密码；`"Cookies"` scheme 魔法串重复 4 次；`Signup` 在关掉 auth 时允许匿名建用户。
- DI：`ISubtitleWriter` 注册两次（SrtWriter 后被 SsaWriter 覆盖，实际死代码——`SubtitleService` 是 `new` 出来的）；`SettingService`（scoped）在**构造函数里**订阅单例 listener 的事件——每个 scope 实例重复挂订阅。
- `Models/`（~60 文件）与 Contracts 重复：`BatchTranslatedLine` ≈ `Contracts BatchSubtitleLine` 同形状两类型，6 个 provider 文件同时 import 两个命名空间；分页端点直接返回 EF 实体（`PagedResult<TranslationRequest>`，把 `StackTrace` 都暴露出去）而单条端点映射 DTO——不一致。

### 3.7 横切关注点

- **无全局异常中间件**，每控制器/作业自滚 catch；`TranslateController.BulkTranslate/TranslateContent` 兜底 `catch(Exception) → 500 + ex.Message`（泄内部信息）；错误即数据（`ModelsResponse.Message`）与 throw/null 返回混用。
- **async void × 2**（`SettingChangedListener.OnSettingChanged:33`、`ScheduleInitializationService.OnApplicationStarted:36`，已复核）——未观察异常可崩进程。`SettingChangedListener` 还直接 `ExecuteSqlRaw("UPDATE movies SET media_hash = ''")`。
- 日志：自制管道标记 `|Green|...|/Green|` 的彩色控制台 logger；`SrtParser`/`ApplicationBuilderExtensions` 用裸 `Console.WriteLine`（8 处）；插值串与结构化模板混用。
- 配置：环境变量读取散落 ≥4 个文件，无 IOptions、无绑定期校验；硬编码 `/app/...` 路径多处（语言 JSON、DataProtection keys、Hangfire SQLite）。
- 静态/全局状态：`_asyncTranslationJobs`（取消状态在单进程内，与共享 Hangfire DB 的多实例部署冲突）、`MigrationConfiguration` 静态连接串、`DeepLService` 静态 MemoryCache、AsyncLocal 作业上下文。

### 3.8 Migrations & Contracts

- 迁移质量不错：一套 FluentMigrator 兼容三库 + Testcontainers 全量验证（社区项目少见）。弱点：`MigrationConfiguration` 静态字段存连接串；每次启动同步跑迁移；为加一个设置项就要发一个迁移（22 个里 11 个种子）——**设置 schema 进 DB 行**的架构决定使 schema 演进与数据播种耦合。
- Contracts 是认真的插件 ABI（版本门 + manifest + 设置表单模型 + 参考插件），v1 约束：major-only 门、`AssemblyLoadContext.Default` 不可卸载、Core 反向引用。

---

## 4. 测试与工程质量

### 4.1 覆盖矩阵（212 个测试方法）

| 已覆盖（较扎实） | 未覆盖（确认缺失） |
|---|---|
| `SubtitleTranslationService` 引擎（910 LOC 测试：链回退/上下文/取消） | **`TranslationJob`（378 行主管线）**、ProofreadJob/WebhookJob/SyncJob/CleanupJob |
| Gemini + LocalAI 两个 provider（684+225） | 其余 8 个 provider、`TranslationFactory` |
| `/translate/content` 与 `/translate/line` 控制器（673+251，真 EF InMemory） | `TranslationRequestService`（1,047 LOC）、`SubtitleService`、`MediaService` 查询路径 |
| 字幕：SSA 解析/写回/格式化、MediaSubtitleProcessor 5 件套 | **SrtParser/SrtWriter（SRT 无测试）** |
| MediaService include 级联、StartupService、语言码、自动化轮换 | **`AuthService`/`AuthController`（哈希/校验/API key 全无测试）**、SettingService、调度、PluginLoader、集成服务 |
| 迁移三库真容器验证 | 前端：**0 测试**（无 vitest 依赖） |

两条最高风险未测路径：**主翻译作业**与**认证**。

### 4.2 CI 与工具链

- GitHub Actions：backend test（`--filter Category!=Integration`）、migrations test、frontend（node 24 lint+build）、docker build（main+release）、docs。
- `.githooks/pre-commit`：`docker exec Lingarr.Client npm run lint` + `dotnet test`——**依赖本地 dev 容器在跑**，否则提交失败；未配 `core.hooksPath` 的话根本不生效。
- 死引用待清：`BCrypt.Net-Next`、`JwtBearer`（Server.csproj）；`Core/Program.cs` 空文件；前端 `postcss.config.mjs` + `darkMode`。

---

## 5. 重构机会清单（按优先级）

图例：价值/风险 ● 高 ◐ 中 ○ 低。所有路径以仓库根为基准。

### P0 — 低风险高回报，可直接做

> ✅ **已于 2026-10-12 全部完成**（未提交）。后端 build 通过 + 262/262 测试绿；前端 lint/vue-tsc/build 绿。净减 ~429 行。另修复：Lingarr.Core 误配 `OutputType=Exe`（模板残留）。

| # | 事项 | 位置 | 价值/风险 | 说明 |
|---|---|---|---|---|
| 1 | **扁平化服务层 + 统一错误** | `Client/src/services/*.ts`（57 处 `new Promise`） | ●/○ | 顺带修复 `StatisticsComponent` 错误 UI 永不显示的 bug；引入单一 `ApiError`，保留 `data.message` 兼容 |
| 2 | **HttpClient 卫生** | `OpenAiService.cs:43,439`、`XAiService.cs:440`、`MistralService.cs:442` | ●/○ | 换 `IHttpClientFactory` 命名客户端，防 socket 耗尽 |
| 3 | **安全三连** | `AuthService.ValidateApiKey`（常数时间比较）、密码最短长度、`MapHubs` 挂鉴权 | ●/○ | `CryptographicOperations.FixedTimeEquals`；Hub 现在完全裸奔 |
| 4 | **XSS 收口** | `directives/show-title.ts`、`highlight.ts`、`DynamicPluginForm.vue` | ●/○ | 转义或 DOM 构建；插件描述 sanitize |
| 5 | **吞异常修复** | `SrtParser.cs:30`（Console.WriteLine→ILogger+抛出） | ◐/○ | 损坏文件静默变空列表 |
| 6 | **死代码/死配置清理** | `BCrypt.Net-Next`+`JwtBearer` 引用、`Core/Program.cs`（空）、`ISubtitleWriter` 双注册、`postcss.config.mjs`、`darkMode`、`LOCALE/ILocale` 死契约、`themeDropdown` | ◐/○ | 全部 grep 确认无消费 |
| 7 | **i18n 响应性修复** | `SettingPage.vue:95-137`、三个列表页排序数组 | ◐/○ | `t()` 移入 computed；语言切换即时生效 |
| 8 | **async void × 2** | `SettingChangedListener.cs:33`、`ScheduleInitializationService.cs:36` | ◐/○ | 改 async Task + 安全触发 |

### P1 — 核心结构重构，建议带测试推进

> ✅ **已于 2026-10-12 全部完成**（八项，9 个提交 4638ca5..18889b2）。后端 290/290 测试绿，前端 lint/build/vitest 10/10 绿。遗留说明：#9 中 LocalAI 批量保留其特有的解析重试分支；DeepL/Libre/GTranslator 的 SDK 重试循环未统一（语义不同）；#10 采用外观模式保持消费者零改动；#11 中取消语义对齐为内容路径旧行为（取消即标 Cancelled，不再产出部分结果）。

| # | 事项 | 位置 | 价值/风险 | 说明 |
|---|---|---|---|---|
| 9 | **翻译 provider 骨架统一**（后端最大收益） | `Services/Translation/*`（18 个重试循环、10 份 InitializeAsync、5 个 chat-completions 克隆） | ●/◐ | 共享初始化 + `ResilientInvoker` + ChatCompletions 客户端；顺带修 DeepSeek 零重试 bug；以 Gemini/LocalAI 现有测试为锚 |
| 10 | **拆分 TranslationRequestService**（1,047 LOC / 11 依赖 / 静态 CTS 字典） | → QueryService + QueueService（Hangfire 耦合隔离到 `IJobQueue` 后面）+ ProofreadService + ContentTranslationService | ●/◐ | 拆完才能做 #11 |
| 11 | **统一双翻译管线** | `TranslationJob.Execute`（378）vs `TranslateContentAsync`（~250） | ●/◐ | 抽 `ITranslationPipeline` + 文件/内容两种 source 适配器；消除 resume 语义分歧 |
| 12 | **列表页三胞胎收敛** | `MoviePage/ShowPage/TranslationPage` + `store/movie|show|translationRequest` | ●/◐ | `useMediaSelection`/`usePagedList` composable + 共享工具栏/表格脚手架 |
| 13 | **`src/ts/` → `src/types/`**（✅ 重命名已于 2026-10-12 完成） | 90 个引用文件 | ◐/○ | 重命名已完成；剩余：按域拆模块 + 只许桶导入 |
| 14 | **图标收敛** | 42 个 SFC → `<Icon name>` 或 lucide-vue-next | ◐/○ | −531 LOC + 数百行 import |
| 15 | **Auth 收拢强化** | `AuthController` 直接用 DbContext 做用户 CRUD → 移入 `IAuthService`；`LingarrAuthorizeAttribute` → `[Authorize]` policy + ApiKey AuthHandler | ●/◐ | 消除持久化双头；鉴权进框架管线（覆盖 Hub） |
| 16 | **测试补两条命脉** | `TranslationJob`（resume/取消/统计）与 `AuthService`（哈希轮转/API key/用户规则） | ●/◐ | 现有 EF InMemory + mock HttpMessageHandler 基建可直接复用；前端引入 Vitest 先钉纯逻辑（选择机/校验/服务解包） |

### P2 — 债务清理与打磨

> ✅ **已于 2026-10-12 基本完成**（#17/18/19/20/21/22/23/24，提交 ec0b72c..bf4a216 + 5b9fdc8）。后端 290/290 绿，前端 lint/build/vitest 11/11 绿。其中 #21 的 token 语义清理由用户并行提交（6bbf664）。**遗留两项**：①JobContextFilter AsyncLocal→PerformContext（涉及 9 个作业签名，单实例下现状安全，暂缓）；②请求 record 合并仅做了 SignupRequest（其余形状确实不同，非重复）。

| # | 事项 | 位置 | 说明 |
|---|---|---|---|
| 17 | DTO 收拢：`BatchTranslatedLine` 等并入 Contracts；分页端点不再泄 EF 实体（`StackTrace`）；~25 个单字段 request record 分组 | `Server/Models` | 机械改动，为 API 版本化解锁 |
| 18 | 静态/全局状态清理：`_asyncTranslationJobs`→DB 行、自动化轮换游标出 IMemoryCache、`MigrationConfiguration` 静态串、AsyncLocal→`PerformContext`、`/app/...` 硬编码→`ContentRootPath` | 多处 | 同时是**多实例部署**前置条件 |
| 19 | 连接串单一来源：合并 `ServiceCollectionExtensions` 两处存储配置进 `DatabaseConfiguration` | `SCE.cs:300-350` | 现在两处独立拼串 |
| 20 | 全局异常中间件 + ProblemDetails；删 `TranslateController` 兜底 `500+ex.Message` | Controllers | 统一错误面 |
| 21 | 前端杂项：`TranslationDetailPage`（519）拆校对面板/SignalR 组合式；`auth.ts` 表单状态出 store；三套通知组件合一；`String.prototype` 猴补改函数；token 语义清理（`border-secondary`→`border-line`、`text-accent-content` 滥用、LogsPage 橙色→warning）；`shims-vue.d.ts` 删除 | Client | 一批小刀 |
| 22 | 日志统一：`Console.WriteLine`×8 → ILogger；彩色标记与结构化模板二选一 | Server | 顺带把 `InMemoryLogger` 环形缓冲接统一管线 |
| 23 | 插件系统打磨：内置 manifest 与外置 loader 路径统一；`IsBuiltIn` 改显式声明；`SettingService` 构造函数订阅事件改显式生命周期 | Plugins | 降低 v2 ABI 变更成本 |
| 24 | pre-commit 钩子改造（去 docker exec 依赖或改 CI-only）；`SETTING.LOCALE` 等僵尸设置项下线迁移 | 根目录 / Migrations | 小事但每次都硌手 |

### 建议推进顺序

```
P0 全部（1-2 天量级，互相独立）
→ #16 测试基建（给后面的大刀上保险）
→ #9 provider 统一（后端最大杠杆，测试已有锚点）
→ #10 → #11（先拆服务再并管线，顺序不可倒）
→ #12 + #13 + #14（前端一轮结构收敛）
→ #15（鉴权，动所有控制器，放结构稳定后）
→ P2 按需
```

---

## 6. 总体评价

**这是一个纪律性不错的小型全栈项目**：前端有类型契约层、store 按域拆分、路由全懒加载、i18n 完整同步、SignalR composable 工程质量高、视觉改版后 token 体系 adoption ~99%；后端迁移有三库真容器验证、核心翻译引擎有 910 行测试、插件 ABI 设计认真。

拖后腿的是四件事：
1. **复制粘贴文化**：后端 18 个重试循环 + 5 个 chat-completions 克隆 + 已发生漂移（DeepSeek 零重试）；前端 57 个 Promise 包装 + 列表页三胞胎 + 选择状态机三份。
2. **巨型服务与双管线**：`TranslationRequestService` 1,047 行 7 种职责，与 `TranslationJob` 平行的第二条翻译管线语义分歧。
3. **鉴权与安全毛刺**：Hub 无鉴权、API key 非常数时间比较、控制器直捅 DbContext、XSS 面。
4. **零前端测试 + 后端命脉无测试**（主管线 + 认证）。

重构的支点已经存在：核心引擎的测试、i18n 的完整键表、token 体系的语义化——从 P0 清单开始即可安全启动。
