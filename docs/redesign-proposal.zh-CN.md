# Lingarr 产品重构设计提案（P3 · UX 与产品层）

> 日期：2026-10-12 · 基线：`feat/visual-refresh`（P1 代码重构已完成，P2 代码债清单见 `module-analysis.zh-CN.md`）
> 配套原型：`design-previews/refactor-prototype.html`（六屏可点击，遵循 `DESIGN.md` Timeline 体系）
> 竞品信息联网查证于 2026-10-12（来源见文末）

---

## 一、竞品调研：谁在做相似的事

### 1.1 自托管 / 开发者向（直接相邻）

| 项目 | 定位 | 与 Lingarr 的关系 | 可借鉴 |
|---|---|---|---|
| **Bazarr** | *arr 配套字幕**下载**管理器 | 最近邻。同管 Sonarr/Radarr 集成，但核心是"找字幕"不是"翻译字幕"（导入功能参考已弃用，见 §3.6.1） | 媒体库与字幕状态矩阵的呈现方式 |
| **ChineseSubFinder**（3.9k★，Go，2025-01 停更） | 中文场景字幕自动下载（zimuku/subhd 等源）+ Emby/Jellyfin 联动 | 中文自托管场景最贴近的参考；**导入链路主参考**，本地源码分析见 §3.6.1 | 编码转码、简繁转换、`第x集` 解析、防覆盖语义、`.default/.forced` 清理 |
| **Subgen** | Whisper 语音**生成**字幕，对接 Jellyfin/Plex/Emby/Bazarr webhook | 互补而非竞争（生成 vs 翻译）。证明"媒体服务器 webhook"是真实需求 | 媒体服务器集成面比 Lingarr 宽（*arr 之外还有 Jellyfin/Plex/Emby） |
| **subtitle-translator**（rockbenben，1.2k★） | 纯浏览器批量翻译，.srt/.ass/.vtt/.lrc，27 个 LLM 端点，整季一次翻完，双语输出 | 无媒体库集成，但**翻得多、翻得快** | 上下文感知翻译、双语输出、行级结果流、本地缓存复用 |
| **GPT-Subtrans / llm-subtrans** | LLM 字幕翻译，项目制，大上下文窗口 | Bazarr 用户常推荐的翻译补充 | 上下文窗口策略（整块对话送翻，非逐行） |
| **TranslateBooksWithLLMs**（2.4k★） | 书籍/EPUB/SRT 翻译，智能分块+**断点续传**，桌面/Web/CLI 三形态 | 格式与形态参考 | 断点续传已对齐；"CLI 同引擎"值得跟进（Lingarr 已有 HTTP API） |
| **pyvideotrans** | 视频翻译+配音，SRT 批量，**术语表**，双语输出，全文字幕上下文 | 功能面最杂的邻居 | 术语表（人名/剧名一致性）；"发全量字幕给 AI"选项 |
| **Subtitle Edit** | 桌面字幕编辑器（300+ 格式，本地 LLM 翻译） | 质量工序的天花板 | 字幕格式覆盖（VTT/ASS）；人工精校的编辑体验 |

### 1.2 SaaS（仅作 UX 参照）

Maestra、Happy Scribe、VEED、Kapwing、Rask 等：云端、创作者向、订阅制。共同点是都有**浏览器内字幕编辑器**和"上传→翻译→精校→导出"的线性流程。它们不碰 *arr 生态，不构成直接竞争，但"翻译之后还有一个精校环节"是全行业的共识——这正是 Lingarr 校对台的价值背书。

### 1.3 结论：空白点在哪

没有任何自托管工具同时具备这四件事：

1. **媒体库自动化**（*arr 监听 + 定时批量）——Lingarr 已有
2. **多翻译服务 + 插件 ABI**——Lingarr 已有（10 内置 + 插件）
3. **人工校对工作台**——Lingarr 有雏形（Proofread API + 详情页内嵌状态机），但没有像样的界面
4. **用量/成本核算**——LLM provider 下没人做，而 Lingarr 的任务表 + SignalR 实时推送恰好有数据基础

**定位主张：从"翻译执行器"升级为"字幕本地化流水线"——自动化管产量，校对台管质量。**

---

## 二、信息架构（IA）重构

### 2.1 现状问题（对应当前 21 条路由）

| 问题 | 现状 | 证据 |
|---|---|---|
| Shows 与 Movies 是 90% 双胞胎 | 两个一级导航、两套 store、两个 250 LOC 页面 | `MoviePage`/`ShowPage` diff 几乎全是改名 |
| Dashboard 是统计页不是工作台 | 统计卡 + roadmap，回答"历史如何"，不回答"现在怎么样" | `DashboardPage` 只包一个 `StatisticsComponent` |
| 校对没有自己的家 | 校对状态机内嵌在 519 行的 `TranslationDetailPage` | `module-analysis` §2.3 |
| Settings 11 个子页平铺 | 无分组，新用户不知道从哪开始 | 路由表 `settings/*` |
| "Translations" 命名含混 | 既是任务历史又是队列 | `TranslationPage` + `TranslationRequestService` |

### 2.2 新 IA：六个一级目的地

```
主导航
├─ 概览  Overview      运营中枢：管线视图 + 活跃任务 + 成本 + 服务健康
├─ 媒体库 Library       Shows + Movies 合并，type 过滤；字幕轨状态矩阵
├─ 任务队列 Queue       原 Translations；timeline 式队列，实时进度
├─ 导入  Import        自由导入 + 定向导入（单部/整季）；转格式/转编码/去重
├─ 校对台 Proofread     从详情页独立成一级页；双轨对照精校
└─ 设置  Settings       11 子页 → 4 组：翻译服务 / 媒体与自动化 / 字幕与质量 / 系统
```

路由映射（旧 → 新，保留重定向）：

| 旧 | 新 |
|---|---|
| `/shows`、`/movies` | `/library?type=show\|movie` |
| `/translations` | `/queue` |
| `/translations/:id` | `/queue/:id`（查看）+ `/proofread/:id`（精校） |
| —（新增） | `/import`（自由）· `/import?mediaId=&season=`（定向，预绑定条目） |
| `/dashboard` | `/`（概览，内容重做） |
| `/settings/services`…11 项 | `/settings/providers` 等四组内锚点 |

**收益**：主导航 5→6 项但语义各归其位；删一对双胞胎页面；校对与导入升为一级公民；设置可巡览。

### 2.3 Settings 分组

| 组 | 收纳（现有子页） |
|---|---|
| 翻译服务 Providers | services + request-template + plugins |
| 媒体与自动化 Sources | integration（*arr）+ automation + mapping |
| 字幕与质量 Quality | subtitle + 校对相关（新增术语表入口） |
| 系统 System | authentication + logs + telemetry + tasks(schedule) |

---

## 三、关键界面设计（页面级决策，视觉基座沿用 `DESIGN.md`）

原型六屏对应以下决策，全部使用既有 token（`--accent` #E8833A、`--track-a/b`、`--line` 等），不引入新色。

### 3.1 概览 = 管线，不是统计卡
首屏回答"现在发生了什么"：一条横向管线（媒体库 → 缺字幕检测 → 翻译队列 → 翻译服务 → 本月写回），各节点实时计数（数据源全部现成：Hangfire 队列、SignalR、StatisticsService）。KPI 条只留四个 mono 数字：进行中 / 排队 / 本月成本 / 30 天成功率。下方"活跃任务"轨道条 + "服务健康"一行状态点。Roadmap 从概览移除（营销内容不该占运营中枢首屏）。

### 3.2 媒体库 = 字幕状态矩阵
合并后的列表每行：标题/年份/类型徽章 + **语言轨 chips**（`EN ✓` `ZH ◐` mono 徽章，状态用 success/accent/line 三色）。批量操作固定为吸底托盘（选中才出现）。**行级导入入口**：剧集行「整季导入」、电影行「导入字幕」，进入预绑定该条目的定向导入（§3.6.2）。这一个矩阵同时回答"哪些缺字幕"和"哪些翻了一半"，替代现在需要进详情才能看到的信息。

### 3.3 任务队列 = 每任务一条轨道
沿用签名组件"轨道条 + 播放头"：每行一条 track bar，accent 分段填充 + 2px 播放头，配 mono ETA。失败行内联"重试"，取消带确认。筛选 tab：全部/进行中/排队/失败/已完成。

### 3.4 校对台 = 双轨对照（本方案的重心，差异化主战场）
- 左轨源语言（`--track-a` 蓝），右轨译文（`--track-b` 橙），行与行水平对齐，时间码 gutter 用 JetBrains Mono `tabular-nums`
- 行聚焦即进入编辑；改动行打 dirty 标记；支持逐行"通过 ✓ / 驳回 ✗"
- 键盘流：`J/K` 上下行、`Enter` 通过、`E` 编辑——校对是重复劳动，手不离键盘是效率关键
- 顶部进度（已校对 128/512）+ 底部"完成校对并写回"
- 数据与逻辑已有：ProofreadRequestService + AsyncTranslationRegistry + 现有校对状态机，只是把它从详情页解放出来

### 3.5 设置·翻译服务 = provider 卡片
每个服务一张卡：状态点、模型、本月用量（mono）、最近一次请求延迟；操作：测试连接 / 请求模版 / 编辑。替代现在的长表单滚动页。卡片化后“多 provider 并存 + 切换默认”的心智模型自然成立。

### 3.6 导入与转换台（新增特性）

**定位：*arr 之外的第二入口。** 现在字幕只能从 Sonarr/Radarr 媒体条目产生，手动字幕文件（外挂下载、他人分享、老库存量）完全进不了管线。导入台同时解决三件事：入管、转格式、转编码。

#### 3.6.1 ChineseSubFinder 参考实现分析（本地 shallow clone：`PiSpace/ChineseSubFinder`，3.9k★，Go）

> Bazarr 的逐集上传模型参考价值有限，已弃用；ChineseSubFinder 的导入链路更贴近中文自托管场景，作为主要参考。

**导入 API（`internal/backend/controllers/v1/subtitles.go`，异步队列模式）**

| 端点 | 作用 |
|---|---|
| `POST /subtitles/manual_upload_2_local` | `video_f_path`（服务器端视频路径）+ `file`；字幕落缓存目录后入内存队列 |
| `GET /subtitles/list_manual_upload_2_local_job` | 队列列表 |
| `POST /subtitles/is_manual_upload_2_local_in_queue` | 按 VideoFPath 去重查询 |
| `POST /subtitles/manual_upload_result` | 按 VideoFPath 轮询结果（ok / 空 / 错误信息） |
| `POST /subtitles/refresh_media_server_sub_list` | 手动触发 Emby 字幕刷新 |

**处理管线（`pkg/manual_upload_sub_2_local/processor.go` → `save_sub_helper`）**

1. 导入前**清除该视频已有字幕的 `.default/.forced` 标记**（让新字幕成为默认轨）
2. SubParserHub 识别格式（真解析仅 ASS/SRT）
3. 按命名 formatter 保存到视频同目录：Emby 格式 `xxx.chinese(简英,来源).default.ass` / 常规格式 `xxx.zh.ass`；中文变体（chs/cht/简英/简日）归一化在 `pkg/language`（含 whatlanggo 内容级判简繁）
4. **自动把该视频标记为“跳过自动下载”**，防止自动下载覆盖手动成果
5. 保存时可挂：**编码转码**（`change_file_encode`→UTF-8）、**简繁转换**（`chs_cht_changer`）；自动下载链路另带时间轴校正（`sub_timeline_fixer`）

**整季多文件（`frontend/src/pages/library/tvs/BtnUploadMultipleForTv.vue`）**

拖拽多文件（.srt/.ass/.ssa/.sbv/.webvtt 白名单）→ 前端本地正则解析集号：`s\\d+e(\\d+)` 与 **`第(\\d+)集`**（支持中文命名）→ 每行自动预选“第 N 集”，未匹配置空由用户下拉手选 → 逐文件上传 + 轮询队列；未匹配不阻断。

**局限 → Lingarr 对策**

| 局限 | 对策 |
|---|---|
| 队列/结果纯内存，重启即丢 | Hangfire 持久化（现成基建） |
| 导入＝保存文件：无内容去重、无翻译衔接 | 导入即任务（Source=import），哈希去重 + 可直进翻译队列 |
| 真解析仅 ASS/SRT，其余格式存得了解析不了 | VTT 并入 P3-f；解析失败明确拒收不静默 |
| 上传即处理，无预览确认 | 映射预览 + 转换选项前置（见原型） |
| 时间轴校正只在自动下载链路 | 校对台预留偏移检测（P3-c） |

**采纳清单**：锚定物理路径的自由导入、`.default/.forced` 清理、“导入后不自动覆盖”语义（Lingarr 对应＝导入不自动排队翻译）、编码转码＋简繁转换进保存管线、`第x集` 中文集名解析、多语言变体归一化。

#### 3.6.2 媒体库导入入口与整季导入（season pack）

三类入口（对应原型）：

1. **工具栏「导入字幕」**（自由导入）：进入导入台，文件暂存导入库，不预绑定条目
2. **剧集行「整季导入」**（定向 · 整季）：预绑定 `Show + Season`，拖入整季字幕包，文件名解析自动映射到各集：
3. **电影行「导入字幕」**（定向 · 单部）：预绑定单个电影；无集号解析，映射表退化为单行确认

```
选剧 → 选季 → 拖入 N 个文件
  → 文件名解析（SxxExx / E01 / 1x02 / 第x集；无集号回退标题匹配）
  → 映射预览表：文件 → 集（置信度 ✓ 精确 / ✗ 未匹配可手改下拉）
  → 每行动作：入库＋翻译（默认）｜仅入库；导入选项：转码 UTF-8 / 简繁 / 双语
  → 批量确认 → 去重过滤（内容哈希）→ 入管线
  → 完成后写回原目录；不自动排队翻译，提示可排队（防覆盖语义）
```

电影定向导入复用同一预览表，仅去掉集号解析与归组。

#### 3.6.3 自由导入流程

```
拖拽上传 / 服务器路径扫描（Docker 场景路径比浏览器上传更常用）
  → 解析识别：格式 + 行数 + 编码探测 + 与既有任务去重（内容哈希）
  → 导入选项：转码 UTF-8（默认开）· 简繁（简/繁/原样）· 双语输出
  → 每文件选动作：仅转换 ｜ 转换＋翻译（直接进队列）
  → 批处理 → 写回：路径扫描文件→原目录，上传文件→导入库目录 ｜ 下载
```

#### 3.6.4 后端落点（均为小刀）

- **上传端点**：`SubtitleController` 加 `POST import`（IFormFile 批量，自由导入）+ `POST import/{mediaId}`（定向单部）+ `POST import/{mediaId}/season/{season}`（定向整季）；路径扫描复用现有 `DirectoryService`
- **文件名解析端点**：`POST /subtitles/parse-filenames`（自写 `SxxExx / E01 / 1x02 / 第x集` 正则；CSF 把这类解析放在前端且仅两条规则，Lingarr 收敛为后端端点并补齐变体；不引 guessit）
- **统一行模型已存在**（SrtParser/SsaParser → Writer），转换 = Parser→Writer 重编解码；双语合成复用 `SubtitleConversionService`
- **编码探测是关键新增**：UTF-8/GBK/Big5/Latin-1 自动识别，统一转出 UTF-8（对标 CSF `change_file_encode`）；简繁转换（对标 `chs_cht_changer`）作为导入选项——中文自托管用户最痛的两点
- **防覆盖语义（学 CSF）**：导入完成的字幕默认不触发自动化翻译批次；导入前清理同视频既有字幕的 `.default/.forced` 标记
- **数据模型**：不新建实体族。`TranslationRequest` 加 `Source = library|import` + 原始文件路径；导入即任务，避免与 P2 #18（静态状态入库）方向冲突
- VTT 导入/导出依赖 P3-f 的 `VttParser`，同批交付

---

## 四、功能演进建议（P3，按性价比排序）

> P2（#17–24）是代码债清理，与本节不冲突；P3 是产品特性，多数可搭在 P1 重构后的骨架上。

| 优先 | 特性 | 竞品依据 | 落点 |
|---|---|---|---|
| P3-a | **术语表 Glossary**（人名/剧名/专有名词，按媒体条目或全局） | pyvideotrans、专业翻译工具标配 | 数据模型一张表；LLM provider 注入 system prompt；校对台联动（术语命中高亮） |
| P3-b | **用量与成本面板** | 全行业空白（自托管领域） | 任务表加 token/字符计数；概览 KPI + 设置页 provider 卡片汇总 |
| P3-c | **校对台产品化**（本提案 3.4） | SaaS 全行业共识环节 | 现有 Proofread API + 状态机前移 |
| P3-d | 上下文感知批量（整块送翻，非逐行） | subtitle-translator、GPT-Subtrans | 已有 batch 基建，调 prompt 组装策略即可 |
| P3-e | Jellyfin/Emby/Plex webhook | Subgen 证明需求 | Integration 层抽象成 MediaSource provider（Radarr/Sonarr 已是两个实现，抽象是顺势） |
| P3-f | 字幕格式扩展（VTT，其次 ASS 输出增强） | Subtitle Edit 300+ 格式的长尾压力 | SrtParser/SsaParser 骨架加 VttParser |
| P3-g | **导入与转换台**（自由导入 + 整季定向 + 编码转码 + 简繁 + 去重） | ChineseSubFinder 全链路参考（§3.6.1）；pyvideotrans 的编码处理佐证需求 | 见 §3.6；`SubtitleController` 加上传端点 + 编码探测 + 简繁转换 |
| 不做 | STT 语音生成字幕（Whisper） | Subgen 已占位 | 偏离核心；未来可用插件 ABI 接入而非内置 |

---

## 五、交付物与验证路径

1. **本提案**（IA + 界面决策 + 路线）
2. **原型**：`design-previews/refactor-prototype.html`——自包含单文件，六屏可在侧栏间切换（快捷键 1–6；hash 深链如 `#import/season` 直达整季导入），内容为真实感样例数据，跨屏数字已对齐（成本口径、任务时间线、状态机一致）
3. **落地顺序建议**：
   - 前端结构先行：路由迁移 + Library 合并（搭 #12–14 收敛出的 `usePagedList` 便车）
   - **导入台可提前独立交付**（后端 = 上传/解析小端点 + 编码探测 + 简繁转换，前端 = 1 页 2 tab，不依赖 IA 迁移完成）
   - 校对台：把 `TranslationDetailPage` 拆出的校对组件放进新壳（对应 P2 #21）
   - Queue 改造：轨道条组件已存在（签名 Progress），换数据呈现
   - Dashboard 重做：StatisticsService 加"当前队列/活跃任务"两个查询，SignalR 已就绪
   - P3-a/b 在以上稳定后进

## 来源（查证日期 2026-10-12）

- Lingarr 官方仓库与官网：github.com/lingarr-translate/lingarr · lingarr.com
- subtitle-translator：github.com/rockbenben/subtitle-translator（1.2k★，MIT）
- TranslateBooksWithLLMs：github.com/hydropix/TranslateBooksWithLLMs（2.4k★，AGPL）
- GPT-Subtrans：github.com/machinewrapped/gpt-subtrans
- pyvideotrans：github.com/jianchang512/pyvideotrans（SRT 批量/术语表/双语输出文档）
- Subgen（Whisper 生成字幕 + 媒体服务器 webhook）：noted.lol/subgen
- ChineseSubFinder：github.com/ChineseSubFinder/ChineseSubFinder（3.9k★，Go，2025-01 停更；导入链路本地源码分析见 §3.6.1）
- Bazarr / Subtitle Edit：github.com/morpheus65535/bazarr（导入参考弃用，仅作 *arr 生态参照） · github.com/SubtitleEdit/subtitleedit
- SaaS 市场格局：maestra.ai、blog.laratranslate.com、blog.openl.io（2026 年度盘点文）
