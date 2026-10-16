# PRD 增补批次 — 音轨感知 · 库列表运营化 · 详情可达性

日期：2026-10-15｜状态：裁决完成待排期｜上游：[字幕搜索 PRD v1.0](subtitle-search-prd.zh-CN.md)、[10w 审计](scale-audit-100k.zh-CN.md)（S4 合流）
取证基线：radarr/sonarr 源码只读核实 + lingarr 实体现状（`RadarrMovie.cs` DTO 仅 7 字段、`RadarrMovieFile` 仅映射 path、`LibraryPage.vue` 6 列、路由 `/library/{show|movie}/:id` 已存在）。

## 交付现状（2026-10-16 对照 cc656db）

- **已交付**：卡 A 全量（音轨列+旗标，M0007；native/audio-unknown 过滤项与徽章；手动标记 `audio_override`）；卡 B 第一批第 1/2 项（过滤项+原生中文/音轨未知徽章）；卡 C 标题链接化（`4ea4f29`）。
- **未交付/裁剪**：B2 五列 **DB 已写**（M0008：movies.quality/size_bytes/imdb_rating+year/imdb_id、shows 同批）但 **UI 未上屏**（无排期）；B1 第 3/4/5 项（入库日期 tooltip/内嵌轨数 tooltip/覆盖率%+全语言明细）裁剪未做；AC2 缺口数四处一致中两处未扣豁免（详情页缺集数/概览管线节点，PM 裁顺延至字幕搜索 M6 前端批）；AC11 返回仅 type 段过滤恢复；AC13 滚动位置不保持。
- 正文就地标注 `> 现状（2026-10-16）：…`，原文保留供考古。

## 需求卡 A：音轨语言感知与「无需字幕」覆盖态

**背景**：生产库含大量华语内容（国产片、双语 BDRip），因只统计字幕轨被误判"缺中文字幕"——每日补缺空转、状态列全显缺。这是**缺口统计正确性问题，P0 级**。

**裁决**：可做。落点不是"排除出产品"，而是引入第三种覆盖态——**「无需字幕」（native）**，与「已有字幕」「缺字幕」并列，由各消费方自行决定排除范围。

**数据来源（tech-lead 已核实，零额外请求）**：
- Radarr `MovieFileResource.Languages`（`List<Language>`，`[{id,name}]`）+ `MediaInfo.AudioLanguages`（斜杠拼接字符串，如 `"english / chi"`，来自 ffprobe 真值，空 tag 已过滤）；`/api/v3/movie` 列表载荷内嵌完整 movieFile。Sonarr `EpisodeFileResource` 同构（Lingarr 每集已单独 GET，同载荷可用）。
- **可信度顺序裁决：`mediaInfo.audioLanguages`（真值）> `languages`（聚合猜测值，无法识别时会回退 OriginalLanguage，勿单独依赖）> 本地 ffprobe**。
- ffprobe 兜底搭车：现命令带 `-select_streams s` 在进程层丢弃音频流（`EmbeddedTrackService.cs:808`）——去掉该参数并让 `ParseFFprobeOutput` 按 `codec_type` 分流即可；**每文件仍一次 ffprobe，首次全量零额外成本**，scan_key=path|mtime 增量对音轨自动适用。
- **落库裁决**：`movies.audio_languages`/`episodes.audio_languages` JSON 列（规范化代码数组 `["zh","en"]`，复用 SubtitleLanguageParser+ContainerLanguageFolding 折叠 chi/zho→zh），**并入 M0006 一次迁移，不建新表不复用 MediaSubtitleTrack**。

> 现状（2026-10-16）：🔧「并入 M0006 不拆」未按裁决执行——M0006 槽位被审计 S1 性能索引占用；音轨列实际落 **M0007_AudioAndLanguageCache**（同批建 `audio_override`/`needs_translation`/`no_subtitle_needed` 旗标列与 `subtitle_language_cache` 嗅探缓存表，故也非「不建新表」）；quality/size_bytes/imdb_rating/year/imdb_id 落 **M0008**。

**语义**：
- 排除（SQL 层统一，S4 求差落库时合流）：缺口统计（`audio_langs ∩ {target} ≠ ∅ → 豁免`）、自动补缺队列选题、翻译求差队列、概览"缺字幕"计数。
- 不排除：详情页手动搜索/导入/翻译——弹层一次性提示"检测到中文音轨，通常无需字幕"，可继续。
- 判定：目标语言命中**任一**音轨即豁免（英+中双语原盘宽松豁免，零误伤成本）。
- 边界：①载荷为空/und → ffprobe 兜底；仍 und → 记 `unknown` **不豁免**（保守），详情页示"音轨语言未知"，状态过滤单列"音轨未知"；②手动标记 > ffprobe > *arr 标签（来源随值落库并展示），详情页一键"标记需要字幕"可逆翻转、下轮选题生效；③豁免透明：概览 KPI 单列"无需字幕 N 项"，绝不静默吞。

**AC**：AC1 含 zh 音轨且无 zh 字幕的媒体状态列显「无需字幕」态；AC2 全库缺口数=总量−已覆盖−豁免−unknown，四处（概览/列表/详情/历史）一致；AC3 自动补缺选题集不含豁免项、手动弹层提示但可执行；AC4 音轨语言独立存储不动字幕轨表语义；AC5 手动标记立即翻转并优先进下轮选题；AC6 unknown 不入"无需字幕"过滤、单列"音轨未知"。

> 现状（2026-10-16）：AC1 ✅（形态为 **chips 徽章+过滤项**而非状态列独立态——设计等价，QA 附记已自述）。AC2 ⚠️四处中**两处未扣豁免**——列表/统计扫描侧已扣，详情页缺集数与概览管线"缺字幕"节点未扣（PM 裁顺延至字幕搜索 M6 前端批一并处理，届时 KPI 同口径；见文末 QA 附记 P1-2）。AC3 ✅前半（选题 SQL 排除 `no_subtitle_needed=1`）；后半（弹层"检测到中文音轨"一次性提示）未做——弹层无豁免提示，手动搜索/下载不受阻（与「不排除」裁决一致，仅缺提示文案）。AC4 ✅（`audio_languages` 独立 JSON 列）。AC5 ✅（`audio_override` 手动标记，SubtitleNeedRule override 优先，下轮选题生效）。AC6 ✅（`audio-unknown` 单列过滤+「?」弱标记）。

**估时**：2.5d（DTO 扩展+列+同步映射+ffprobe 分流+透出+三处单测）。**前置依赖：审计 P0-6（内嵌扫描首次 6-14h/互斥失败）修复必须先行**，否则音轨搭车会被拖进同一黑洞。

## 需求卡 B：库列表信息增补

**定位**：10w 级库唯一可行的批量作业面（运营中心）——"看一眼知道哪些要处理、先处理哪个"。分两批，≤8 项。

**第一批（零后端/依赖卡 A）**：
| # | 内容 | 数据来源 | 位置 |
|---|---|---|---|
| 1 | 状态过滤增「无需字幕」「音轨未知」 | 卡 A | toolbar 状态下拉 |
| 2 | 音轨语言标识（`原生中文` 弱标记） | 卡 A | chips 区/状态列 |
| 3 | 入库日期 | 实体已有 DateAdded（口径：电影=movie.added） | "最近活动"悬停 tooltip |
| 4 | 内嵌轨数 | media_subtitle_tracks COUNT（索引已建 M0004:33） | chips tooltip |
| 5 | 覆盖率%+全语言明细 | SubtitleCoverage 快照现算 | chips tooltip |

> 现状（2026-10-16）：第 1/2 项 ✅（toolbar 状态下拉增「无需字幕」「音轨未知」；chips 区「原生中文」弱标记+「? 音轨未知」标记，QA 附记 P1-3 已修过滤/徽章对称性）；**第 3/4/5 项裁剪未做**——入库日期 tooltip、内嵌轨数 tooltip、覆盖率%+全语言明细 tooltip 均未上屏（列表列维持 标题/轨/状态/最近活动 布局；「轨」列仅字幕轨语言 chips，无日期/覆盖率 tooltip 挂载）。

**第二批（扩 *arr DTO，载荷免费，搭 M0006/AC12 班车不单独发版）**：
| # | 内容 | 数据来源 | 位置 |
|---|---|---|---|
| 6 | 年份 + IMDB 评分（含链接） | year/imdb_id（M0006 已计划）+ ratings.imdb（需扩 DTO） | 标题 meta 行 |
| 7 | 规格：质量·分辨率·大小 | movieFile.quality + mediaInfo.resolution + size/sizeOnDisk | meta 行 tooltip（1920 可见/手机收起） |
| 8 | monitored（可选） | payload 免费携带 | tooltip |

**明确不建议加**：海报缩略图（密度/流量，详情页已有）、演员/简介/体裁（与处理决策无关，装饰性）、行级成本预估（无翻译前不可算→静态装饰，违反禁令）、多列排序（10w 需索引建设，首期不做）、图形进度条（chips 已编码同等信息）。

**AC**：AC7 新字段真实可算零静态占位；AC8 1920 六列布局不变（信息进 meta/tooltip）；AC9 二批字段缺失的存量媒体显空非假值；AC10 过滤项与状态列态一一对应。**估时 2.5d**。

> 现状（2026-10-16）：B2 **DB 侧已写**——M0008 落 `movies.quality`/`movies.size_bytes`/`movies.imdb_rating`+`movies.year`/`movies.imdb_id`、`shows.year`/`shows.imdb_id`/`shows.imdb_rating`，同步写路径（MovieSync/EpisodeSync/SeriesResource 补抓）同批改完；**UI 未上屏**（标题 meta 行/tooltip 均未加，年份+IMDB 评分链接、规格 tooltip、monitored 无呈现面），排期待定。**monitored 裁定跳过**（未建列，M0008 注释在案）。AC8 ✅（布局未动）；AC10 ✅（过滤项↔徽章一一对应）；AC7/AC9 的 UI 面随 B2 上屏后生效。

## 需求卡 C：标题点击进入详情

**裁决**：做。**标题文本链接化，与「详情」按钮并存**（批量勾选时手在行尾，删按钮无收益）；路由已存在（`router/index.ts:69-78`），改动=标题 `<b>` 包链接 + cursor/hover/focus 样式 + `aria-label`，勾选框独立 td 无冲突（**禁止整行点击**——误触勾选）。键盘 Tab/Enter 天然可达；右键新标签页可用。**估时 0.5d，可先行合入**。
**AC**：AC11 标题点击/Enter/新标签均达详情且返回不丢列表过滤；AC12 勾选框点击不触发导航；AC13 往返后滚动位置保持。

> 现状（2026-10-16）：AC11 🔧——标题链接化 ✅（真实 `<a>` 锚点+router.resolve href，修饰键点击/新标签可用，`4ea4f29`），但「返回不丢列表过滤」**仅 type 段恢复**（type 写入路由 query，返回时读回；搜索词/状态过滤不持久化，返回即丢）；AC12 ✅（勾选框独立 td 不触发导航）；AC13 ❌——无 keep-alive/滚动恢复机制，往返后滚动位置不保持。

## 协同增强（3 条）

1. **音轨列并入 S4 语言清单表合流施工**（一次施工两用，审计已预留省 2 人日）。
2. **「无需字幕」贯通概览管线"缺字幕检测"节点与 KPI**——缺口数=真缺口+豁免透明，否则概览与列表数字打架。
3. **批量托盘确认提示**："翻译所选"含豁免项时确认"N 项检测为中文音轨，仍翻译？"——与卡 A 同批交付。

## 排期与迁移

| 卡 | 估时 | 时点 |
|---|---|---|
| C 标题进详情 | 0.5d | 随时可做（独立小卡） |
| A 音轨感知 | 2.5d | 并入 S4 批次（S1→S2→S3 之后、字幕搜索 M1 之前）；P0-6 修复为前置 |
| B 第一批 | 随 A | 同批上线 |
| B 第二批 | 2.5d | 搭 M0006/AC12 同步补齐班车 |

**迁移裁决（tech-lead）**：`audio_languages`/`quality`/`size`/`imdb_rating` 列与 M0006 已计划的 year/imdb_id 同属"同步载荷落库"，**一次合入 M0006 不拆**（写路径相同、Down 同生共死，拆分只多一份计数门禁成本无灰度收益）；M0007 遥测维持独立。

> 现状（2026-10-16）：🔧裁决未兑现——M0006 槽位被 10w 审计 S1 性能索引占用；列组分两批落地：**M0007**（audio_languages/audio_override/needs_translation/no_subtitle_needed+subtitle_language_cache）与 **M0008**（quality/size_bytes/imdb_rating/year/imdb_id+字幕搜索业务表）；原「M0007 遥测」实为 **M0009**。写路径仍同处改完（MovieSync/EpisodeSync），Down 各自独立可回滚——裁决担心的双份计数门禁成本实际发生。

## QA 验收附记（2026-10-15，S4 落地时）

- **P1-1 首扫过渡态**：已修——新增持久化 `no_subtitle_needed` 标志（M0007 列，默认 0=不豁免），由统计扫描与 subtitle-need 端点按 `SubtitleNeedRule.NoSubtitleNeeded`（override 优先，已知音轨含任一目标语言才豁免，unknown/零目标永不豁免）单一规则维护；native 过滤与「原生中文」徽章均读该标志，首扫前全库不误标。
- **P1-3 AC10 断裂**：已修——列表 chips 增「? 音轨未知」弱标记（`audioLanguages === null` 时渲染，与 audio-unknown 过滤一一对应）。
- **P2-1 误导注释**：已修——同步侧空数组折回 null 的承诺由写入方守卫实现，注释改为描述现状。
- **P2-2 UI 不对称**（已知音轨不含目标的缺口媒体无「无需字幕」入口）：维持 PM 表——该态无需豁免入口，属有意设计。
- **P1-2 顺延**：AC2"缺口数四处一致"中的详情页缺集数/概览管线节点扣减豁免项，顺延至字幕搜索 M6 前端批次一并处理（届时 KPI 亦需同口径）。
- **P3 顺延**：StatisticsJob 逐 id ExecuteUpdate 批量化，并入字幕搜索 M5 性能批次。
