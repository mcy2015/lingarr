# Lingarr 重构计划与完成情况总览

> 更新：2026-10-12 · 分支：`refactor/ia-migration`（领先 main 14 提交，未推送）
> 基线：main = `3858195`（feat/visual-refresh 合并，已推送 origin）
> 配套文档：`module-analysis.zh-CN.md`（P0-P2 清单）· `redesign-proposal.zh-CN.md`（P3 设计）· `ia-gap-audit.zh-CN.md`（原型差距与接口计划）
> 质量快照：Server.Tests **366/366** · Migrations.Tests **12/12**（真容器）· 前端 build/oxlint/vitest 11/11 · i18n en/zh-CN **579/579**

---

## 一、总时间线

| 阶段 | 内容 | 状态 | 提交锚点 |
|---|---|---|---|
| P0 | 低风险快赢 8 项（安全/错误处理/死代码） | ✅ 全部完成 | 并入分支早期提交 |
| P1 | 核心结构重构 8 项（#9–16） | ✅ 全部完成 | `4638ca5..38ee87c` |
| P2 | 代码债清理 8 项（#17–24） | ✅ 完成（1 处暂缓，见 §七） | `d6066b8..c4241a9` |
| P3-设计 | 产品设计提案 + 六屏交互原型 | ✅ 已提交 | `442f0d3` |
| P3-导入台 | 导入工作台全栈（解析/转码/绑定/去重/简繁/暂停） | ✅ 全部完成+冒烟 | `03adb12..594c259` |
| IA 迁移 | 媒体库合并 / 队列改名 / 设置四 tab / 旧路径重定向 | ✅ 完成+截图验证 | `57da73b` `7393d05` |
| 队列 A 档 | 原型 qrow 风格六项（纯前端） | ✅ 完成+截图验证 | `79f3fc9` |
| 队列 B 档 | 运行时字段 + provider/ETA/暂停/重试（全栈） | ✅ 完成+冒烟 | `8f03bd1` `1d3eaa9` `eb106ee` |
| 合并/推送 | feat/visual-refresh → main（47+1 提交）→ origin | ✅ main 已推送 | `3858195` |
| 实机核验+修复 | localhost:9876 六屏巡检；发现并修复导入台整页崩溃 | ✅ 修复+实测恢复 | `80fa3a3` |
| 导入台原型复刻 | 按原型第四屏一比一重写（双面板/六列表/选项条/per-file 动作/客户端检出） | ✅ 完成+全链实测 | `5e1b39a` |

---

## 二、阶段一：P0 低风险快赢（#1–8）✅

> 全部完成。后端 build + 262/262 测试绿（当时基线）；前端 lint/vue-tsc/build 绿；净减约 429 行。

| # | 事项 | 状态 | 说明 |
|---|---|---|---|
| 1 | 服务层扁平化 + 统一 `ApiError`（57 处 Promise 包装） | ✅ | 顺带修复 StatisticsComponent 错误 UI 永不显示的 bug |
| 2 | HttpClient 卫生（`IHttpClientFactory` 命名客户端） | ✅ | 防 socket 耗尽 |
| 3 | 安全三连（常数时间 API key 比较 / 密码长度 / Hub 鉴权） | ✅ | `FixedTimeEquals`；Hub 进框架管线 |
| 4 | XSS 收口（show-title/highlight/插件描述） | ✅ | 转义 + DOM 构建 |
| 5 | SrtParser 吞异常 → ILogger + 抛出 | ✅ | 损坏文件不再静默变空列表 |
| 6 | 死代码/死配置清理（BCrypt、JwtBearer、Core/Program、postcss、darkMode、LOCALE、themeDropdown） | ✅ | 全部 grep 确认无消费 |
| 7 | i18n 响应性（`t()` 进 computed） | ✅ | 语言切换即时生效 |
| 8 | async void × 2 改安全模式 | ✅ | — |

## 三、阶段二：P1 核心结构重构（#9–16）✅

> 10 个提交（`4638ca5..38ee87c`），测试从 262 增至 290。

| # | 事项 | 状态 | 落点 |
|---|---|---|---|
| 9 | 翻译 provider 骨架统一（18 重试循环 / 10 Initialize / 5 克隆） | ✅ | `BaseTranslationService.ApplyResilienceSettings + ExecuteWithRetryAsync`；17/18 循环收敛，DeepSeek 零重试 bug 修复 |
| 10 | 拆分 TranslationRequestService（1,047 LOC 巨型服务） | ✅ | Query / Queue / Proofread / Content 四服务 + `AsyncTranslationRegistry` + `JobQueue`，外观层 118 行 |
| 11 | 统一双翻译管线 | ✅ | `ITranslationPipeline`（文件/内容双 source），取消语义对齐 |
| 12 | 列表页三胞胎收敛 | ✅ | `usePagedList` / `useSelection` / `ListToolbar`；前端 Vitest 基建 |
| 13 | `src/ts/` → `src/types/` | ✅ | 90 文件迁移，桶导入治理 |
| 14 | 图标收敛（42 个 SFC） | ✅ | `ICONS` 注册表 + `Icon.vue`，几何全等校验，−531 LOC |
| 15 | Auth 收拢强化 | ✅ | `ApiKeyAuthenticationHandler` + `LingarrAccess` policy（17 控制器 + 3 Hub）+ 用户 CRUD 入 `IAuthService` |
| 16 | 测试补命脉 | ✅ | TranslationJobTests 8 + AuthServiceTests 14 + LingarrAccessHandlerTests 6；Vitest 基建 |

## 四、阶段三：P2 代码债清理（#17–24）✅

> 11 个提交（`d6066b8..c4241a9`），后端 290/290 + 前端全绿 + dev 容器冒烟。

| # | 事项 | 状态 | 说明 |
|---|---|---|---|
| 17 | DTO 收拢（Contracts 对齐、分页不泄实体） | ✅ | `TranslationRequestSummary` 投影，`BatchTranslatedLine` 并入 Contracts |
| 18 | 静态/全局状态清理 | ✅ | 自动化游标→settings 行；MigrationConfiguration 显式传参；`/app` 硬编码→ContentRootPath。**遗留一处**：`JobContextFilter` AsyncLocal→PerformContext（9 作业签名，暂缓） |
| 19 | 连接串单一来源 | ✅ | `DatabaseConfiguration` 统一（原两处已漂移的默认值消除） |
| 20 | 全局异常中间件 | ✅ | 500 返回 `{message, traceId}`；删两处控制器兜底 catch |
| 21 | 前端杂项 | ✅ | 详情页 519→335（`useTranslationDetailHub` + `LineProofreadPanel`）、toast 统一、`buildUrl` 替代猴补、shims 删除、token 语义清理 |
| 22 | 日志统一 | ✅ | ABE 4 处 Console→ILogger；`InMemoryLogger` 接管线 |
| 23 | 插件系统打磨 | ✅ | `ISettingChangeNotifier` 单例枢纽；IsBuiltIn 语义注释 |
| 24 | pre-commit 钩子改造 + 僵尸设置下线 | ✅ | 按可用性跳过 + CI 兜底；`core.hooksPath` 已接线 |

## 五、阶段四：P3 设计 + 导入工作台 ✅

### 5.1 设计资产（`442f0d3`）
- `docs/redesign-proposal.zh-CN.md`：竞品调研（Bazarr/Subgen/ChineseSubFinder/pyvideotrans 等）、新 IA、逐屏设计
- `design-previews/refactor-prototype.html`：六屏交互原型（Timeline 体系）

### 5.2 导入工作台（后端 + 前端 + 冒烟）

| 能力 | 状态 | 提交 / 验证 |
|---|---|---|
| 文件名解析器（SxxExx/1x02/E05/第x集 + 语言变体） | ✅ | `03adb12`，38 测试 |
| 编码探测 + UTF-8 转码（BOM/严格 UTF-8/Ude/CJK 合理性覆盖） | ✅ | `3fe92a3`，12 测试 |
| M0023 `source` 判别列 | ✅ | `6344863`，真容器 12/12 |
| 端点 `POST /subtitles/import` + `/parse-filenames` | ✅ | `7542ecf` |
| 前端导入台（自由/整季双 tab、映射预览、简繁、去重提示） | ✅ | `23c399e` + `2e30423`；崩溃修复 `80fa3a3`；**2026-10-12 按原型一比一复刻** `5e1b39a`（详见 gap audit 屏 5） |
| 定向/整季媒体绑定（写回剧集视频旁） | ✅ | `7c51199` 后端 + `152cb6c` 前端，真机冒烟 ✓ |
| 简繁转换选项（OpenccNetLib，短语级） | ✅ | `aff1fd8`，6 测试 |
| 内容哈希去重（M0024 + skip 入队） | ✅ | `d38d3a0` + `594c259`（copy 块丢字段修复），真机冒烟 ✓ |
| 全链路冒烟（导入→翻译→写回） | ✅ | 自由导入 + 定向绑定 两条链均通过 |

**2026-10-12 实机核验补记**：localhost:9876 六屏巡检发现 `/import` 整页崩溃——`getSettings` getter（2024 年上游遗留，此前零消费者）对 `applySettingsOnLoad` 已解析成数组的 `source/target_languages` 再次 `JSON.parse`，`"[object Object]"` 抛错；软导航主区域空白、硬加载整应用不挂载，配置过语言的实例必现（语言为 null 的全新实例侥幸不炸，或因此逃过当时冒烟）。修复：`parseLanguages` 兼容 `string | ILanguage[]` 两种形态（`80fa3a3`），修复后硬加载实测完整渲染，lint/vitest/vue-tsc 全绿。

## 六、阶段五：IA 迁移 + 队列原型对齐 ✅

### 6.1 IA 迁移（`57da73b` + `7393d05`）
- `LibraryPage` 合并 Shows+Movies（`?type=` URL 同步），两页降级为 library section 组件
- `/translations` → `/queue`（QueuePage/QueueDetailPage），旧路径全重定向
- Settings 顶部四 tab（providers/sources/quality/system），旧 9 子路由重定向；语言映射首次入菜单
- 主导航五项：概览/媒体库/任务队列/导入/设置
- 验证：headless Chrome 实渲染五屏 + 重定向落点；修复主导航残留旧路由名导致的全应用白屏

### 6.2 队列 A 档（`79f3fc9`，纯前端六项）✅
①tab 顺序（失败前移） ②3px 状态 rail ③轨道条常显+状态色 ④ETA 列（✓/✗/—） ⑤失败原因/写回文件副行 ⑥Hangfire 信息条；移除表头与独立语言/状态列，改原型 qrow 自描述布局

### 6.3 队列 B 档（`8f03bd1` + `1d3eaa9` + `eb106ee`，全栈四项）✅
- M0025：`service_type` / `progress` / `started_at` / `retry_count`
- provider 徽章（管线后持久化实际服务）✅ 冒烟：`libretranslate` chip
- 进度持久化 + 线性 ETA 估算（startedAt+progress）✅
- 协作式暂停（registry 门控 + 批循环检查点）+ `pause`/`resume-paused` 端点 ✅ 校验 400 ✓
- 失败重试计数（Hangfire 状态数据回读）✅
- i18n 545/545

## 七、未完成与计划

### 7.1 原型对齐批次（详表见 `ia-gap-audit.zh-CN.md` §二）

| 批次 | 内容 | 接口 | 规模预估 |
|---|---|---|---|
| **B1 概览重做** | 管线条带 + KPI 条 + 活跃任务轨道条 + 动态 feed + 服务健康 | A1 `translate/health` · A2 `statistics/pipeline` · A3 `translationrequest/events` | 中（3 端点 + 1 页） |
| **B2 媒体矩阵 + 成本** | 媒体库字幕状态矩阵/状态列/最近活动 + 概览成本 KPI + 托盘消耗预估 + 队列写回列 | A4 媒体聚合投影 · A5 成本（M0025 已有 progress 基础） | 中 |
| **B3 长尾** | 导入路径扫描/编码检测列/写回策略选项 + 术语表 CRUD+翻译注入+校对高亮 + VTT | A7 / A8 / A9 | 中大 |
| **校对台工作台** | 一级路由双轨对照 + J/K/Enter 键盘流 + 术语高亮 + 完成写回 | 复用 Proofread API 全套 + 术语表（依赖 B3-A8 高亮部分） | 大 |

### 7.2 小尾巴
- [ ] 队列筛选 tab 补「已暂停」计数（并入进行中计数显示）
- [ ] 简繁转换应用于翻译**输出**侧（现仅导入存储侧）
- [ ] dev 库冒烟数据清理（requests #2–#8 与 smoke 文件，无害）
- [ ] `PiSpace/bazarr`（148M，参考价值已尽，可删）；`PiSpace/ChineseSubFinder` 保留

### 7.3 已知限制（有意为之或已注明）
1. 暂停门控存于内存：应用重启自动释放暂停、作业继续运行（UI 已注明）
2. 简繁转换仅导入侧；输出侧待接（§7.2）
3. 内容哈希去重仅覆盖 `translate=true` 的导入；仅入库文件不参与
4. ETA 为线性估算（无速率模型），排队位次不可见
5. pre-commit 本地跳过后端构建（宿主无 dotnet），以 CI 为准
6. P2 #18 遗留：`JobContextFilter` AsyncLocal→PerformContext（9 作业签名）

## 八、验证基线

| 项 | 结果 |
|---|---|
| Server.Tests（xUnit v3） | **366/366** ✅ |
| Migrations.Tests（Testcontainers MySQL/PG/SQLite） | **12/12** ✅ |
| 前端 build（vue-tsc + vite）/ oxlint / vitest | ✅ / ✅ / 11/11 |
| i18n | en/zh-CN 545/545 完全同步 |
| 真机冒烟 | 自由导入 ✓ · 定向绑定 ✓ · 去重 ✓ · 简繁 ✓ · 暂停校验 ✓ · provider chip ✓ · 六屏实机巡检 ✓ · 复刻后全链 ✓（注入文件→per-file 动作→分组双调用→LibreTranslate 翻译→写回，测试数据已清理） |
| dev 环境 | Server/Client/Mysql/LibreTranslate 四容器运行中（localhost:9876 UI · 9877 API） |
