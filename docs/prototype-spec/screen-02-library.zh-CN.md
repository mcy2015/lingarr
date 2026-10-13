# Screen 02 · 媒体库(`data-screen="library"`)

主导航第 2 屏。全部媒体(剧集+电影合并视图)的检索与批量翻译入口。

- **入口**:侧栏「媒体库」(徽标 1 284)/ 快捷键 `2` / 概览管线节点 / 队列屏 CTA
- **顶栏**:媒体库 · 1 284 项 · 剧集与电影合并视图 · CTA「翻译所选 ▸」(见交互 6)

## 布局

toolbar(固定)→ 表格卡(滚动收进卡内,`.card` flex:1 overflow auto)→ 托盘(勾选 >0 出现,sticky bottom)→ 分页(固定)。

## 元素清单

### 1. 工具栏(`.toolbar`)

| 元素 | 类名/ID | 内容 | 交互 |
|---|---|---|---|
| 类型分段 | `#lib-seg .seg` | 全部(默认选中)/ 剧集 / 电影 | `aria-pressed` 单选,驱动过滤 |
| 搜索框 | `.search #lib-search` | placeholder「搜索标题…」 | input 即时过滤(小写包含,匹配整行文本) |
| 目标语言 | `#lib-lang .btn.sm` | 「目标语言 ZH ▾」 | 下拉菜单:ZH · 中文 / EN · English / JA · 日本語(当前勾选;选中回写 mono 值) |
| 状态过滤 | `#lib-state .btn.sm` | 「全部状态 ▾」(`#lib-state-l`) | 下拉菜单 7 项:全部状态/翻译中/队列中/失败/待校对/已完成/未排队 |
| 导入按钮 | `.btn.sm` | 「⇪ 导入字幕」 | → import(free tab) |
| 计数 | `#lib-count .mono` | 默认「1 284 项 · 63 缺字幕 · 14 进行中」 | 过滤激活时改写为「匹配 N / 1 284 项」 |

### 2. 媒体表(6 列)

列:选择(34px)/ 标题 / 字幕轨 / 状态 / 最近活动 / 操作(90px)。

| 列 | 内容与格式 |
|---|---|
| 选择 | 自定义 checkbox,`aria-label="选择 <标题>"` |
| 标题 | `.badge`(剧集=show 蓝边 / 电影=movie 中性)+ 标题 b + meta(年份 · Sxx · N 集 / 年份 · N min) |
| 字幕轨 | `.chips`:每语言一枚 chip —— `EN ✓`(ok)/ `ZH ◐ 3/10`(part 部分完成)/ `JA ·`(dim 无)/ `ZH ⏳ 38%`(busy 进行)/ `ZH ✗ 5/6`(dim 部分失败) |
| 状态 | `.state`:done「✓ 10/10 完成」/ part「◐ 翻译中」「⏳ 队列中」「◐ 待校对」/ danger「✗ 1 集失败」 |
| 最近活动 | mono 12px 时间 |
| 操作 | 剧集行:「整季导入」(→ import 并 setImportTab('season'))+「详情」(→ media-detail);电影行:「导入字幕」(→ import)+「详情」(→ media-detail-movie) |

示例 6 行(覆盖全部状态谱):Shōgun(翻译中)、The Bear(✓ 完成)、Dune: Part Two(队列中,✓预勾)、True Detective(✗ 1 集失败,✓预勾)、Past Lives(队列中,✓预勾)、3 Body Problem(◐ 待校对)。

### 3. 批量托盘(`#tray .tray`)

勾选数 >0 时显示(`.on`),sticky bottom、accent 边:

| 元素 | 行为 |
|---|---|
| 「已选 N 项」 | N 由 `syncTray()` 实时统计 |
| 「翻译所选 EN → ZH ▸」(primary) | `trayRun()`:toast「已加入翻译队列 · N 项 · 预计 $N×0.07」→ 清空勾选 → 跳 queue |
| 「加入队列末尾」 | toast「已加入队列末尾 · N 项」→ 清空勾选(不跳转) |
| 「清除已选」 | 仅清空勾选 |
| 右侧预估(静态) | 「预计消耗 ≈ $0.21 · 41 800 字符」(迁移时应随勾选动态计算) |

### 4. 分页(`.pager`)

「第 1 / 129 页」+ ‹ ›(上下限 1–129 钳制)。

## 交互逻辑

1. **三联动过滤**(`applyLibFilter`):类型 AND 状态 AND 搜索词;每行初始化时从行文本派生 `dataset.type`(含「电影」→ movie)与 `dataset.st`(翻译中→run / 队列中→wait / 失败→fail / 待校对→proof / 完成→done / 其余 none);不匹配行加 `.hidden`;计数回写。**迁移时必须改为真实数据字段过滤**(文本派生是原型取巧)。
2. **勾选 → 托盘**:checkbox change → `syncTray()`(计数 + `.on` 切换)。
3. **行操作跳转**:剧集行带 `setImportTab('season')` 深链;详情按类型分流剧集/电影屏。
4. **分页**:仅页号状态,无行数据变化(原型示意)。
5. **状态菜单**:选中项回写按钮文案 + `libSt` 状态 + 重过滤。
6. **CTA「翻译所选」**(`ctaLibrary`):无勾选时自动勾选第一行并同步托盘 → 开菜单(EN → ZH · 简体,默认勾选,执行 `trayRun` / EN → ZH · 繁体,toast + 清空)。

## 状态完备(迁移时补)

空库(无媒体)空态;搜索/过滤零结果的空态(当前仅消失行);分页边界;托盘预估成本动态化;标题排序;「缺字幕」快捷过滤(管线图「缺字幕检测」跳入后应激活对应状态过滤)。
