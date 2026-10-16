# 技术方案 v1.0 — 字幕搜索/下载（Lingarr Subtitle Search）

状态：定稿可落盘｜日期：2026-10-15｜前置：[PRD v1.0](subtitle-search-prd.zh-CN.md)、sonarr/radarr 源码核实、DA/QA/设计师需求全部纳入。
关键落点已核实：`ScheduleService.cs` L44-55 六个 builtin job、`StatisticsJob.cs` L45/L75 全量载入、`LogsController.cs` SSE 先例、`SonarrService.cs` 按 seriesId+season 拉集、`Movie.cs` L13 已有 MediaHash 列、`MigrationTests.cs` 四处计数门禁、`M0004` Down 先例。

> 现状（2026-10-16）：「六个 builtin job」等为落笔时快照——SubtitleSearchJob 合入后为**七个**（代码注释自称「8th」计数有误）。

## 交付现状（2026-10-16 对照 cc656db）

- **已交付**：M1–M5+M7 的 webhook 半边——ABI/注册器、七源 Provider、优先级队列（backlog/pin）、下载管线（search/download/test 端点）、Radarr 侧 webhook 高优入队；前端搜索弹层与源卡设置组。实现过程叠加了 QA 修复链的**超集契约**（⑭补录）——**按本文旧契约回退实现会丢修复，回退前必读⑭**。
- **主要漂移**：迁移槽位 M0006/M0007→**M0008/M0009**；无 DelegatingHandler 链（SubtitleHttp 单发分类+SubtitleSourceCooldownStore）；search 端点 provider 必填；providers 端点=manifest+auth 标志（健康态未合入）；history/kpi 端点未交付（R7 批次交付中，未合入 cc656db）；Sonarr webhook 加成未合入（R6 批次交付中）；M7 水位化零实现（coverage_checked_at/coverage_daily 零读写）。
- 正文就地标注 `> 现状（2026-10-16）：…`，原文保留供考古。

## ① 总体架构

新模块 `Lingarr.Server/Modules/SubtitleSearch/`（`AddSubtitleSearchModule`，含 DI 注册与命名 HttpClient），复用 `Modules/Translation/Plugins/IPluginManifest.cs` 本体模式 + 平行 `SubtitleProviderRegistry`。数据流：触发（手动/自动批次/webhook P1）→ 优先级队列出队（`subtitle_search_state`）→ 编排器（`Task.WhenAll` 各源、单源 10s 超时、UUID 候选缓存 `IMemoryCache` 10min）→ 打分排序 → 下载管线（SharpCompress 解压 → 编码检测 → 可选转 SRT → 复用命名模板落盘）→ 历史/统计/翻译入队 → `JobProgressHub` 广播。回填搭车现有 04:00 同步波（`ScheduleService.cs` BuiltinJobs）。

## ② 核心 ABI

```csharp
// Modules/SubtitleSearch/Abstractions/
public interface ISubtitleProvider {
    Task<SubtitleSearchResult> SearchAsync(SubtitleSearchRequest request, CancellationToken ct);
    Task<SubtitleDownload> DownloadAsync(SubtitleCandidateRef candidateRef, CancellationToken ct);
}
public record SubtitleSearchRequest(string Title, int? Year, int? Season, int? Episode,
    string? ReleaseName, string? VideoHash /*OpenSubtitles 算法*/, string[] Languages, string? ImdbId);
public record SubtitleCandidate(string Provider, string ExternalId, string Language, int MatchScore,
    bool Forced, bool HearingImpaired, string? ReleaseInfo, string? Uploader, string Format, long? Size, bool HashMatch);
public record SubtitleCandidateRef(string CacheKey); // IMemoryCache UUID，10min TTL，免跨请求传参
```

Provider 声明 `SubtitleProviderAttribute(name, requiresAuth)`，注册器按 settings 启用。命名 HttpClient `subtitle-<provider>`，DelegatingHandler 链：冷却 → TokenBucket 限速 → 重试（仅幂等 GET，1 次）。

> 现状（2026-10-16）：🔧已变更（R3a 后）——**无 DelegatingHandler 链**：每源命名 HttpClient 直连，`SubtitleHttp` 单发请求并统一分类失败（429/401/403/5xx/超时/网络错误→SubtitleProviderException，驱动⑧冷却表），冷却状态由 `SubtitleSourceCooldownStore` 持久化（settings JSON，读写经静态 PersistGate 串行防并发覆盖）。TokenBucket 限速与 GET 重试**未实现**（见⑧标注）。超集契约：搜索档 10s/2MB 缓冲，下载档专用 client 120s+流式 100MB 硬上限（⑭）。

## ③ 匹配打分

hash 命中=100 短路（最优先）；标题 55 + 年份 15 + 季集 20 + 发行名 token 10。自动线 75（PM 定稿，单线不分语言）；低于 75 的自动候选跳过并落历史 `result=LowConfidence`（无人工审批）。分值表为 Bazarr 观察性结论 + 本项目调参，代码注释标出处。

## ④ 数据模型（M0006 + M0007，FluentMigrator，均带 Down）

> 现状（2026-10-16）：🔧迁移编号全文漂移——实际为 **M0008_AddSubtitleSearch / M0009_AddSubtitleSearchTelemetry**（M0006=审计 S1 性能索引、M0007=审计 S4 音轨列+嗅探缓存）。本节及全文 M0006/M0007 字样按此对应阅读；MigrationTests 四处计数门禁已按实际编号同步。

**M0006_AddSubtitleSearch**（`Lingarr.Migrations/Migrations/M0006_AddSubtitleSearch.cs`）——业务表+列+设置行：
- `subtitle_search_state`：id, media_type(int), media_id(int), status(int 0待处理/1成功/2失败/3已跳过), priority_rank(int 0=Pinned/1=WebhookBoost/2=Normal), pinned(bool), webhook_boost_until(datetime null), next_attempt_at(datetime), attempt_count(int), last_error(TEXT null), last_searched_at(null), updated_at
- `subtitle_downloads`（一次建全，采集列不再后补）：id, media_type, media_id, provider, external_id, language, file_path, match_score, hash_match(bool), source(int Manual/Auto/Webhook), search_session_id(int null), result(int 成功/失败/低置信跳过/已存在跳过), error(null), removed_at(null), replaced_by_download_id(int null), created_at
- `subtitle_search_blacklist`：id, provider, reason, expires_at
- 列：`movies.year` `movies.imdb_id` `shows.year` `shows.imdb_id`（不加到 episode/season，见⑤）
- 列增补（2026-10-15 库增强轮，[需求卡](library-enhancements-prd.zh-CN.md)裁定一次合入不拆）：`movies.audio_languages` `episodes.audio_languages`（JSON 规范化代码数组，chi/zho→zh 折叠，来源可信度 mediaInfo.audioLanguages>languages>ffprobe）、`movies.quality` `movies.size_bytes` `movies.imdb_rating`、（可选）`monitored`——同步载荷免费携带，MovieSync/EpisodeSync 写路径与 year/imdb_id 同处一次改完
- 设置行：`subtitle_search_enabled`(false)、`subtitle_search_schedule`(cron `0 5 * * *`)、`subtitle_search_auto_score`(75)、`subtitle_search_convert_srt`(true)、`subtitle_search_batch_limit`(200)

> 现状（2026-10-16）：🔧「一次合入不拆」未按裁决执行——`audio_languages` 实际落 **M0007_AudioAndLanguageCache**（同批还建了 `audio_override`/`needs_translation`/`no_subtitle_needed` 旗标列与 `subtitle_language_cache` 嗅探缓存表）；`quality`/`size_bytes`/`imdb_rating`/`year`/`imdb_id` 落 **M0008**。**monitored 裁定跳过**（未建列）。设置行实际 **6 个**：上文 5 个 + `subtitle_search_auto_translate`(true)；`subtitle_search_schedule` 归 Automation 设置组（BuiltinJobs 读该键）。

**M0007_AddSubtitleSearchTelemetry**（`M0007_*.cs`）——DA 采集：

> 现状（2026-10-16）：本节内容实际落 **M0009_AddSubtitleSearchTelemetry**。`subtitle_search_sessions`/`provider_quota_usage` 已有读写方；**`coverage_daily` 与 `coverage_checked_at` 已建但零读写**（M7 水位化未实施，见⑪标注）。
- `subtitle_search_sessions`：id, media_type, media_id, provider, query_language, result_count, hit_target(bool), created_at（无结果也落行）
- `provider_quota_usage`：date(date), provider, searches, downloads, quota_limit, quota_remaining(null)，PK(date,provider)
- `coverage_daily`：date, media_type, language, covered, total，PK(date,media_type,language)，增量 upsert 永不全量重扫
- 列：`movies.coverage_checked_at` `episodes.coverage_checked_at`（水位，P1 用，先建列后用）

**索引清单**（QA 四处计数门禁同步 `Lingarr.Migrations.Tests/MigrationTests.cs` 期望表）：
- `ix_subtitle_search_state_dequeue (status, priority_rank, next_attempt_at)` 出队覆盖索引
- `ux_subtitle_search_state_media (media_type, media_id)` UNIQUE webhook/手动入队去重
- `ix_subtitle_search_state_boost (webhook_boost_until) WHERE webhook_boost_until IS NOT NULL`（`Execute.Sql` 建部分索引，过期降级定位）
- `ix_subtitle_downloads_media (media_type, media_id, created_at)` 字幕卡就地刷新查询
- `ix_subtitle_downloads_created (created_at)` 历史屏 keyset 深页
- `ix_subtitle_downloads_source (source, result, created_at)` 历史筛选（途径×结果）
- `ix_subtitle_search_sessions_media (media_type, media_id, created_at)`、`ix_subtitle_search_sessions_created (created_at)`
- PK 即可：provider_quota_usage、coverage_daily

## ⑤ 触发与优先级队列（设计点 1/2 裁决）

**裁决：剧集 Year/ImdbId 落 Show，不冗余到 Season/Episode**——单集 imdb \*arr API 就没有（EpisodeResource 仅 TvdbId），冗余会带来每集同步更新成本与 10w 行级写放大；搜索请求构建走 `Episode→Season→Show` 单次 JOIN 投影（EF `Include` 一条 SQL，无 N+1）。

**优先级队列**：`priority_rank` 为写时固化的排序标量（0/1/2），出队单条 SQL：
```sql
SELECT ... WHERE status=0 AND next_attempt_at<=@now
ORDER BY priority_rank, next_attempt_at LIMIT @batchLimit;  -- 命中 ix_subtitle_search_state_dequeue
```
- Pinned：API 置顶置 `rank=0`；Webhook 入队：`INSERT ... ON CONFLICT(media_type,media_id) DO UPDATE SET webhook_boost_until=@now+48h, priority_rank=MIN(rank,1), next_attempt_at=@now`（保留 attempt_count，不重置退避史）；出队批内顺手 `UPDATE ... SET priority_rank=2 WHERE id IN(...) AND webhook_boost_until<@now` 完成过期降级。
- 退避：失败 `next_attempt_at = now + 6h×2^attempt_count` 封顶 7d，attempt_count≥8 转 status=3。

**回填与同步（设计点 2）**：Radarr `/api/v3/movie/` 无分页，10w 级 JSON 原始约 300-500MB、gzip 后 50-80MB——**保留单次 GET，但 `RadarrService.GetMovies()`（RadarrService.cs L17）改流式反序列化**：`HttpCompletionOption.ResponseHeadersRead` + 流式分批喂给 `MovieSyncService`（BatchSize=100 事务批写先例）。Year/ImdbId 取自同一载荷，零额外请求。Sonarr `GetEpisodes(seriesId, seasonNumber)` 按剧拉取已天然分批，仅 SeriesResource 补抓 year/imdb 落 Show。HttpClient 10min 超时对 80MB gzip 足够。

**触发面**：`SubtitleSearchController`（手动）+ `SubtitleSearchJob`（第 7 个 recurring job，`ScheduleService.cs` BuiltinJobs 列表追加，queue=`system`）+ webhook 高优入队（P1，`WebhookJob.cs` ProcessRadarr/ProcessSonarr 末尾追加一行入队调用）。缺源判定：`SubtitleCoverage` 差集（目标语言未覆盖）+ `GetAllSubtitles` 兜底核验存在性。

> 现状（2026-10-16）：✅job 已入 BuiltinJobs，确为**第 7 个** recurring job（`ScheduleService.cs`/`SubtitleSearchJob.cs` 代码注释自称「8th」计数有误，未修）；缺源判定实际走 S4 落库的 `needs_translation`/`no_subtitle_needed` 旗标 SQL（非每次 SubtitleCoverage 差集），`GetSubtitles` 兜底核验保留在自动轮下载前（⑦管线 AC14 态）。webhook 加成 🔧——**仅 Radarr 侧已交付**（master 开关联动已修：总开关关则跳过加成，不影响导入主流程）；**Sonarr 侧未合入**（ProcessSonarrWebhook 无加成调用，R6 批次交付中，按 cc656db git log 判断）。

## ⑥ API 面（设计点 4/5 裁决）

**流式裁决：前端分源并发三请求，不做 SSE/SignalR 组**。`LogsController.cs` SSE 先例虽在，但每搜索会话需请求级通道与取消管理；前端对启用源各发一个 `POST /search`（带 provider），各自 Promise 先到先渲染——单源 10s 超时天然满足聚合 P95≤15s，单源失败可独立重试，服务端零新增流式状态。最简单可靠。

| 端点 | 方法 | 请求/响应要点 |
|---|---|---|
| `/api/subtitle-search/search` | POST | `{mediaId, mediaType, languages[], provider}`；provider 必填（前端逐源并发；省略=全源聚合供 API 用户）；返回 `candidates[]`（已缓存，cacheKey 随行） |
| `/api/subtitle-search/download` | POST | `{cacheKey, candidateIndex, mediaId, mediaType, source}`；返回落盘路径+历史 id |
| `/api/subtitle-search/test/{provider}` | POST | 凭据/连通/配额校验，返回健康态 |
| `/api/subtitle-search/pin` | POST | `{mediaId, mediaType, pinned}` 置顶管理 |
| `/api/subtitle-search/backlog` | GET | `{backlog, nextRoundCount, pinnedCount, webhookBoostedCount}`（批次卡实时） |
| `/api/subtitle-search/history` | GET | `pageCursor&pageSize&source&result&provider&mediaQuery`，keyset 深页（`created_at>` 游标） |
| `/api/subtitle-search/kpi` | GET | `{backlog, throughput7d, quotaWaterline[]}`（概览 KPI） |
| `/api/statistics/trend/{days}` | 扩展 | 现有端点加第 3 序列 `subtitleDownloads`，自 `subtitle_downloads` 按日 COUNT |

> 现状（2026-10-16）：端点逐条——`test/{provider}` ✅；`search` 🔧契约收紧：**provider 必填**（空白 400，「省略=全源聚合供 API 用户」未兑现），另接受可选 `query` 关键词覆写（R3b 超集，⑭）；`download` ✅（超集：410 过期/400 归属不符/skipTranslation，⑭）；`pin` ✅；`backlog` ✅；**`history`/`kpi` ❌未交付**（R7 批次交付中，未合入 cc656db）；**trend 第 3 序列 subtitleDownloads ❌未实现**（StatisticsService 无 subtitle 序列）。

源卡健康态（冷却到期/最后错误/配额行）：`GET /api/subtitle-search/providers` 返回每源 `{enabled, cooldownUntil, lastError, quota}`，读 settings JSON 冷却键 + `provider_quota_usage` 当日行。

> 现状（2026-10-16）：🔧已交付但形状为 **manifest+auth 标志**——实际返回每源 `{provider, displayName, description, requiresAuth, credentialsConfigured, settings[]}`（凭据已配标志实时读 Secret 存储，随热更刷新）；本节的健康态扩展（冷却到期/最后错误/配额行）**未合入**（R7 批次交付中）。

## ⑦ 下载后处理管线

`SubtitleDownloadPipeline`：① SharpCompress 魔数探测压缩包，条目选择=文件名语言标签优先否则最大 srt/ass ② `SubtitleEncodingService`+CodePages（GBK）检测转码 ③ `convert_to_srt` 默认 true（`SrtWriter` 复用） ④ `SubtitleService.CreateFilePath`（SubtitleService.cs L298）同命名模板落盘——落盘即普通外挂字幕 ⑤ 写 `subtitle_downloads` + `subtitle_search_sessions` 命中 ⑥ 旧字幕标记 `removed_at/replaced_by`（文件保留由用户清理策略管） ⑦ `auto_translate` 开（默认开）则入翻译队列 ⑧ `SubtitleCoverage` 就地刷新该媒体 + `JobProgressHub` 广播字幕卡更新（前端不刷页）。

> 现状（2026-10-16）：✅①–⑤/⑦/⑧ 已交付（⑦以 needs_translation 旗标刷新承载）。**步骤⑥ `removed_at`/`replaced_by` 零写方**——列已建（M0008）但无任何代码写入，替换语义未实现（与 PRD 非目标「升级替换 P2 复评」一致，当前为死列）；自动轮另有「已存在跳过」前置核查（Auto 源+库内已有目标语言→AlreadyExistsSkipped，不进①）。手动路径同名冲突走「(1)」后缀（⑭）。

## ⑧ 反爬与稳健性

冷却映射持久化 settings JSON：429→1h / 日限额→当日 / 5xx→15min / 超时→10min / 401·403→冷却至凭据变更。`RateLimiting` TokenBucket 每源独立（OpenSubtitles 1 req/s 保守值）。每源独立代理开关 + 全局 `proxy_url`。降级：单源冷却即跳过该源当轮，全源冷却→job 记 warning 正常收尾，不失败重试风暴。`subtitle_search_blacklist` 记 provider 级封禁观察（如 assrt 违规账号），仅提示不自动启用。

> 现状（2026-10-16）：🔧部分交付——冷却映射已兑现（`SubtitleSourceCooldownStore`：429→1h 或更长 Retry-After/日限额→当日/5xx·网络→15min/超时→10min/401·403→凭据变更前；持久化 settings JSON；全源冷却收尾不重试风暴 ✅）。**未实现**：TokenBucket 每源限速、每源独立代理开关、全局 `proxy_url`、per-source enabled 设置（registry 无按源启停开关）、日限额的配额感知触发（quota_remaining 未采集，见⑩——冷却表有该 reason 但无人触发）。`subtitle_search_blacklist` 表已建（M0008）但**零读写**。

## ⑨ 性能与规模化（NFR 预算表，QA 用例直接引用）

| 项 | 预算 | 手段（验证方式） |
|---|---|---|
| 缺口出队 | <200ms @10w 行 state | 覆盖索引+LIMIT 200；EXPLAIN QUERY PLAN 断言走 `ix_subtitle_search_state_dequeue` 无全表 SCAN |
| 自动单轮耗时 | P95 ≤30min | 批内并发 8（SemaphoreSlim）×源间并行：200/8×≤10s×3 源并行 ≈12.5min 上界 |
| 聚合搜索延迟 | P95 ≤15s | 逐源 10s 超时 + 流式渲染（前端并发） |
| 历史深页 | 与偏移量无关 | keyset 游标 `created_at>` + 等价性断言（offset 对照测试） |
| 每轮扫描量 | 与库规模无关 | 出队 LIMIT=batchLimit；StatisticsJob 增量 ≤5000 行/轮 |
| 哈希计算 | 懒算 | 仅进入候选打分时算 OpenSubtitles hash；结果写回 `movies.media_hash`/`episodes.media_hash`（列已存在，即持久缓存）；绝不批量预计算 |
| 批量写 | 单事务批 | 历史/会话/配额行按批攒 `SaveChanges`（BatchSize=100 先例）；**测试断言每轮 SQL 条数防 N+1**（出队后媒体元数据一次 JOIN 回字典） |
| 同步内存 | 10w 部电影不 OOM | 流式反序列化（⑤），峰值批 100 条 |
| 迁移门禁 | 四处计数 | MigrationTests 版本号/表/列/索引期望同步更新 |

> 现状（2026-10-16）：🔧性能套件未按本表执行（10w 冒烟 ❌，种子规模实际 **2000** 行，SubtitleSearchQueueServiceTests）。逐项修正：**「批内并发 8」未实现**——自动轮为串行 `foreach`（SubtitleSearchJob，媒体间串行；单媒体内源间 Task.WhenAll 并行），「200/8×≤10s×3 源≈12.5min 上界」算式失效，≤30min 预算无实测背书；**哈希缓存为进程内 (path,mtime)**（`LazyMediaHasher` ConcurrentDictionary，重启即失）——不写回 `media_hash` 列做持久缓存；**「测试断言每轮 SQL 条数防 N+1」未做**（出队 EXPLAIN QUERY PLAN 断言已在单测覆盖，全量 SQL 计数断言无）；迁移门禁 ✅（按 M0008/M0009 同步）。

## ⑩ v1 源实现要点（设计点 6/10 · 2026-10-15 扩充为 7 源，全 API 型免注册）

- **OpenSubtitles**（`Providers/OpenSubtitlesProvider.cs`）：官方 v1 REST `api.opensubtitles.com/api/v1`，头 `Api-Key`+自述 User-Agent；`GET /subtitles?movie_hashes=&languages=&imdb_id=`；下载两步 `POST /download`→`link` 重定向取文件。**配额常量：免费 API-key 40 downloads/日，写死为 `const int` 并注释官方文档 URL**（数值写进 quota_limit 列供前端水位展示，只展示不熔断——PM 定稿）。
- **SubSource**：JSON API，免 key；电影+剧集英文与多语言补充，POST 搜索。
- **SubDL**：`api.subdl.com`，免费 key（可内置公共值——OpenSubtitles 同款先例）；电影见长。
- **Gestdown**：`gestdown.info` HTTP API，免 key；Addic7ed 目录的 API 化镜像，**剧集 EN 专项**（以 TVDB/标题+季集查询），绕开账号源限制。
- **Podnapisi**：老牌 XML 接口，免 key；**上游 subliminal(MIT) 有完整实现，协议细节可直接参考移植**（许可最干净的一个）。
- **assrt**：`api.assrt.net/v1`，token 认证；`/sub/search?q=`→`/sub/detail?id=`（取文件列表）→`/sub/download`；响应含配额余量，写 `quota_remaining`。中文字幕主力。

> 现状（2026-10-16）：🔧assrt `quota_remaining` **未采集**——TelemetryService 统一以 quotaRemaining=null 写 `provider_quota_usage`（仅 OpenSubtitles 写死常量 quota_limit=40），配额水位无数据来源（呼应⑧「日限额触发未接」）。
- **shooter**：`POST shooter.cn/api/subtitle.php`，filehash 为分段 MD5 摘要算法（按官方公开算法实现，注释标出处）；响应 JSON 数组直接给下载 URL。中文哈希兜底。

跨源规则：候选去重键=(Language, 规范化 ReleaseInfo, Size)；并列消解=站点优先级（默认序 OpenSubtitles>assrt>SubSource>SubDL>Gestdown>Podnapisi>shooter，设置可调）×双语>单语×格式偏好。除 OpenSubtitles/assrt 已注明外，其余源实现细节以各源官方/社区文档为准，subliminal(MIT) 的 Podnapisi/TheSubDB 实现可作协议参照；subliminal_patch 的对应代码仅作行为观察、严禁搬运。v2 观察池：xunlei（CID 哈希）/a4k（非官方镜像）/TheSubDB（哈希免 key，库小）/BSplayer（哈希免 key，须按观察到的协议重实现）。

> 现状（2026-10-16）：🔧并列消解简化——实际排序=分数降序→hashMatch→**注册序**（即默认站点优先级，**不可设置**）；**「双语>单语」「格式偏好」加权未实现**。七源本体全部交付（含 shooter 分段哈希）。

配额 upsert 时机：每次 `SearchAsync`/`DownloadAsync` 返回后同批事务 `INSERT ON CONFLICT(date,provider) DO UPDATE searches/downloads=+1`。

## ⑪ 里程碑与估时（总 ~16.5 人日，2026-10-15 扩源后）

| 批 | 内容 | 验证 | 估时 |
|---|---|---|---|
| M1 地基 | ABI+Registry+模块骨架+M0006/M0007+MigrationTests 门禁更新 | 迁移上下行测试绿 | 2d |
| M2 源与打分 | 七 Provider+打分器+假源服务器（python 仿源，注入 429/超时/慢响应） | 单测+假源 E2E | 4.5d |
| M3 自动化批次 | SubtitleSearchJob+优先级队列+backlog/pin API+退避 | 出队 EXPLAIN 用例+10w 种子 | 2d |
| M4 下载管线 | 管线全链+历史+翻译入队+Coverage 刷新 | 集成测试（GBK/zip/ass 样本） | 2d |
| M5 性能批次 | 全索引落位+10w 种子性能套件+SQL 计数断言+StatisticsJob P0（缺口计算索引化分页） | QA 性能用例全绿 | 1.5d |
| M6 前端批次 | 搜索弹层（分源并发渲染）/源卡组（代理 switch+健康态+配额行）/批次卡 hint/历史屏（keyset 分页+四维筛选）/概览 KPI+trend 第 3 序列 | GUI 走查+假源演示 | 3d |
| M7 webhook+水位（P1） | webhook 高优入队去重+StatisticsJob 水位化（coverage_checked_at 增量+coverage_daily 日末 upsert，替换 StatisticsJob.cs L45/L75 的 `ToListAsync` 全量） | 10w 种子下单轮扫描量断言 | 1.5d |

> 现状（2026-10-16）：🔧M7 拆半——**webhook 半边已交付**（Radarr 侧高优入队+UNIQUE 去重保退避史；Sonarr 侧 R6 批次交付中）；**水位化零实现**：`coverage_checked_at`/`coverage_daily` 零读写，StatisticsJob 仍全量载入（扫描预算另有 QA 修复兜底，非水位化）。其余批次 M1–M6 对应内容均已交付（M6 前端仅搜索弹层+源卡，历史屏/KPI/批次卡未落）。

## ⑫ 风险与回滚（设计点 9 终态确认）

- 回滚三层：总开关 `subtitle_search_enabled=false`（即刻停，无需重启——SettingUpdatesHub 已有热更通路）；M0006/M0007 Down 完整（删表/删列/删设置行，M0004 Down 先例）；已落盘字幕为普通外挂文件，功能移除后无残留副作用，`subtitle_downloads` 历史随表删除即清理。
- 风险：10w 级同步内存（流式反序列化缓解，M1 即做）；OpenSubtitles 配额政策变动（常量单一落点可调）；shooter 接口无 SLA（冷却机制兜底，源可单独停用）。
- 技术债登记：xunlei/a4k v2 接口扩展点即 `ISubtitleProvider`，不预留半成品。

## ⑬ 许可合规

subliminal(MIT)/CSF(MIT) 可移植代码注明版权+许可；bazarr(GPL) 只借鉴事实性参数（hash 算法、分值观察），注释标出处，不搬代码；subliminal_patch 无 LICENSE 严禁搬运，一律按各源官方 API 文档重写。仓库根新增 `THIRD-PARTY-NOTICES.md`（`LICENSE` 同级）。

> 现状（2026-10-16）：✅`THIRD-PARTY-NOTICES.md` 已随 `23f0db4` 落仓库根。

## ⑭ 补录（2026-10-16）：已交付超集契约（R1–R5 修复链）

以下契约在实现/修复链中引入，**超出本文 v1.0 原契约**——均为已交付行为，按旧文档回退实现会引入回归，回退前必逐条核对：

1. **wrong-episode 封顶 70**：剧集请求中不含所请求 SxxEyy/NxMM 标签的候选分数封顶 70（低于自动线 75），防同剧错集高分混入（`SubtitleMatchScorer.WrongEpisodeScoreCap`，QA1 P1-2）。
2. **下载预算拆档**：搜索 JSON 档 10s+2MB 响应缓冲；字节下载走专用 `subtitle-{source}-download` client 120s，流式 100MB 硬上限（声明与实际取大者，QA2 P2-4/P2-5）。
3. **压缩包条目上限 64**（`SubtitleArchiveExtractor.MaxArchiveEntries`），防条目炸弹。
4. **候选缓存归属校验**：cacheKey 命中但 mediaId/mediaType 与搜索归属不符→400；过期→410（QA3 P3-2，搜索结果只属于一条库记录）。
5. **skipTranslation**：手动下载可「仅落盘、不入翻译队列」（锁定 API 契约，download body 布尔参）。
6. **Succeeded 复活**：入队 pass 重排「状态=Succeeded 但目标语言仍缺且无在途翻译」的队列行——修复 NOT EXISTS 守卫把已成功媒体永久跳过的断裂（QA3 P1-1）。
7. **Query 拆分**：search 请求接受可选 `query` 关键词覆写（预填标题、尾部 SxxEyy 标签拆离后并入季集匹配，R3b）。
8. **自动轮「已存在」前置核查**：Auto 源在进入①前核验库内目标语言字幕，已有即 AlreadyExistsSkipped 落历史；手动路径不受此门（同名走「(1)」后缀）。

---

**九个定稿设计点结论速查**：1) rank 固化列+覆盖索引+批内降级，UNIQUE 去重，webhook UPSERT 保退避史；2) 保留全量单 GET 但改流式反序列化，sonarr 天然分批确认，Year/ImdbId 落 Show/Movie；3) 拆 M0006(业务)/M0007(采集)，索引清单见④；4) 前端分源并发三请求，否决 SSE/SignalR 组；5) 端点清单见⑥含 trend 扩展；6) OpenSubtitles 40/日常量+注释来源，搜索/下载返回后同事务 upsert；7) 水位列 `coverage_checked_at`+coverage_daily 增量 upsert，P1 批次；8) 七批 16.5 人日（⑪表合计；2026-10-16 修正原文「14.5」笔误）；9) Down 迁移+总开关+普通外挂文件，终态确认。
