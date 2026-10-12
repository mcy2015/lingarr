# IA 原型对照差距审计与接口计划

> 日期：2026-10-12 · 分支：`refactor/ia-migration` · 原型：`design-previews/refactor-prototype.html`
> 用途：逐屏对照原型与当前实现，列出缺口、所需接口与落地批次。**本文档是"界面严格按原型"要求的执行清单。**
> 标记：✅ 已完成 · 🔶 部分完成（结构对齐，内容待改） · ❌ 未实现

---

## 一、逐屏差距

### 屏 1 · 概览 `/` ✅（2026-10-12 B1 批次完成）

> B1 全链落地：A1+A2+A3 三端点 + Dashboard 按原型第一屏重写，实测渲染与视觉比对通过。

| 原型元素 | 状态 | 实现 |
|---|---|---|
| 管线条带（媒体库→缺字幕检测→队列→服务→写回，实时计数） | ✅ | `GET /api/statistics/pipeline`（A2）：媒体/缺字幕来自统计扫描（按目标语言求差、逐语言下限 0），队列深度=Hangfire 各队列 enqueued 求和，服务在线=已配置且窗口未失败，本月写回=CompletedAt≥月初且有写回文件 |
| KPI 条（进行中/排队/本月成本/30 天成功率） | ✅ | 进行中/排队复用 `counts`；成功率=近 30 天 Completed/(Completed+Failed)；**成本显示「—」，待 A5（B2）** |
| 活跃任务（轨道条+播放头+批次信息） | ✅ | 数据源用 `requests?status=InProgress`（/active 端点只有 MediaId/Status 不够展示）；`RequestProgress` SignalR 实时推进 + 终态触发全量刷新 + 30s 兜底轮询；ETA 线性估算与队列页同公式 |
| 服务健康（状态点+延迟+月成本） | ✅ | `GET /api/translate/health`（A1）：`BaseTranslationService` 共享重试环逐 attempt 记录（工厂创建后注入单例记录器），环形窗口 20 样本；状态点 绿/黄/红/灰=健康/降级/故障/未配置；插件 ABI 实现不经过 Base 暂不记录；**月成本待 A5** |
| 最近动态 feed | ✅ | `GET /api/translationrequest/events?take=`（A3）：事件表 join 请求标题，倒序 20 条，图标 ✓/✗/▸/⏸/· 着色 |

### 屏 2 · 媒体库 `/library` ✅（2026-10-12 B2 批次完成）

| 原型元素 | 状态 | 缺口 / 所需接口 |
|---|---|---|
| 类型 tab（剧集/电影，URL 同步） | ✅ | — |
| 字幕轨状态矩阵 chips（`EN ✓` `ZH ◐ 3/10`） | ❌ | 电影：subtitles 已在 DTO，前端改造成 chips 即可；剧集：需在 Show DTO 聚合季集字幕状态（`GET /media/shows` 投影加 aggregate） |
| 状态列（翻译中/完成/失败/未排队） | ❌ | 同上聚合接口（从 translation_requests 按 media 汇总最近状态） |
| 最近活动列 | ❌ | 同汇总接口（该媒体最近一次请求的 UpdatedAt/CompletedAt） |
| 批量托盘（已选 N + 翻译所选 + 预计消耗） | 🔶 | 选择+批量翻译已有（选择模式+ContextMenu）；**预计消耗需**：选中项字符数聚合 + provider 单价表（P3-b）；托盘 UI 化（吸底条） |
| 搜索/排序/分页 | ✅ | — |

### 屏 3 · 任务队列 `/queue` ✅（本轮完成）

| 原型元素 | 状态 |
|---|---|
| 状态过滤 tab + 计数 | ✅ `GET /translationrequest/counts` + status 过滤参数 |
| 导入徽章 | ✅ Summary.Source |
| 轨道条进度+播放头 | ✅ TranslationProgress（签名组件既有） |
| provider 徽章 | ✅ M0025 service_type + TranslationJob 管线后写入 + 前端 mono chip |
| ETA | ✅ M0025 progress/started_at 持久化 + 前端线性估算（startedAt+progress） |
| 暂停 | ✅ 协作式暂停（registry 门控 + 批循环检查点）+ pause/resume-paused 端点；内存门控，重启自动恢复（已注明） |

### 屏 4 · 校对台 `/proofread/:id` ❌（独立任务：双轨工作台）

原型元素：双轨行对照（时间码 gutter）、J/K/Enter 键盘流、逐行通过/驳回、术语命中高亮、进度条、完成写回。
现状：QueueDetailPage 内嵌 LineProofreadPanel（P2 拆分，含通过/驳回逻辑）。
计划：独立一级路由 + 双轨组件 + 键盘 composable（`useProofreadKeyboard`）；数据接口已有（Proofread API 全套）。

### 屏 5 · 导入 `/import` ✅ 结构+视觉 1:1 · 🔶 服务端增强项

> 2026-10-12 实机核验：曾因 `getSettings` getter 双重 `JSON.parse` 整页崩溃（配置过语言的实例必现，硬加载连应用都不挂载），已修复（`80fa3a3`）并实测恢复——硬加载/软导航均完整渲染，语言下拉正常取值。
> 同日晚：按原型第四屏**一比一复刻**（双面板布局、300px+1fr 网格、处理队列六列表、选项条与预估行、整季映射预览+排除未匹配），per-file 动作经"按动作分组两次调用"实现，检出/编码列为客户端真实检测（见下）。

| 原型元素 | 状态 | 缺口 |
|---|---|---|
| 拖拽/选择 + 处理队列表（文件/检出/编码/目标/动作/状态） | ✅ | 检出列行数为客户端真实统计（SRT cue 块 / ASS Dialogue 行） |
| 编码检测列 | ✅ | 客户端严格 UTF-8 校验（fatal decode）；具体字符集名（GB18030 等）仍由导入时服务端判定，行内显示「非 UTF-8 · 将转码」 |
| 每文件动作（转换＋翻译 / 仅转换） | ✅ | 前端按动作分组两次调用，零 API 改动；B3 文件级数组可省一次请求 |
| 服务器路径扫描 | ❌ | `POST /subtitles/scan-path`（B3-A7）；UI 已占位：路径框 + 禁用按钮 + 规划中徽章 |
| 写回目录/冲突策略选项 | 🔶 | 以信息 chip 呈现当前真实行为（自由=导入库目录；整季=媒体目录·未匹配回退；冲突=加后缀不覆盖）；可配置项待 B3 设置键 |
| 内容哈希去重 | ✅ | duplicateOfRequestId，行内「△ 与任务 #N 重复」 |
| 简繁转换 | ✅ | chineseConvert |
| 双语输出开关 | 🔶 | 以「开/关 · 跟随全局」chip 呈现全局设置值；导入级覆盖需 B3 透传 IsBilingual |
| 成本预估（$） | ❌ | 依赖 A5 用量/单价（B2）；现为真实行数/项数预估 |

### 屏 6 · 设置 `/settings` ✅ 结构 · 🔶 内容（原型卡片化）

| 原型元素 | 状态 | 缺口 |
|---|---|---|
| 四 tab 顶部导航 | ✅ | — |
| 翻译服务=provider 卡片（状态点/模型/用量/延迟/测试） | ❌ | 需 `GET /api/translate/health`（同概览）+ 卡片 UI；现堆叠旧配置页 |
| 媒体源卡片（Sonarr/Radarr 状态+重同步+Jellyfin 规划） | 🔶 | IntegrationSettings 已有配置+测试；卡片化重排 + 状态点（测试结果缓存） |
| 自动化批次卡片（cron+开关） | 🔶 | AutomationSettings 已有表单；卡片化重排 |
| 字幕输出卡（双语/命名/格式） | 🔶 | SubtitleSettings 已有；格式行加 VTT「规划中」徽章（P3-f） |
| **术语表卡** | ❌ | **新特性 P3-a**：`translation_glossaries` 表 + CRUD 端点 + 翻译管线注入 + UI 卡片 |
| 系统行（认证/日志/遥测/定时任务） | 🔶 | 现堆叠旧页；重排为行式卡片（含状态点） |

---

## 二、缺失接口计划（按依赖排序）

| # | 接口 | 规格 | 支撑界面 | 批次 |
|---|---|---|---|---|
| A1 | `GET /api/translate/health` | `[{provider, displayName, configured, latencyMs?, successRate?, lastError?, lastRequestAt, sampleCount, monthlyCost?}]` | ✅ 已交付（内存环形窗口，重启清零；monthlyCost 待 A5） | B1 ✅ |
| A2 | `GET /api/statistics/pipeline` | `{mediaCount, missingSubtitles, queueDepth, servicesOnline, writtenThisMonth, successRate30Days, monthlyCost?}` | ✅ 已交付（缺字幕=按目标语言与扫描文件数求差，逐语言下限 0） | B1 ✅ |
| A3 | `GET /api/translationrequest/events?take=` | 最近事件（events 表 join 请求标题，倒序） | ✅ 已交付 | B1 ✅ |
| A4 | 媒体字幕状态聚合 | `GET /media/shows` 投影加 `subtitleSummary: [{lang,state,count}]` + 最近活动；电影同 | 媒体库矩阵 | B2 |
| A5 | 用量与成本 | 请求表加 `tokens/characters` 列（M0025）+ provider 单价设置；`GET /api/statistics/cost?month=` | 概览成本 KPI · 媒体库托盘预估 · provider 卡片月成本 | B2（=P3-b） |
| A6 | 队列增强 | Summary 加 `ServiceType/StartedAt`；暂停端点 | 队列 provider 徽章/ETA/暂停 | B2 |
| A7 | 导入增强 | `POST /subtitles/scan-path`；parse 扩展编码检测；per-file 动作数组；写回/冲突设置项 | 导入台选项条/编码列 | B3 |
| A8 | 术语表（P3-a） | `translation_glossaries` 表 + CRUD + LLM provider prompt 注入 + 校对台高亮 | 设置术语卡 · 校对台 | B3 |
| A9 | VTT（P3-f） | VttParser/VttWriter + 格式徽章转正 | 导入格式 · 字幕输出卡 | B3 |

## 三、批次

- **B1（概览优先）**：✅ 已完成（2026-10-12）——A1 + A2 + A3 + Dashboard 重做（管线+KPI+活跃任务轨道条+动态 feed）；分支 `feat/b1-overview`
- **B2（数据层）**：A4 + A5 + A6（媒体库矩阵、成本面板、队列增强一次到位，共享 M0025）
- **B3（长尾）**：A7 + A8 + A9
- 校对台工作台：独立任务，与上述并行

## 四、已完成核对（2026-10-12）

- [x] 全局：六项主导航、Timeline 主题、i18n 双语同步
- [x] 媒体库：结构合并、类型 tab、搜索/排序/分页、批量翻译（选择模式）
- [x] 队列：状态过滤 tab+计数、导入徽章、轨道条进度、取消/重试
- [x] 导入：拖拽上传、UTF-8 转码、简繁转换、整季定向绑定写回、内容哈希去重、入库即翻译
- [x] 设置：四 tab 结构、旧路由重定向、语言映射入菜单
- [x] 后端：解析器/编码检测/去重/绑定 全部有测试；M0023/M0024 三库验证
- [x] 导入台页面实机核验（2026-10-12：修复 `getSettings` 崩溃 `80fa3a3` 后，硬加载/软导航均正常渲染）
