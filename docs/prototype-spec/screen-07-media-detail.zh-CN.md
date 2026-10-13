# Screen 07 · 媒体详情 · 剧集(`data-screen="media-detail"`)

下钻屏。单个剧集的逐集字幕轨状态 + 翻译历史;批量排队缺字幕集的入口。

- **入口**:媒体库剧集行「详情」/ ⌘K「Shōgun」
- **顶栏**:媒体详情 · Shōgun · S01 · 逐集字幕轨与翻译历史 · CTA「翻译缺失 ▸」
- **布局**:pb-head(固定)→ toolbar(固定)→ 集表卡(flex 1.5,滚动)→ 翻译历史卡(flex 1,滚动)。手机单列。

## 元素清单

### 1. 页头(`.pb-head`)

| 元素 | 内容/行为 |
|---|---|
| 「← 媒体库」 | 返回 library |
| 标题块 | 「Shōgun」+ mono 副行「2024 · 1 季 · 10 集 · EN → ZH · 缺 7 集中文字幕」 |
| 「⇪ 整季导入」 | → import 并 `setImportTab('season')` |
| 「翻译缺失 EN → ZH ▸」(primary) | 见交互 1 |

### 2. 工具栏(`.toolbar`)

- 季分段 `.seg`:S01(单季示例,`aria-pressed`)
- 语言 chips:`EN ✓`(ok)/ `ZH ◐ 3/10`(part)/ `JA ·`(dim)
- 右侧 mono「最近活动 15:41」

### 3. 集表(6 列)

列:`#`(44px)/ 集标题 / 字幕轨 / 状态 / 最近活动 / 操作(150px)。10 行(Shōgun S01)覆盖完整状态谱:

| 集段 | 状态(对应 class) | 字幕轨 chips | 操作 |
|---|---|---|---|
| E01–E03 | ✓ 已完成(state done) | EN ✓ + ZH ✓ | 「校对」→ proofread |
| E04 | ✗ 失败 · Gemini 429(danger) | EN ✓ + ZH ✗(dim) | 「重试」+「详情」→ queue-detail |
| E05 | ◐ 翻译中 72%(part) | EN ✓ + ZH ⏳ 72%(busy) | 「详情」→ queue-detail |
| E06–E10 | · 未排队(state none) | EN ✓ + ZH ·(dim) | 「翻译 ▾」菜单 |

集标题列:`.ep-num`(E0x mono)+ `.ep-title b`(英文集名)。

### 4. 翻译历史卡

卡头:「翻译历史」+ meta「近期 5 / 24 个请求」。`.hist-row` ×5(标题+副行 / 服务 chip / 迷你 track 或状态 / 「详情」→ queue-detail):

| 请求 | 副行 | 服务 | 右侧 |
|---|---|---|---|
| S01E05「Broken to the Fist」 | EN → ZH · 72% · 剩 04:12 | openai · gpt-4o-mini | track 72%(110px) |
| S01E01「Anjin」 | 写回 Shogun.S01E01.zh.srt · 512 行 | deepl · api-free | ✓ 完成 |
| S01E02 | 写回 · 498 行 · $0.018 | deepl · api-free | ✓ 完成 |
| S01E03 | 写回 · 505 行 · $0.019 | deepl · api-free | ✓ 完成 |
| S01E04「The Eightfold Fence」 | Gemini 429 · 自动降级失败 | gemini · 2.5-flash(danger 边) | ✗ 失败 |

## 交互逻辑

1. **翻译缺失**(`mdTranslateMissing`,顶栏 CTA 与屏内主按钮共用):筛选状态含「未排队」的集行;空 → toast「无待排队剧集」;否则逐行 `setEpQueued()`(状态 →「◐ 排队中」part、ZH chip → busy「⏳ 排队」、操作列替换为「详情」→ queue),`qCounts.wait/all += N` 同步队列 tab 计数,toast「已排队 N 集 · EN → ZH」→ 跳 queue。
2. **「翻译 ▾」菜单**(未排队集):「翻译 EN → ZH · 简体」(默认)/「繁体」—— 单集入队(同 setEpQueued + 计数 + toast「已排队 · 集名」)+ 跳 queue。
3. **「重试」**(失败集):单集重新入队 + 计数 + toast「已重新入队 E04 · 走回退链 openai」。
4. 「校对」→ proofread;「详情」→ queue-detail;历史行「详情」→ queue-detail。

## 状态完备(迁移时补)

多季(当前单季示例,seg 需真实季数据);全季完成态(主按钮消失/变校对入口);0 集剧集;历史分页或「查看全部 24 个」;电影无集表(已拆分为独立屏 screen-08)。
