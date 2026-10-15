# 10 万级扩展性审计（存量功能）

日期：2026-10-15｜方法：3 路只读代码体检（同步链 / 扫描统计链 / API·前端查询面），每条发现附 file:line 证据与 10w 量级推算｜状态：待用户拍板修复批次
量级假设：≈2 万部剧/电影、10 万集、约 5 千-3 万季、15 万+字幕文件；NAS 为 QNAP x86 机械盘（IO 敏感）；SQLite 单库+WAL。

## 总体结论

1. **当前 440 媒体的生产库一切正常是因为量级兜底**；10w 级下有 **10 个 P0**（不可用/炸库/OOM 级）。
2. 系统性瓶颈不在任何单个 SQL，而在两个模式：**①`GetAllSubtitles` 对同一目录树按 4 种扩展名各递归一遍，被三个每日 job 以整库规模反复调用**；**②"算出已不缺"本身要扫完整库**（AutomatedTranslationJob 稳态每轮 40 万次目录走查）。
3. **translation_requests 表零索引**（M0001 建表后从未加），30s 轮询的 3 个端点在百万行下每次全表扫。
4. 同步链有一个**一行修复砍掉 19 万次 HTTP 请求**的 N+1（Sonarr 响应本含 episodeFile，模型未映射）。
5. EmbeddedTrackScanJob **首次全量 6-14 小时**且互斥锁 5 分钟等待即失败——次日起每天触发即异常。
6. 与字幕搜索立项高度协同：体检的"扫描水位化/语言清单落库求差"正是该立项 M5/M7 批次，**合并施工可省约 2 人日**。

## P0 清单（10 条）

| # | 位置 | 问题 | 10w 推算 | 修复方向 |
|---|---|---|---|---|
| P0-1 | `IntegrationService.cs:34-35`+`RadarrService.cs:17` | 整体反序列化 `List<RadarrMovie>`，写库全程持有 | 300-500MB JSON→托管堆 1-2GB+跟踪实体 2-4GB，OOM | 流式反序列化（ResponseHeadersRead+分批），与字幕搜索方案⑤一致 |
| P0-2 | `EpisodeSync.cs:25-39`+`SonarrService.cs:29,53` | 每季拉 2 次+**每集 1 次** GetEpisodePath | ≈19 万次 HTTP：30ms≈1.6h、300ms≈16h | `SonarrEpisode` 补 episodeFile 映射，砍掉全部 GetEpisodePath |
| P0-3 | `SyncMovieJob.cs:57`/`SyncShowJob.cs:57` | 整轮同步包单事务，数小时持 SQLite 写锁 | WAL 无法 checkpoint 膨胀至 GB；结尾异常全回滚 | 去外层长事务，靠既有 BatchSize=100 批提交 |
| P0-4 | `SubtitleService.cs:62-64` | `GetAllSubtitles` 每种扩展名各 `Directory.GetFiles(*,AllDirectories)` 一遍=**4 遍递归** | StatisticsJob 每季 2 次调用=8 遍/季；一轮 6 万次递归枚举，热 5-20min、冷 1-3h | 单遍 `EnumerateFiles` 按扩展名集合过滤 |
| P0-5 | `AutomatedTranslationJob.cs:150-153,229-241` | 求差在文件系统结果上算：游标循环凑够 N 个翻译才停，稳态 hash 全命中时**扫完整库** | 10w 集×4 遍枚举=40 万次走查/轮（1-3h+）+全实体载入 100-300MB | 每媒体语言清单落库，SQL 求差选题（=字幕搜索 M0007 coverage_daily 基建） |
| P0-6 | `EmbeddedTrackService.cs:506-540,666-714`+`EmbeddedTrackScanJob.cs:32-34` | 首次全量 ffprobe 无上限串行+每文件 2 次写事务；`DisableConcurrentExecution(5min)`+`AutomaticRetry(0)` | 首次 6-14h；**次日起每天触发即失败**；20 万次 WAL 提交 | 每轮上限+断点续扫+批量提交；互斥改"未结束则跳过" |
| P0-7 | `MediaLibraryService.cs:55-71` | /media/library 全库 Include(Seasons→Episodes) 载入内存再分页 | 10w episode 实体+跟踪，每页 20 条也要物化整库，GB 级 | 投影聚合下推 SQL+LIMIT/OFFSET |
| P0-8 | `MediaLibraryService.cs:267-278`+`MediaController.cs:172-192` | "每媒体最新请求"全表载入内存 GroupBy；详情页 `?id=` 分支仍跑完整 library 聚合 | 百万请求行（含 StackTrace 大列）进内存；**每次进详情页**都付全量代价 | SQL GROUP BY 下推；独立单媒体端点 |
| P0-9 | `M0001_Baseline.cs:122-146` | **translation_requests 零索引**（media_id/status/completed_at/created_at 全裸奔） | status/mediaIds/趋势/计数全表扫；30s 轮询×3 端点 | 补 4 组索引（一条迁移） |
| P0-10 | `ProgressService.cs:32-70`+`SubtitleTranslationService.cs:258` | 每翻译一行：落库+**全组**广播；每 1% 进度再一轮 | 并发 N 任务×数千行×全组客户端=广播风暴+逐行写放大 | 按 requestId 分组播+进度节流+逐行批量写 |

## P1 清单（12 条）

| # | 位置 | 问题（10w 推算略，见体检底稿） | 修复方向 |
|---|---|---|---|
| P1-1 | M0001:33,48,76 | movies.radarr_id/shows.sonarr_id/episodes.sonarr_id 无索引，同步与 webhook 按此查找全表扫 | 三个唯一索引 |
| P1-2 | `ShowSyncService.cs:38-54` | 批保存后从不 `ChangeTracker.Clear()`，跟踪图单调增长，DetectChanges 准 O(n²) | 每批 Clear |
| P1-3 | `MediaService.cs:194-205` | webhook 404 兜底触发**全量**重同步且挤占 webhook 队列 | 兜底改为提示/仅清孤儿 |
| P1-4 | `MediaLibraryService.cs:66` 等 | 标题 `ToLower().Contains()` 中缀 LIKE，B-tree 不可用 | FTS5 或 NOCASE 前缀 |
| P1-5 | `MediaController.cs:84-96` | 详情页字幕卡每季 4 遍实时递归扫描无缓存 | 复用统计快照/加缓存 |
| P1-6 | `MediaDetailPage.vue:636-646` | 剧集历史把全部 episode id 拼进 URL（千集剧≈8KB 逼近 Kestrel 上限） | 按 show_id 过滤+分页 |
| P1-7 | `SubtitleService.cs:28-30,243-263` | 嗅探缓存上限 500 条，10w 文件下形同虚设，无标记文件每轮重嗅 | 嗅探结果落库（path+mtime→lang） |
| P1-8 | `InfrastructureModule.cs:119-126`+`ScheduleService.cs:44-51` | WorkerCount 默认 1：长翻译轮饿死 system 队列；调大则 04:00 三 job 同刻 IO 叠加 | 队列 worker 分配文档化+默认 cron 错峰 |
| P1-9 | `TranslationRequestQueryService.cs:234-240` | GetStatusCounts 全表 GROUP BY（每 30s 被调） | (status) 索引/快照计数 |
| P1-10 | `StatisticsService.cs:60-72` | trend 端点实时拉窗口内全部完成请求（含大列）进内存算日聚合，无日聚合表 | SQL GROUP BY 日+provider（更正：DA 所称 DailyTrendStatistics 表不存在） |
| P1-11 | `StatisticsJob.cs:45,75-78` | Movies/Shows/Episodes 全实体全列载入入跟踪器，峰值 200-500MB | 投影只取所需列 |
| P1-12 | `SubtitleController.cs:35-40` | POST /subtitle/all 任意 path 递归无白名单/上限（**兼安全问题**：可枚举服务器任意可读目录） | 限定 media 根内+上限（复用 scan-path 500 cap） |

## P2 清单（9 条，打包处理）

PathConversion 每次换算查表无缓存（`PathConversionService.cs:81`）；`GetEpisodes` 带 includeImages 白传 2-5 倍体积（`SonarrService.cs:32`）；媒体删除走 Include 全子实体逐行 DELETE（`ShowSyncService.cs:82-103`，改 ExecuteDeleteAsync）；BytesOf 每字幕×语言重复 stat（`StatisticsJob.cs:152-157`）；CleanupJob 无 embedded 提取孤儿回收（`EmbeddedTrackService.cs:417-426`）；AutomatedTranslation 内 seasons IN+线性查找 O(E×S)（`:233-241`）；pageSize 无上限（`MediaController.cs:169`，clamp 100）；千集逐集表无虚拟滚动（`MediaDetailPage.vue:99`）；季目录取"第一个有文件集"路径的语义脆弱（`SeasonSync.cs:66-96`）。

## 已过关项（节选，证据在体检底稿）

- 同步无 updated_at 写放大（DetectChanges 判 Unchanged 零 UPDATE）；批写 BatchSize=100 先例已在。
- 默认 WorkerCount=1 全队列串行（安全兜底）；Hangfire 独立库，簿记不与主库争锁。
- webhook 主路径是单条增量（仅 404 兜底除外）。
- EmbeddedTrackScan 的 mtime 增量设计正确（稳态 2-10min 过关；问题只在首次全量与互斥语义）。
- 归整服务有 500 文件硬上限；ffprobe 有超时与进程树清理。
- 前端 LibraryPage/⌘K 均真服务端分页+debounce；覆盖率/估价走统计快照不扫盘。
- media_subtitle_tracks、request_events/lines、auth_sessions 索引齐全；EF 全局 SplitQuery；PRAGMA（WAL+busy_timeout 120s+synchronous=NORMAL）配置正确。

## 修复批次建议（与字幕搜索立项合流）

| 批 | 内容 | 估时 | 验证 |
|---|---|---|---|
| S1 索引与查询下推（止血） | P0-9/P1-1 一条迁移补 8 组索引；P0-7/P0-8 library SQL 化+详情页独立端点；P1-10 trend SQL 化 | 2.5d | EXPLAIN QUERY PLAN 断言+10w 种子库 |
| S2 同步链 | P0-1 流式反序列化；P0-2 补 episodeFile 映射（一行砍 19w 请求）；P0-3 去长事务；P1-2 ChangeTracker.Clear；P2 删除改 ExecuteDelete | 2.5d | 假 Radarr/Sonarr 灌 2 万部压测 |
| S3 扫描链 | P0-4 单遍枚举；P0-6 EmbeddedTrackScan 上限/续扫/批量提交/互斥语义；P1-5 详情页字幕卡走快照；P1-11 统计投影化 | 2.5d | 10w 种子计时断言 |
| S4 求差落库 | P0-5 语言清单落库+SQL 求差（**=字幕搜索 M0007 coverage_daily，一次施工两用**）；P1-7 嗅探结果落库；**+音轨语言列合流（[库增强需求卡 A](library-enhancements-prd.zh-CN.md)：`audio_langs ∩ {target} → 豁免` 在 SQL 求差层排除中文音轨媒体）** | 1.5d | 与字幕搜索 M5 性能套件共用 |
| S5 广播与前端 | P0-10 SignalR 分组/节流/批量写；P1-6 URL 修正；P2 pageSize clamp/虚拟滚动 | 1.5d | 并发任务压测 |
| S6 小项打包 | 其余 P1/P2（FTS5 视需要、白名单、cron 错峰文档化等） | 1d | 回归门禁 |

合计 ≈11.5 人日；S3/S4 与字幕搜索立项 M5/M7 重叠约 2 人日，**建议顺序：S1→S2→S3 先行（10w 通行证），随后字幕搜索 M1 起步、其 M5/M7 并入 S3/S4 已有成果**。
