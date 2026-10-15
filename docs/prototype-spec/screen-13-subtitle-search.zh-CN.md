# screen-13 · 字幕搜索与下载 UI 增量（subtitle-search）

日期：2026-10-15（字幕搜索立项）｜配套：[PRD](../subtitle-search-prd.zh-CN.md) · [技术方案](../subtitle-search-tech-design.zh-CN.md)
本文件覆盖**一个新下钻屏 + 一个新交互原语 + 四处既有屏增量**——真实应用迁移时全部以此为验收依据。

## 增量清单（对 refactor-prototype.html）

| 位置 | 增量 | 锚点 |
|---|---|---|
| screen-01 概览 | KPI 网格第 5 格「字幕补缺」（积压 N · 今日下载 M，点击→sub-history） | `.kpis` 改 5 列 |
| screen-06 设置·sources | 「字幕源」第二卡组：3 张 `.pcard`（OpenSubtitles/assrt/shooter），含代理 switch、测试连接、健康态、配额行 | `.grp-label` + `.set-grid` |
| screen-06 设置·automation | 自动化批次卡新行「字幕自动补缺」（data-sub="1"）：常显实时 hint + 查看记录/▶ 运行/编辑/开关 | `.setlist .row` |
| screen-07/08 媒体详情 | 「字幕文件」卡（外挂行+内嵌轨行，卡头「🔍 搜索字幕」按钮）；剧集集表「翻译 ▾」菜单加「搜索字幕 · 本集」 | `.card` 中位，`flex:none` |
| 新原语 `#smodal` | 搜索弹层（列表型模态：查询条固定+结果区滚动） | 仿 `#fmodal` 骨架 |
| screen-13 `sub-history` | 字幕下载记录下钻屏：pb-head+四维筛选 toolbar+滚动表+keyset 分页 | `data-screen="sub-history"` |

## #smodal 搜索弹层（新原语，API=分源并发三请求）

**结构**（`.modal > .modal-box.smodal-box`，780px，max-height 76vh）：mhead（标题=媒体名+集号）→ `.sq-bar` 查询条（mono 输入预填 release 名 + 语言 chips ZH/EN 可点选 + 搜索按钮）→ `.sq-src` 逐源状态行 → `.sq-head` 结果头（实时 meta + 分数线说明）→ `.sq-res` 候选滚动区。

**候选行 `.subrow`**：2px 左轨（ZH=success 绿 / EN=track-a 蓝）｜release_info（mono truncate+title 全文，次行=上传者·格式·大小）｜语言 chips（ZH·免翻译/hash ✓/forced/HI/文件已存在）｜分数（hash=success 色、<分数线=warning 色+title 说明）｜来源 chip｜动作。

**动作语义（双路径裁决的界面表达）**：ZH 行→「下载」（落盘即终态）；EN 行→「仅下载」+「下载并翻译 ▸」（后者入翻译队列）；已存在行→「重新下载」ghost。

**状态完备（9 态）**：逐源骨架「X 源搜索中…」先到先插｜全空态｜冷却中（源行 warning「冷却至 14:32·本轮跳过」+「重试该源」链接，重试后变绿）｜低置信（分数 warning 色不阻塞手动）｜已存在（行 dim+徽章）｜下载中（本行 ops 禁用+pointer-events:none，其余行可并行）｜下载失败（行内 danger+重试）｜部分源失败（源行如实标注，计数「命中 M/3 源」）｜下载成功（按钮变 ✓ 已下载+toast 含落盘文件名，弹层不关）。

**联动**：下载成功 → 概览 KPI（`#kpi-sub-backlog` -1、`#kpi-sub-dl` +1）→ 批次卡 hint 积压同步 → 当前屏字幕文件卡插入新行（`#md-sub-list`/`#mdm-sub-list`，meta 计数 +1）→ sub-history 表头插「刚刚」行。「下载并翻译」→ 队列 tab 计数 +1。

**实时量清单（禁静态）**：`候选 N · 命中 M/3 源 · 耗时 X.Xs`（随逐源到货更新）；分数线值来自设置。

## 设置·sources「字幕源」卡组

组标题 `.grp-label`（名称+meta「为手动搜索与自动补缺供货」）。每卡：dot 健康态（ok 在线/warn 冷却中·mono 到期时刻+最后错误/derr 错误·最后错误摘要）/凭据与配额行（mono，「今日配额 187 / 1 000」仅在有日配额的源渲染）/usage 行（搜索/下载次数·延迟）/ops 行（**代理 switch**（行内切换+toast 说明走全局 proxy_url 或直连）+测试连接（写卡底 `.test-res`，失败带源侧错误码）+编辑（`data-subedit` → openForm：API Key/Token password 型；shooter 无凭据字段））。

## 设置·automation「字幕自动补缺」批次行

行结构同每日补缺：名称 + **常显实时 hint mono**（`cron · 分数线 75 · 每轮 ≤200 · 自动翻译 开 · 积压 12 480 · 下轮 200`，编辑保存后回写）+ 下次执行（含时区后缀）+ ops（查看记录→sub-history / ▶ 运行→toast 本轮 200 项预计 25 分钟 / 编辑（cron/分数线 0-100/每轮上限/自动翻译下拉）/ switch）。
**优先级语义**为编辑表单底部只读 mono 行：`Pinned > Webhook 新入库(48h) > 存量(指数退避)`——策略语义非装饰文案，允许。0 启用源时卡上警示行+跳 sources 链接（首用守卫，复用每日补缺先例）。

## screen-13 `sub-history` 字幕下载记录

- **pb-head**：← 概览 返回；副标题 mono 实时「共 41 208 条 · 筛选后 N 条」；**无 CTA**（只读屏，顶栏按钮隐藏）。
- **toolbar 四维筛选**：来源（全部/opensubtitles/assrt/shooter）· 途径（全部/手动/自动/webhook）· 结果（全部/成功/失败/低置信跳过/已存在跳过）· 媒体名搜索 + 清除筛选；右侧 mono 计数。
- **表**（`.card.tcard` 内滚+表头吸顶）：时间(mono)/媒体/来源 chip/途径 chip/分数(mono 右对齐)/结果列（✓ 写回 `xxx.zh.srt`·免翻译｜✗ 错误摘要（429 已冷却/写盘只读/伪 zip 魔数）｜◐ 低置信 62<75·跳过｜· 同语言已存在·跳过）/操作（失败与低置信行「重试/重搜」→ 跳媒体详情并开 #smodal）。
- **分页**：`.pager` mono「第 x / 825 页」——10w 级必须真实计数（keyset），禁「加载更多」。
- **空态两种文案**：无记录（引导启用源）vs 筛选空（0 条 · 调整筛选）。
- **视口铺满**：`[data-screen="sub-history"] .card.tcard{flex:1;min-height:0;overflow:auto}`，pb-head/toolbar 固定。
- **深链**：`#sub-history`；⌘K 两组新条目（字幕源→settings/sources、字幕下载记录）；导览 KPI 第 5 格与批次卡「查看记录」为入口。

## 交互原语复用/新增声明

零新全局交互模式：复用 `.set-grid`/`.pcard`/`.test-res`/openForm/openMenu/toast/go+hash/`.pager`/setlist 行式/批次卡+首用守卫/KPI 格。新增仅两件：`#smodal`（带滚动结果区的列表型模态）与 `openSubSearch(media, ep?)`（注册进 global-interactions 原语表）。ESC/遮罩关闭；弹层不进 hash（瞬态操作，刷新即关，cmdk/fmodal 同例）。

## 迁移时补（真实应用状态完备）

- #smodal：服务端单源 10s 超时的错误分桶展示、搜索中可取消、候选缓存过期（10min）后重搜提示。
- sub-history：1 000 000 行级 keyset 分页正确性、筛选组合与 URL query 同步。
- i18n namespace：`subSearch`（弹层）/`subSources`（源卡组）/`subFetch`（批次）/`subHistory`（历史屏），双 locale 同步 append。

## 原型演示数据口径（非真实值，交互后变化）

候选 8 条覆盖全状态（hash ZH×2/双语 ZH/en×2/繁体低分 exists/shooter 低分×2）；assrt 演示为冷却源；积压 12 480/今日 23 随下载 -1/+1；历史 12 行混合四途径×四结果。**真实应用中所有数字来自 API（backlog/kpi/history 端点），不得硬编码。**
