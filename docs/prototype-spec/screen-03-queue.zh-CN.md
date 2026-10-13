# Screen 03 · 任务队列(`data-screen="queue"`)

主导航第 3 屏。全部翻译任务的状态过滤与生命周期操作(暂停/取消/重试)。

- **入口**:侧栏「任务队列」(徽标 hot 14)/ 快捷键 `3` / 概览管线节点与 KPI / 托盘入队后 / 导入完成后
- **顶栏**:任务队列 · 实时 · SignalR 推送 · CTA「＋ 新建任务」→ 跳 library + toast「从媒体库勾选条目即可新建翻译任务」

## 布局

tabs(固定)→ `.qcol` 滚动容器(视口铺满:滚动收进队列列表)。

## 元素清单

### 1. 过滤 tabs(`.tabs`,role=tablist「队列过滤」)

| tab | data-q | 计数(初始) |
|---|---|---|
| 全部 | all | 24 |
| 进行中 | run | 2 |
| 排队 | wait | 12 |
| 失败 | fail | 1 |
| 已完成 | done | 9 |

`aria-selected` 单选;计数由 `qCounts` 对象维护,增删任务后 `qSyncCounts()` 回写 tab 上的 `.n`(跨屏联动:媒体详情排队、队列取消/重试都会改计数)。

### 2. 队列行(`.qrow`,六列网格)

列:状态轨(3px rail)/ 标题块 / 服务 chip / 进度 track / eta / 操作。

| 状态类 | rail 色 | 操作列 | eta 表现 |
|---|---|---|---|
| run | accent | ⏸ ✕ | 「72% · 04:12」 |
| wait | line | ✕ | 「00:00」,track 空 |
| fail | danger | 重试 + 日志 | danger 色「✗ 83% · 2 次重试」,track danger 填充 |
| done | success | 校对 | success 色「✓ 15:41 完成」,track 100% success |
| paused(JS 加) | warning | ▶ ✕ | 不变 |

示例 7 行(状态谱全覆盖):
- run ×2:Shōgun S01E05(批次 3/4 · 已译 368 行,openai · gpt-4o-mini,72% · 04:12)、Dune: Part Two(上下文窗口 500 行,deepl · api-free,38% · 11:47)
- wait ×2:The Bear S03E02(排队位次 1 · 随批自动化)、Past Lives(手动添加,ollama · qwen3:14b)
- fail ×1:True Detective S04E06(Gemini 429 连续 3 次 · 自动降级失败;服务 chip danger 边)
- done ×2:The Bear S01E01(写回 512 行 · $0.021 → 校对)、3 Body Problem S01E02(术语命中 12 次 · 进入校对)

## 交互逻辑

1. **tab 过滤**:非 all 时隐藏 `data-st` 不符的行(`.hidden`)。
2. **整行点击** → queue-detail(行内按钮点击冒泡除外)。
3. **⏸/▶ 暂停恢复**:toggle 行 `.paused`(rail 变 warning)、按钮互换文案与 title;toast 语义:「已暂停 · 批循环检查点挂起,重启自动恢复」/「已恢复」。
4. **✕ 取消**:行移除;`qCounts[原状态]--`、all--,同步 tab 计数;toast「已取消任务」。
5. **重试**:行 st→run(class 换);eta →「◐ 0% · 重新入队」;副行 →「EN → ZH · 手动重试 · 走回退链 openai」;按钮变 ⏸;计数 fail--/run++;toast「已重新入队 · gemini ✗ → openai」。
6. **「日志」按钮**(fail 行)→ logs;**「校对」按钮**(done 行)→ proofread。
7. 数据语义:eta/进度/批次来自任务运行时(SignalR 实时);副行携带任务来源(随批自动化/手动添加/位次)。

## 状态完备(迁移时补)

队列为空(全部 tab)空态;各 tab 零结果空态;取消进行中任务的确认交互(原型直接执行);批量暂停/取消;排序;队首任务与执行中的并发上限提示(并发 2,见 schedule 屏)。
