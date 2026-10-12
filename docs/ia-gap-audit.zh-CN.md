# IA 原型对照差距审计与接口计划

> 日期：2026-10-12 · 分支：`refactor/ia-migration` · 原型：`design-previews/refactor-prototype.html`
> 用途：逐屏对照原型与当前实现，列出缺口、所需接口与落地批次。**本文档是"界面严格按原型"要求的执行清单。**
> 标记：✅ 已完成 · 🔶 部分完成（结构对齐，内容待改） · ❌ 未实现

---

## 一、逐屏差距

### 屏 1 · 概览 `/`

| 原型元素 | 状态 | 缺口 / 所需接口 |
|---|---|---|
| 管线条带（媒体库→缺字幕检测→队列→服务→写回，实时计数） | ❌ | 新接口 `GET /api/statistics/pipeline`：各阶段计数（媒体总数、缺字幕数、队列深度=Hangfire 各队列 pending、在线服务数、本月写回数）；数据源全部现成（ Hangfire MonitoringApi + MediaService + StatisticsService） |
| KPI 条（进行中/排队/本月成本/30 天成功率） | ❌ | 进行中/排队可从 `counts` 复用；**本月成本需新数据**：任务表加 token/字符用量字段或用量日志表（P3-b 成本面板同源，合并实现）；成功率 = 按状态聚合 |
| 活跃任务（轨道条+播放头+批次信息） | 🔶 | `GET /translationrequest/active` 已有；前端重做成轨道条 |
| 服务健康（状态点+延迟+月成本） | ❌ | 新接口 `GET /api/translate/health`：各 provider 最近请求延迟/成败（需在 BaseTranslationService 记录）+ 配置状态 |
| 最近动态 feed | ❌ | `translation_request_events` 表已有事件数据；新接口 `GET /api/translationrequest/events?take=20` 或复用 SignalR |

### 屏 2 · 媒体库 `/library` ✅ 结构 · 🔶 内容

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

### 屏 5 · 导入 `/import` ✅ 大部分 · 🔶 增强项

| 原型元素 | 状态 | 缺口 |
|---|---|---|
| 拖拽/选择 + 处理队列表（检出/匹配） | ✅ | — |
| 服务器路径扫描 | ❌ | `GET /api/directory/get` 已有目录浏览；新增 `POST /subtitles/scan-path {path}`：列出该目录下字幕文件（复用 DirectoryService） |
| 编码检测列 | ❌ | `parse-filenames` 扩展：接受文件字节→SubtitleEncodingService.Detect 返回编码名（或导入后结果回显） |
| 每文件动作（仅转换/转换+翻译） | 🔶 | 端点已有全局 translate 开关；per-file 动作需请求体改为文件级数组（小改） |
| 写回目录/冲突策略选项 | ❌ | 后端现为固定（冲突=加后缀不覆盖）；做成设置项（SettingKeys）+ 表单字段 |
| 内容哈希去重 | ✅ duplicateOfRequestId | — |
| 简繁转换 | ✅ chineseConvert | — |
| 双语输出开关 | 🔶 | 端点未接收该字段（双语在写回阶段由设置决定）；透传到请求的 IsBilingual |

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
| A1 | `GET /api/translate/health` | `[{service, configured, latencyMs?, successRate?, lastError?, monthlyCost?}]`；延迟/成败由 BaseTranslationService 请求计时聚合（内存环形+可选持久化） | 概览服务健康 · 设置 provider 卡片 | B1 |
| A2 | `GET /api/statistics/pipeline` | `{mediaCount, missingSubtitles, queueDepth, servicesOnline, writtenThisMonth}` | 概览管线 | B1 |
| A3 | `GET /api/translationrequest/events?take=` | 最近事件（复用 events 表） | 概览动态 | B1 |
| A4 | 媒体字幕状态聚合 | `GET /media/shows` 投影加 `subtitleSummary: [{lang,state,count}]` + 最近活动；电影同 | 媒体库矩阵 | B2 |
| A5 | 用量与成本 | 请求表加 `tokens/characters` 列（M0025）+ provider 单价设置；`GET /api/statistics/cost?month=` | 概览成本 KPI · 媒体库托盘预估 · provider 卡片月成本 | B2（=P3-b） |
| A6 | 队列增强 | Summary 加 `ServiceType/StartedAt`；暂停端点 | 队列 provider 徽章/ETA/暂停 | B2 |
| A7 | 导入增强 | `POST /subtitles/scan-path`；parse 扩展编码检测；per-file 动作数组；写回/冲突设置项 | 导入台选项条/编码列 | B3 |
| A8 | 术语表（P3-a） | `translation_glossaries` 表 + CRUD + LLM provider prompt 注入 + 校对台高亮 | 设置术语卡 · 校对台 | B3 |
| A9 | VTT（P3-f） | VttParser/VttWriter + 格式徽章转正 | 导入格式 · 字幕输出卡 | B3 |

## 三、批次

- **B1（概览优先）**：A1 + A2 + A3 + Dashboard 重做（管线+KPI+活跃任务轨道条+动态 feed），概览是首屏门面
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
