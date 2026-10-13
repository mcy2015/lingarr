# Screen 11 · 日志(`data-screen="logs"`)

下钻屏。系统运行日志的级别过滤 + 实时滚动 + 导出。

- **入口**:设置 · 系统「打开日志」/ 队列失败行「日志」/ 任务详情「日志」/ ⌘K「日志」
- **顶栏**:日志 · 环形缓冲 2 000 条 · SSE 实时 · CTA「⇩ 导出」
- **布局**:pb-head(固定,含过滤与开关)→ 日志卡(滚动收进卡内)。

## 元素清单

### 1. 页头(`.pb-head`)

| 元素 | 内容/行为 |
|---|---|
| 「← 系统」 | 返回 settings |
| 标题块 | 「日志」+ mono 副行「环形缓冲 2 000 条 · SSE 实时 · Information 级」 |
| 级别过滤 `.chips`(role=group「级别过滤」) | `全部 12`(默认 pressed)/ `INFO 8` / `WARN 3` / `ERROR 1` —— chip 型按钮,`aria-pressed` + accent 选中态 |
| 自动滚动 | label + switch(on) |

### 2. 日志列表(`#loglist`)

`.logline` 三列(70px time / 46px 级别 / 1fr 消息),mono 11.5px;消息单行省略。级别色:INF=info、WRN=warning、ERR=danger;`.warnrow/.errrow` 整行消息着色。

12 条示例(事件语义与队列/任务详情屏互相印证):
- 15:41:22 INF — TranslationRequest #2417 批次 3/4 · 368/512 行 · 1.4 行/s
- 15:40:05 INF — TranslationRequest #2418 创建 · Dune: Part Two · 手动添加
- 15:38:44 INF — AutomatedTranslationJob 扫描完成 · Radarr「最近添加」扫出 6 部缺中文字幕
- 15:36:51 WRN — GeminiService 429 Rate Limited · attempt 2/3 · 退避 8s
- 15:36:12 INF — #2417 降级 → OpenAI gpt-4o-mini · 回退链第 2 位 · 批次 1 断点续传
- 15:36:02 WRN — GeminiService 429 · attempt 1/3 · 退避 4s
- 15:35:48 INF — #2417 开始翻译 · gemini 2.5-flash · 512 行 4 批
- 15:29:41 ERR — TranslationJob #2409 失败 · True Detective S04E06 · 回退链耗尽(429 ×3)
- 15:29:12 WRN — GeminiService 429 · attempt 3/3 · 切换回退链下一位
- 15:02:19 INF — ProofreadRequest #2416 打开 · 3 Body Problem S01E02 · 486 行
- 14:57:33 INF — 写回 The.Bear.S03E01.zh.srt · 512 行 · $0.021
- 04:00:07 INF — StatisticsJob 完成 · 312s · DailyStatistics 写入

## 交互逻辑

1. **级别过滤**:chip 点击 → `aria-pressed` 单选 + 非 all 时隐藏 `data-lv` 不符行(`.hidden`);计数(12/8/3/1)静态。
2. **自动滚动 switch**:开关切换(原型未接线;迁移时 = 新日志到达时是否 `scrollTop` 跟随,用户上滚阅读时自动暂停)。
3. **导出**(顶栏 CTA `exportLogs`):遍历当前行拼 `time  LV   msg` 文本 → download `lingarr-logs.txt` + toast「日志已导出 · 12 条」。

## 状态完备(迁移时补)

按请求 ID 过滤(与任务详情联动);时间范围检索;级别计数动态;环形缓冲滚动加载(2 000 条);空态;SSE 断连提示;导出遵循当前过滤(原型导出全部行,含被隐藏行 —— 迁移时需决定并固定语义)。
