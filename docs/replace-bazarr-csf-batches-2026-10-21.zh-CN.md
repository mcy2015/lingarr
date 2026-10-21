# 替换 bazarr + ChineseSubFinder 实施批次任务书（B01–B12）

> 依据 2026-10-21 三方对照（bazarr master bfe4593b / CSF 归档版 3335a9c9 / lingarr 现状实码核查）。目标：lingarr 全量吸收两工具的日常价值后退役它们。
>
> **执行约定**：批次按泳道并行、同文件批次串行（见末尾依赖矩阵）；每批独立验收后再进同泳道下一批。实现走 lingarr-dev 代理（白名单制，不 commit 不跑门禁）；服务端测试在 CI 跑（本机无 dotnet SDK）；JSON 合同枚举与仓库线形态逐字对齐（PascalCase）；迁移手写 FluentMigrator，下一个可用编号 **M0014**；新增依赖必须登记 `Directory.Packages.props`（CPM）+ `THIRD-PARTY-NOTICES.md`。
>
> **范围修正（对照 2026-10-21 会谈结论）**：三项经实码核查缩范围——①繁简转换 OpenccNetLib 已在库（仅导入时用）→ B01 只做管线接入+存量批处理；②内封轨道已有每日 ffprobe 扫描+抽取（F2a）→ B04 只做覆盖语义+入口+Whisper 兜底；③双语输出已存在（`bilingual_subtitle_output` 逐行合成 + `SubtitleConversionService` 重渲染）→ B06 只做「两个已存在文件合并」工具。fps 转换不单独做，并入 B05 时间轴校正的帧率比率缩放。

---

## 批次 B01：后处理框架 + 繁简存量批处理

**目标**：建立落盘后处理阶段抽象（供 B05/B10 挂载），并把已有 OpenCC 转换接入下载落盘路径与存量字幕。

**前置**：无。

### 文件白名单

新增：
- `Lingarr.Server/Modules/Subtitle/PostProcessing/ISubtitlePostProcessor.cs` — `Task<PostProcessResult> ProcessAsync(string filePath, PostProcessContext, CancellationToken)`；`PostProcessResult` 记录是否改动+原因
- `Lingarr.Server/Modules/Subtitle/PostProcessing/ChineseVariantStage.cs` — 调 `ChineseSubtitleConverter.Convert`（按行转换后用对应 Writer 写回；ass 保留样式层，只动 `Lines`）
- `Lingarr.Server/Modules/Subtitle/PostProcessing/CompositePostProcessor.cs` — 按 DI 注册顺序串行执行各 stage，任一失败记 warning 不阻断落盘（结果行照写，error 字段带 stage 名）
- `Lingarr.Client/src/cells/settings/components/PostProcessCard.vue` + 同名 `.spec.ts` — 设置卡（automation tab）：繁简模式单选 none/simplified/traditional（默认 none，与导入选项同词汇）+ 存量批处理入口（预览计数→确认执行，复用 SubtitleNormalizeService 的 preview/confirm 交互模式）

修改：
- `Lingarr.Server/Modules/SubtitleSearch/Pipeline/SubtitleDownloadPipeline.cs` — 在 `WriteAtomicAsync`(:1300 调用点) 之后、`RecordAsync`(:391) 之前插 CompositePostProcessor 调用（单条路径与季包 `TryLandEntryAsync`(:848) 成功分支两处）
- `Lingarr.Core/Configuration/SettingKeys.cs` — SubtitleSearch 嵌套类加 `PostProcessChineseVariant = "subtitle_postprocess_chinese_variant"`
- `Lingarr.Server/Modules/Infrastructure/ScheduleService.cs`、`StartupService.cs` — 种子设置行
- `Lingarr.Client/src/cells/settings/pages/SettingsAutomationPage.vue` — 挂卡
- `Lingarr.Client/src/i18n/locales/en.json` + `zh-CN.json` — `settings.postProcess.*`
- 存量批处理 API：新控制器或挂 `Controllers/` 现有模式（扫描全库 .srt/.ass/.ssa/.vtt，按设置转换，上限 500 文件/批，逐文件幂等——转换后内容 hash 相同则跳过）

测试：PostProcessing 三个类（含 ass 样式保留断言）、管线插入点（Moq stage 验证调用顺序与失败不阻断）、存量批处理（SQLite in-memory + 临时目录真文件）。

### 已定决策
- 后处理默认 **none**：转换改变存量文本，永不默认执行（与导入选项同一哲学，ChineseSubtitleConverter 头注释既定立场）。
- 编码修复**不进**后处理框架：下载路径 :303 `ConvertToUtf8` 已覆盖；存量 GBK 文件由内容嗅探链路（SubtitleEncodingService）在读取时兜底。

### 验收标准
1. 落盘后繁简模式=simplified 时新下载的 .srt/.ass 内容为简体，ass 样式层（`[V4+ Styles]`/`Style:` 行）逐字节不变；模式=none 时字节与无本批完全一致（回归门）。
2. 任一 stage 抛异常：字幕文件仍落盘、历史行仍写入、error 字段含 stage 名、无重抛。
3. 存量批处理预览不写盘（mtime 不变），确认后仅转换内容确实变化的文件。
4. CI 三绿（backend/migrations/frontend vue-tsc）；`scripts/check-logging.sh --gate` 不超基线。

**估时**：1 人日。

---

## 批次 B02：字幕升级重搜（bazarr upgrade 移植）

**目标**：已落盘字幕若当年分数低，定期重搜换优；换优成功删旧文件、历史留痕。

**前置**：无（与 B01 同文件 `SubtitleDownloadPipeline.cs`，**同泳道串行**，B01 先行）。

### 文件白名单

新增：
- `Lingarr.Server/Modules/SubtitleSearch/Jobs/SubtitleUpgradeJob.cs` — `[DisableConcurrentExecution(1800)] [AutomaticRetry(Attempts=0)] [Queue("system")]`，Execute 首尾 `UpdateJobState`，取消令牌契约照 SubtitleSearchJobTests:410-450（取消绝不 MarkFailed、unwind 后 rethrow）
- `Lingarr.Server.Tests/Modules/SubtitleSearch/Jobs/SubtitleUpgradeJobTests.cs`

修改：
- `Lingarr.Core/Enum/`（SubtitleDownload.Source 枚举所在）— 加 `Upgrade = 3`（int 存储，无迁移；JSON 合同 PascalCase `Upgrade`）
- `Lingarr.Server/Abstractions/SubtitleCandidate.cs` 或调用点 — `DownloadAsync` 增加 `Source` 传参路径（复用现有 Source 参数即可，无需新签名；确认为准）
- `Lingarr.Server/Modules/SubtitleSearch/Scoring/SubtitleMatchScorer.cs` — `IsAutoEligible` 增加可选 `int? minScoreOverride` 重载
- `Lingarr.Server/Modules/SubtitleSearch/Pipeline/SubtitleDownloadPipeline.cs` — 升级落盘成功且新路径 ≠ 旧路径时删除旧文件（照 ComputeOutputPathAsync 输出判断）
- `Lingarr.Core/Configuration/SettingKeys.cs` — `subtitle_upgrade_enabled`（默认 false）、`subtitle_upgrade_schedule`（默认 `"0 4 * * *"`）、`subtitle_upgrade_score_margin`（默认 3）、`subtitle_upgrade_window_days`（默认 14）
- `Lingarr.Server/Modules/Infrastructure/ScheduleService.cs` — BuiltinJobs + RegisterRecurring case + IsBuiltinDisabled 联动 enabled
- 迁移 `Lingarr.Migrations/Migrations/M0014_SubtitleUpgradeSettings.cs` — 种子 4 行 settings
- `Lingarr.Client`：history 页 source 过滤器加 `Upgrade` 选项（`SubtitleSearchHistoryPage.vue` + `types/services.ts` source 联合类型 + 两份 locale）

### 已定决策（选片与换优语义）
- 候选目标：每 `(media_type, media_id, language)` 取**最近一条 Succeeded** 的 `subtitle_downloads`，且满足：coverage 仍由该文件满足（文件存在）、`created_at > now - window_days`、`match_score <= 100 - margin`。
- 换优判定：重搜后取最高分候选，须 `score >= max(subtitle_search_auto_score 现值, 旧分 + margin)` 才落盘；否则不动（记 history 行 `Result=LowConfidenceSkipped`）。
- 防循环：margin≥1 恒成立（默认 3）；同分不换。
- 批量预算：单轮上限 200 目标（照 SubtitleSearchJob batchLimit Clamp 模式），10w 级库首轮回灌靠逐日窗口消化。

### 验收标准
1. 旧分 70、margin 3：候选 80（≥max(75,73)）换优落盘、旧文件删除、history 新行 `Source=Upgrade`；候选 74 不动。
2. 取消令牌触发：进行中目标不 MarkFailed、无 Skipped 副作用、job rethrow `OperationCanceledException`。
3. `subtitle_upgrade_enabled=false` 时 ScheduleService 不注册 recurring、job Execute 短路。
4. 前端 history 过滤 `Upgrade` 仅显示升级行；vue-tsc 过。
5. CI 三绿 + 审计门不超基线。

**估时**：2 人日。

---

## 批次 B03：Plex 库刷新

**目标**：字幕落盘/翻译写回后自动刷新 Plex，替代 bazarr 的媒体服务器通知职责。

**前置**：无（Integration 模块独立区域，可与 B01/B02 并行；对 `SubtitleDownloadPipeline.cs` 的改动仅 :423 广播区一行级，与 B01/B02 改动行不重叠，**冲突时以 B01/B02 先合、B03 rebase**）。

### 文件白名单

新增：
- `Lingarr.Server/Modules/Integration/PlexSettingsProvider.cs` — 照 `IntegrationSettingsProvider.cs:21-44` 抄：`plex_url` 明文 + `plex_token` 加密，缺一返回 null
- `Lingarr.Server/Modules/Integration/PlexService.cs` — `RefreshLibraryAsync()`：`GET {url}/library/sections`（`Accept: application/json`，`X-Plex-Token` 头）解析 Directory 列表并缓存 10min → `POST {url}/library/sections/all/refresh`；任一请求失败记 warning 不抛（刷新是尽力而为）
- `Lingarr.Server/Modules/Integration/PlexRefreshJob.cs` — `[Queue("system")] [AutomaticRetry(Attempts=2)]`，`DisableConcurrentExecution(120)` + 进程内 guard（照 EmbeddedTrackScanJob 的 `_scanInProgress` 模式防锁超时连锁）
- `Lingarr.Client/src/cells/settings/components/PlexCard.vue` + `.spec.ts` — MediaSources 卡同款行式+编辑模态（URL + token reveal，照 InputComponent trailing 插槽模式）

修改：
- `Lingarr.Core/Configuration/SettingKeys.cs` — Integration 类加 `plex_url` / `plex_token` / `plex_refresh_enabled`（默认 false）
- `Lingarr.Server/Modules/Integration/IntegrationModule.cs` — DI 注册
- `Lingarr.Server/Modules/SubtitleSearch/Pipeline/SubtitleDownloadPipeline.cs` — Hub 广播 :423 区域：enabled 时 `IBackgroundJobClient.Enqueue<PlexRefreshJob>`
- `Lingarr.Server/Modules/Translation/TranslationJob.cs` — Writeback 广播 :422-432 区域同上
- `Lingarr.Client/src/cells/settings/pages/SettingsMediaPage.vue`、两份 locale（`settings.plex.*`）、`types/setting.ts`

### 已定决策
- v1 刷新**全库**（`/library/sections/all/refresh`），不做按 section 定向；Plex 自身增量很快，定向收益低。
- 触发即 Enqueue、由 job 的并发保护自然合流（落盘风暴→一串 job 大多空转退出）；不引入显式去重队列。

### 验收标准
1. enabled 且配置齐全：落盘成功路径与翻译写回路径各验证一次 Enqueue（Moq IBackgroundJobClient）。
2. token/url 缺失或 Plex 5xx：订阅路径无异常、字幕照常落盘、warning 含状态码。
3. `plex_refresh_enabled=false`：零 Enqueue。
4. 设置卡 spec：reveal 切换、URL 校验、保存走 settingService 契约。
5. CI 三绿。

**估时**：0.5 人日。

---

## 批次 B04：内封字幕覆盖语义 + 抽取入口 + Whisper 兜底（可选开关）

**目标**：内封轨道从「详情页展示」升级为可计入覆盖、可一键抽为外挂；全源未命中时可走 Whisper 转录生成。对标 bazarr `embeddedsubtitles` + `whisperai`。

**前置**：无。

### 文件白名单

修改：
- `Lingarr.Server/Modules/SubtitleSearch/Pipeline/SubtitleCoverageRefresher.cs` + `SubtitleCoverageMath` — 新设置 `embedded_track_counts_as_coverage`（默认 false）为 true 时：目标语言若有匹配语言的内封轨道（EmbeddedTrack 表，语言归一化复用 SubtitleLanguageParser），按「内封覆盖」计——覆盖显示带 `embedded` 维度徽章（照 9b0180f 同概念徽章全页只显一处的既定裁决），且**不**再入缺字幕队列
- `Lingarr.Server/Modules/Media/EmbeddedTrackService.cs` — `PrepareTranslationSource`(:692-715) 已有抽取到 `{base}.{lang}.srt` 的能力，包一个面向用户的 `ExtractToSidecarAsync`（幂等：目标已存在则跳过）
- Controllers：内嵌轨道行级动作端点（POST extract，`[Authorize("LingarrAccess")]`，照 WebhookController 鉴权模式）
- `Lingarr.Client/src/cells/library/pages/MediaDetailPage.vue` 内嵌轨道区 — 行级「抽取为外挂」按钮（data-test）+ 覆盖徽章；`SubtitleSearchModal.vue` — 结果区顶部「本地内封轨道」chip 组（有匹配语言的轨道时显示，点击触发抽取端点后刷新）
- `Lingarr.Core/Configuration/SettingKeys.cs` + `StartupService.cs` — `embedded_track_counts_as_coverage`、Whisper 三键（`whisper_endpoint`/`whisper_model`/`whisper_api_key` 加密）默认关闭

新增（Whisper 兜底，全部 default-off）：
- `Lingarr.Server/Modules/SubtitleSearch/Providers/WhisperProvider.cs` — `[SubtitleProvider("whisper")]`；SearchAsync：剪辑（复用 `AudioLanguageService.ExtractClip` 的 ffmpeg 30s→改 120s 片段）→ OpenAI 兼容 `POST {endpoint}/audio/transcriptions`（multipart file+model+language）→ 生成单候选 `Provider="whisper"`，`HashMatch=false`，分数走常规打分；注册进 registry 尾部，仅在设置齐全时启用
- `Lingarr.Client` 设置卡 `SubtitleSourcesCard` 或新 `WhisperCard`（sources tab，含连接测试）

### 已定决策
- 内封计覆盖默认关：开着会让「已有内封中字但用户偏好外挂」的媒体停止补搜，必须用户显式选择。
- Whisper 是**源**不是源后处理：走 registry 优先级链尾部，不另起流水线。
- 转录语言跟音轨语言（AudioLanguages 已有），zh 音轨产 zh 字幕直落盘，en 音轨产物进翻译流水线。

### 验收标准
1. 设置关：覆盖计算与现状逐字节一致（回归门）；设置开：有 zh 内封轨的媒体不再出现在 EnqueueGapMediaAsync 的 gap INSERT 结果里（SQLite 测试断言 SQL 行集）。
2. 抽取端点：目标文件已存在时 200 且 mtime 不变；抽取后详情页字幕文件卡出现新行。
3. Whisper 关（默认）：registry providers 列表不含 whisper；开且配置全：`GET /api/subtitle-search/providers` 含 whisper，全源未命中时产出候选并可落盘（夹具：mock multipart 响应）。
4. CI 三绿 + 审计门（注意模板不得出现明文 `apikey=` 字样，包括注释示例）。

**估时**：1.5 人日（Whisper 兜底 +1，可拆为独立后续批）。

---

## 批次 B05：时间轴校正（CSF 王牌移植，含帧率缩放）

**目标**：对已落盘字幕自动/手动做「VAD 包络 + FFT 互相关 + 帧率比率枚举」对齐，替代 CSF `sub_timeline_fixer`。fps 不匹配的缩放校正并入本批，不单独做 fps 转换。

**前置**：**B01**（挂后处理框架）。设计先行：实现前先在本文件附一节「B05 实现纪要」（算法参数、FFT 尺寸、门槛实测值），实测校准后固化。

### 算法规格（干净室：按本规格实现，不打开 CSF/ffsubsync 仓库）

1. **音频包络**：ffmpeg 流式解码全片音频 → 16kHz 单声道 s16le PCM（`-vn -ac 1 -ar 16000 -f s16le`，读 stdout 流，逐 10ms 窗累计能量，**PCM 不落内存**只留包络数组）；包络 = 每窗能量的 log 归一化序列。
2. **字幕包络**：cue 时间映射到同一 10ms 网格的 0/1 语音序列。
3. **比率枚举**：R ∈ {1.0, 25/23.976, 23.976/25, 24/23.976, 23.976/24, 25/24, 24/25, 1.001, 0.999}。每个 R：字幕序列按 R 缩放 → 与音频包络做 FFT 互相关（Math.NET Numerics，尺寸取 next-pow-2）→ 峰值即偏移与相关分。
4. **门槛**（照 CSF 双重验证精神）：最优解相关分 ≥ 0.10（归一化，实测校准）；|offset| ≤ 700s；|offset| < 0.2s → 判定无需校正直接成功返回；字幕总时长 ≤ 视频时长×1.05；R≠1 时要求其相关分比 R=1 的最优高出 ≥10%（防误选缩放）。
5. **写回**：cue 时间 `t' = t×R + offset`（含负偏移钳制到 0 之后整体平移校验），原文件备份为 `{原名}.lingarr-tl-bk`（同目录），重复运行先查 .bk 存在则跳过（幂等）；提供还原动作（.bk 换回）。

### 文件白名单

新增：
- `Lingarr.Server/Modules/Subtitle/TimelineSync/` — `TimelineSyncService.cs`（编排）、`AudioEnvelopeExtractor.cs`（ffmpeg 流式）、`EnvelopeCorrelator.cs`（FFT 互相关+比率枚举）、`SubtitleTimelineRewriter.cs`（经现有 Parser/Writer 往返，ass 样式保留）
- `Lingarr.Server/Modules/Subtitle/PostProcessing/TimelineSyncStage.cs` — 挂 B01 框架；设置 `timeline_sync_on_download`（默认 **false**，NAS 实测一个 soak 期后再议默认值）
- `Directory.Packages.props` + `THIRD-PARTY-NOTICES.md` — Math.NET Numerics（MIT）
- API：POST 手动触发（media+file）/ POST 还原，`[Authorize("LingarrAccess")]`
- `Lingarr.Client`：手动入口挂字幕文件行（与 B10 合并实现亦可，见 B10）

修改：
- `Lingarr.Core/Configuration/SettingKeys.cs` + `StartupService.cs` — `timeline_sync_on_download`、`timeline_sync_min_score`（默认 0.10）
- `Lingarr.Client` 两份 locale + `types`

测试：包络抽取（假 ffmpeg 进程喂固定 PCM）、互相关（合成已知偏移的序列，断言 offset 收敛 ±10ms、比率识别正确）、写回幂等与还原、stage 挂接（B01 mock 模式）、门槛各分支。夹具放 `Lingarr.Server.Tests/Fixtures/Subtitles/timeline/`。

### 已定决策
- 基准只用音频（CSF 的「优先内封字幕做基准」首版不做——内封轨不一定存在，音频路径全覆盖；后续若要加，复用 B04 抽取）。
- 校正失败（门槛不过）不删字幕不改文件，返回原因字符串，历史/任务面板可见。

### 验收标准
1. 合成夹具：字幕整体偏移 +2.5s → 校正后逐 cue 时间差 <40ms；偏移 -1.2s 同过。
2. 帧率夹具：23.976→25 错帧字幕 → R 识别正确且校正后误差 <0.1%时长。
3. 音频缺失/ffmpeg 起不来：抛 `FFprobeUnavailableException` 同族异常（ffmpeg 路径解析复用 `ResolveBinaryPath`/`LINGARR_FFMPEG_PATH`），订阅方不崩溃、字幕保留。
4. 幂等：连续两次运行，第二次判定「无需校正」且 .bk 不被覆盖。
5. CI 三绿；Math.NET 登记 TWO 处（CPM + notices）齐。

**估时**：4 人日（含算法校准 soak）。

### B05 实现纪要（2026-10-21，实现前定稿；标「实测」的数字由合成夹具校准后回填）

**网格与 FFT 尺寸**：统一 10ms 窗（16kHz → 160 采样/窗）。音频包络 A 与字幕包络 S 均按媒体时长栅格化为长度 L 的序列，FFT 尺寸 N = next-pow-2(2L)（2h 片 L=720k → N=2²¹；20min 剧 L=120k → N=2¹⁸）。Math.NET Numerics `Fourier.Forward/Inverse`（复数、默认选项：正变换不缩放、逆变换 1/N）：音频谱只算一次，9 个比率各做一次字幕谱 + 一次 `IFFT(conj(FA)·FS)`，共 19 次变换；2²¹ 情形内存峰值约 3×32MB（复数双精度缓冲复用），单次校正总计算秒级。

**包络归一化**：音频侧 PCM 流式累计，每窗能量 e=Σ(x/32768)²，取 v=log10(1+e)（log 压缩把 60dB 动态范围压进 ~2 个数量级，静音窗≈0、满幅语音窗≈1.9）；字幕侧同一 10ms 网格的 0/1 语音序列（按比率 R 逐 cue 缩放后栅格化：窗 j 计入当且仅当与缩放后 cue 区间相交）。相关前双侧各自 z-score（减均值、除总体标准差），相关分 = 互相关峰 / (‖A‖·‖S‖)，即归一化互相关系数，落在 [-1,1]，门槛语义与 0.10 直接可比。

**lag→offset 映射**：r = IFFT(conj(FFT(A))·FFT(S))，r[m] 对应 offset = −m×10ms（m ≥ N/2 时为负 lag，取 m−N）；对全部 lag 求峰（不预裁 ±700s——搜索本身免费，越界交给门槛分支，这样「偏移超限」门槛可以确定性触发而非被搜索窗掩盖）。

**门槛实测值**（合成夹具实测回填，2026-10-21，夹具=600s 媒体、120 条 cue、语音占比 ~48%，见 `Lingarr.Server.Tests/Fixtures/Subtitles/timeline/TimelineFixtureFactory.cs`）：完全匹配对齐相关分 **1.000**；纯偏移 +2.5s 修正后峰 **0.996**；23.976 字幕配 PAL 音频时正确 R（0.959040）峰 **0.999** 而 R=1 残留峰仅 **0.050**；完全不相关（随机稀疏字幕错配）峰 **0.050**；稀疏语音 +710s 偏移全 lag 搜索峰 0.965、offset 精确 −710.000s。默认门槛 **0.10** 恰好把两类 0.05 拒之门外、给真实对齐（≥0.99）留 10 倍余量；错帧场景由「R≠1 需比 R=1 最优高 ≥10%」规则纠偏（实测 0.999 vs 0.050，比值 20 倍），0.10 只拦彻底不相关（音轨对不上/字幕错文件）。偏移收敛精度 = 窗格 10ms（AC1 的 40ms 验收余量 4 倍）。

**9 个比率的确切取值与推导**：R ∈ {1.0, 25/23.976, 23.976/25, 24/23.976, 23.976/24, 25/24, 24/25, 1.001, 0.999}（全部 double 字面量精确书写，如 25.0/23.976 = 1.0427100…）。推导：1.0 = 纯偏移基线；中间 6 个 = 影视三大帧率（NTSC film 23.976、film 24、PAL 25）两两比值，覆盖 PAL 拉伸（25/23.976≈1.04271）、PAL 加速（23.976/25≈0.95904）及 24↔25、24↔23.976 的母带互换；1.001 / 0.999 = ±0.1% 微缩放，兜底 drop-frame（1000/1001）标记误差与流媒体抓帧漂移。选择规则：先取 R=1 最优分 s₁，任一 R≠1 候选 s_R 只有在 s_R ≥ 1.10×s₁ 时才胜出（防误选缩放），否则回落 R=1；R=1 因时长门槛（缩放后末 cue ≤ 音频时长×1.05）被跳过而某缩放候选可行时，允许该缩放候选直接胜出（PAL 错帧时 R=1 的字幕末尾越界正是症状之一）。

**负时间钳制**：t′ = round(t×R + offset)，start′/end′ 各自钳 ≥0；钳制后若 end′ ≤ start′ 则抬 end′ = start′+1ms（保序不产生负时长 cue）；被钳 cue 数记入结果。备份 `{原名}.lingarr-tl-bk` 与写回顺序：先 `File.Copy(原, .bk, overwrite:false)`（存在即抛，幂等门已先行检查，竞态时宁可失败不覆盖），再临时文件原子替换原文件——任一步失败原文件字节不变。

**其它实现裁定**：后处理 stage 在 `PreviewOnly` 模式直接 Unchanged（不解码媒体音频——批量预览逐文件解码整库不可接受）；下载管线的 stage 依赖 `PostProcessContext.MediaVideoPath`（本批新增尾参，B01 契约允许扩展）；字幕包络/时长门槛按「音频包络长度=媒体时长」计（免 ffprobe 二次调用）。

---

## 批次 B06：双文件双语合并工具

**目标**：把两个**已存在**的外挂字幕（如下载的 zh + 已有 en）按时间轴合并为双语文件。翻译时的逐行双语（既有能力）不在此批范围。

**前置**：无（可与 B05 并行；复用同一 Parser/Writer）。

### 文件白名单

新增：
- `Lingarr.Server/Modules/Subtitle/BilingualMergeService.cs` — 读两文件（`SubtitleService.ReadSubtitles`）→ cue 对齐（时间重叠比例 ≥50% 配对；未配对 cue 单语保留）→ 输出 `{base}.zh.en.srt|ass`（命名经 `CreateFilePath` 模板扩展），行序 zh 在上 en 在下（照 SrtWriter 双语行序既有约定）
- API：POST merge（media + 两个文件路径 + 主语言选择）
- `Lingarr.Client`：MediaDetailPage 字幕文件卡多选两行 → 「合并双语」动作（v1 仅 UI 直调，不做自动策略）

测试：完全对齐/部分错位/一空一满三组夹具；输出可被 SubtitleService 重新解析为双语。

### 验收标准
1. 两文件 100 条 cue 各错位 ≤200ms：合并输出 100 条双语 cue；错位 500ms 以上的不配对 cue 以单语保留。
2. 输出文件被 ReadSubtitles 解析出双语言。
3. CI 三绿。

**估时**：1 人日。

---

## 批次 B07：forced / hearing-impaired 语义贯通

**目标**：forced/hi 从「候选徽章」升级为覆盖与命名的正式维度（对标 bazarr，但裁剪到中文用户必要面）。

**前置**：无。

### 文件白名单

修改：
- `Lingarr.Core` 覆盖模型（`SubtitleCoverageMath`/`SubtitleCoverageRefresher`）— 带仅 forced caption 的文件计为「forced 覆盖」，与全量覆盖分维呈现（徽章去重照 9b0180f 裁决）；forced 覆盖**不**豁免全量搜索
- `Lingarr.Server/Modules/SubtitleSearch/Pipeline/SubtitleDownloadPipeline.cs` — `ComputeOutputPathAsync`：候选 `Forced=true`/`HearingImpaired=true` 时文件名追加 `.forced`/`.hi` 尾标（词汇已存在 `SubtitleService.SupportedCaptions`，NormalizeService 往返兼容需回归）
- `Lingarr.Server/Modules/SubtitleSearch/Scoring/SubtitleMatchScorer.cs` — 设置 `prefer_hearing_impaired`（默认 false）为 true 时同分候选 hi 优先（只动 tiebreak，不动分数）
- `Lingarr.Client` 两份 locale + 设置项（automation tab）+ 覆盖徽章

测试：forced 文件覆盖分维断言；命名尾标 + NormalizeService 往返；tiebreak 断言。

### 验收标准
1. 下载 forced 候选 → 落盘文件名含 `.forced` 且 NormalizeService 预览不改名。
2. prefer_hi=false（默认）：打分排序与现状一致（回归门）；=true：同分 hi 在前。
3. CI 三绿。

**估时**：1 人日。

---

## 批次 B08：每媒体目标语言集合

**目标**：目标语言从全局 `target_languages` 下放为可每媒体覆盖（电影级/剧集级，剧集继承到集），对标 bazarr language profiles 的必要子集。

**前置**：B07 同区域（覆盖模型），**串行**。

### 文件白名单

新增：
- 迁移 `M0015_PerMediaTargetLanguages.cs` — `movies.target_languages_override NULL`、`shows.target_languages_override NULL`（逗号分隔语言码，NULL=随全局）

修改：
- `Lingarr.Core/Entities/Movie.cs` + `Show.cs`（`Episode` 继承 Show，不加列）
- `SubtitleCoverageRefresher`/`SubtitleCoverageMath` — 目标集合解析顺序：媒体级覆盖 > 全局；空串视为显式「无目标」（等于该媒体不再要字幕，与 NoSubtitleNeeded 语义区分：前者可随时改回）
- `Queue/SubtitleSearchQueueService.cs` `EnqueueGapMediaAsync` — gap 判定用同一解析函数（抽公共 `TargetLanguageResolver`，消除两处各算一遍的漂移风险）
- `Lingarr.Client` MediaDetailPage `audio-languages-row` 区域 — 语言集合编辑器（复用既有 edit-audio-languages 交互形态）；两份 locale
- `Lingarr.Migrations.Tests` — schema 断言

### 验收标准
1. 电影 override=`zh`、全局=`zh,en`：该电影覆盖计算只认 zh，en 缺失不入 gap 队列（SQLite 断言）；未 override 的媒体行为与现状一致（回归门）。
2. Show override 对其全部 Episode 生效。
3. UI 修改后详情页覆盖徽章即时反映（SignalR 既有刷新链路）。
4. CI 三绿 + migrations 测试过。

**估时**：1.5 人日。

---

## 批次 B09：候选明细抽屉 + 字幕预览

**目标**：手动搜索候选行可展开看打分明细（哪项得分/失分）、可预览字幕内容。对标 bazarr matches/dont_matches + 预览 modal。

**前置**：无。

### 文件白名单

修改：
- `Lingarr.Server/Modules/SubtitleSearch/Scoring/SubtitleMatchScorer.cs` — `ComputeScore` 拆出信号明细（`record ScoreSignal(string Name, int Points, bool Hit)`；hash 短路时单信号）挂到 `SubtitleCandidate.Signals`（缓存于 SubtitleCandidateCache，随 10min 缓存同生命周期）
- `Lingarr.Server/Controllers/`（subtitle-search 控制器）— 候选 DTO 加 `signals`；新端点 POST `/api/subtitle-search/preview`：按 candidateRef 取缓存 ref → provider 下载字节（**不落盘不进历史**）→ 内存解压解析前 30 条 cue 返回文本+时间；单 ref 预览结果 LRU 60s 防连点
- `Lingarr.Client/src/cells/library/components/SubtitleSearchModal.vue` — 行展开明细（信号点数表，hit 绿点/miss 灰点，照 kernel BadgeComponent 词汇）；预览 modal（Teleport 模式照 MediaSourcesCard:115-150）
- `Lingarr.Client/src/types/services.ts` + 两份 locale

测试：信号明细断言（构造命中/未命中各维度）；预览端点「零落盘」断言（临时目录无新文件、subtitle_downloads 无新行）；modal spec（data-test）。

### 验收标准
1. 候选行展开显示全部信号与点数，总和 = matchScore（hash 短路时 = 100）。
2. 预览请求后：磁盘零新文件、DB 零新历史行、返回前 30 cue 文本。
3. vue-tsc + CI 三绿。

**估时**：1.5 人日。

---

## 批次 B10：字幕工具面板（对已落盘字幕的操作中心）

**目标**：MediaDetailPage 字幕文件行操作面板，聚合：重新翻译 / 双语重渲染（既有）/ 时间轴校正（B05）/ 繁简转换（B01）/ 手动偏移。对标 bazarr SubtitleToolsModal。

**前置**：**B01 + B05 + B06**（动作全部就位后聚合）。

### 文件白名单

新增：
- `Lingarr.Client/src/cells/library/components/SubtitleToolsModal.vue` + `.spec.ts` — 行级「工具」按钮打开；动作分派到既有/本任务书新增端点；进行中状态走 toast store；ass/srt 能力差异在动作上禁用置灰
- API 薄层：手动偏移端点（POST shift：±ms → SubtitleTimelineRewriter 复用）

修改：
- `MediaDetailPage.vue` subtitle-files-card 行动作区 + 两份 locale

### 验收标准
1. 五个动作各有一条 spec：请求契约、成功 toast、失败 toast（错误归一 ApiError 链路）。
2. 偏移动作：+500ms 后 cue 时间精确平移，样式层不变。
3. vue-tsc + CI 三绿。

**估时**：1.5 人日。

---

## 批次 B11：通知（webhook + Telegram）

**目标**：关键事件外发通知，替代 bazarr Apprise 的必要子集。明确不做 Apprise 全渠道。

**前置**：无（B03 先行更顺——Plex 卡的设置形态可复用，但无硬依赖）。

### 文件白名单

新增：
- `Lingarr.Server/Modules/Infrastructure/Notifications/` — `NotificationService.cs`（事件→渠道扇出，每渠道独立 try/catch 记 warning）、`WebhookChannel.cs`（POST JSON：`{event, mediaType, title, language, provider, result, timestamp}`）、`TelegramChannel.cs`（`POST https://api.telegram.org/bot{token}/sendMessage`，`chat_id`+`text`；token 走加密设置）
- `Lingarr.Client/src/cells/settings/components/NotificationsCard.vue` + `.spec.ts`（system tab）

修改：
- `SettingKeys.cs` + `StartupService.cs` — `notifications_enabled`(false)/`notify_webhook_url`/`notify_telegram_bot_token`(加密)/`notify_telegram_chat_id`/`notify_events`（逗号分隔：`downloaded,upgraded,translation_failed,job_failed`，默认前两个）
- 事件埋点：`SubtitleDownloadPipeline.RecordAsync` 成功/失败区、B02 升级成功点、`TranslationJob` 失败路径、`ScheduleService` job 异常路径（各一行调用，通知失败绝不影响主流程）
- 两份 locale + `types/setting.ts`

测试：渠道扇出（Moq HttpMessageHandler 工厂）、事件过滤、token 加密读写、通知异常不外抛。

### 验收标准
1. `downloaded` 事件：webhook 收到契约 JSON（字段全、时间戳 ISO8601）；Telegram 收到 sendMessage 请求体。
2. `notify_events` 不含的事件零外发；渠道失败仅 warning。
3. 设置卡 spec + CI 三绿。

**估时**：1 人日。

---

## 批次 B12：失败降频（长尾补搜窗口）

**目标**：放弃规则从「attempt≥8 即 Skipped」改为「早期密集、长尾稀疏、90 天后放弃」，对标 bazarr adaptive_searching 与 CSF 90 天窗口的并集语义。

**前置**：无。

### 文件白名单

修改：
- `Lingarr.Server/Modules/SubtitleSearch/Queue/SubtitleSearchQueueService.cs` — `MarkFailedAsync`(:292) 的 backoff 曲线改为：attempt 1-8 维持现状（6h×2^n 封顶 7d）；attempt 9-12 间隔固定 14d；attempt >12 → Skipped。`MaxAttempts`(:48) 与新间隔从编译期常量改为 settings（`subtitle_search_max_attempts` 默认 12、`subtitle_search_long_tail_days` 默认 90——用于 UI 文案与统计口径，第 12 次尝试距今超过该值即 Skipped 的兜底判断）
- `SettingKeys.cs` + `StartupService.cs` + 迁移种子（并入 M0014 亦可）
- queue 页 Skipped 行文案与统计（两份 locale）；pin 复活既有行为回归断言

### 已定决策
- 常量→settings 只做这两个键，不开放 backoff 全参数化（配置面爆炸无收益）。
- Skipped 后的复活路径唯一：用户 pin（既有），或新字幕 webhook boost（既有）。

### 验收标准
1. attempt=8 失败 → next_attempt=+7d 且 status 仍 Pending（现状是 Skipped，行为变更点）；attempt=13 → Skipped。
2. `subtitle_search_max_attempts=8` 时行为与现状逐字段一致（回归门，兼容想保持旧语义的用户）。
3. pin 复活 Skipped 行为不回归；CI 三绿。

**估时**：0.5 人日。

---

## 泳道与依赖矩阵

| 泳道 | 批次串 | 估时 |
|---|---|---|
| A（后处理线） | B01 → B05 → B10 | 6.5 人日 |
| B（升级线） | B02 | 2 人日 |
| C（集成线） | B03 → B11 | 1.5 人日 |
| D（内封线） | B04 | 1.5 人日 |
| E（语言语义线） | B07 → B08 | 2.5 人日 |
| F（搜索 UX 线） | B09 | 1.5 人日 |
| G（合并线） | B06（并入 A 的 B10 前完成即可） | 1 人日 |
| H（队列线） | B12 | 0.5 人日 |

- 同文件硬约束：`SubtitleDownloadPipeline.cs` 被 B01/B02/B03/B07 触碰 → 这四批互斥串行（建议 B01→B02→B03→B07 全局序）；其余泳道可并行派发。
- 覆盖模型 `SubtitleCoverageMath/Refresher` 被 B04/B07/B08 触碰 → B04 可与 B07 并行（改不同分支），B08 必须 B07 之后。
- 合计 ≈ **17 人日**（Whisper 兜底 +1 可选）。

## 退役验收（bazarr + CSF 退役条件）

1. B01/B02/B03/B05 上 NAS 后连续两周：升级换优有实际成交、Plex 刷新无感知延迟、时间轴校正 soak 期零误伤（.lingarr-tl-bk 还原率 <2%）。
2. B04 内封覆盖语义按用户选择开启后，原 CSF 承担的「内封中字媒体不再重复补搜」成立。
3. 存量字幕：CSF `.csf-bk` 还原跑一轮（它改过时间的字幕恢复原样再交给 lingarr 校正体系）；繁简/编码存量批处理按需跑过。
4. 手动搜索可用度自评不低于 bazarr（分数明细+预览+B09 齐备）。
5. 停用顺序：先停 CSF（其源 lingarr 已全接）观察一周 → 再停 bazarr → NAS stack 清理。

## 实施陷阱清单（历轮实锤，批次验收前自查）

- 审计门 `check-logging.sh --gate` 是 ratchet：Log 模板不得内插 `{ex.Message}`、不得出现 `apikey=` 字面量与 `|Green|` 色标——**注释和文档字符串里写这些字面样例同样会被计数**。
- CI 是第一道编译门：新文件的 `using` 缺失先红，先本地（或容器）编译再派测试。
- 取消令牌契约：job 内取消绝不写 Failed/Skipped 状态，unwind 后 rethrow `OperationCanceledException`（SubtitleSearchJobTests:410-450 是判例）。
- 枚举只加不改值：`Source` 加 `Upgrade=3`，不动 0-2；JSON 合同 PascalCase 逐字对齐。
- FluentMigrator 手写迁移 + snake_case；测试里 raw SQL 用迁移列名（HookDbContext + `UseSnakeCaseNamingConvention` 判例）。
- 搜索候选缓存 10min 过期：B09 的 preview 端点测试须新造 ref，不复用跨测试缓存。
- 前端：Vue 3 + cell 架构；两份 locale（en/zh-CN）同步缺一 CI 不红但违反约定；选择器一律 `data-test`；Tailwind 类名含点是非法 CSS 选择器；禁静态装饰文案。
- vitest 不在 CI 门禁内，但每个新组件/卡按惯例配同名 `.spec.ts`；`vue-tsc --noEmit` 是硬门。
- 新依赖双登记：`Directory.Packages.props`（CPM）+ `THIRD-PARTY-NOTICES.md`（B05 的 Math.NET 必须两处齐）。
- 部署链：推 main → CI 三绿 → NAS Portainer Stacks Update 勾 Re-pull；服务端改动 HMR 不生效必须重建容器；无 SSH 排障用浏览器 fetch（带 credentials）+ SSE 日志流。
- 10w 级红线：所有新 job 必须有 batch limit 与断点续跑（EmbeddedTrackScanJob 的 marker 模式是判例），首轮全量禁止无界扫描。
