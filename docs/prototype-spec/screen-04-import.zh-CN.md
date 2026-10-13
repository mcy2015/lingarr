# Screen 04 · 导入与转换(`data-screen="import"`)

主导航第 4 屏。两种导入模式:自由导入(散文件:解析/转码/转格式/去重)与定向导入 · 整季(字幕包按集映射)。「导入即任务」:处理后可同时进入翻译队列。

- **入口**:侧栏「导入与转换」/ 快捷键 `4` / 概览与媒体库的「⇪ 导入字幕」/ 媒体库「整季导入」(带 `#import/season` 深链)/ 媒体详情「整季导入」
- **顶栏**:导入与转换 · 拖拽上传或服务器路径 · 转码 / 转格式 / 去重后入管线 · CTA「开始处理 ▸」(仅作用于自由导入 tab)

## 布局

tabs(固定)→ 两个 `.panel`(free 默认 on)。free:左右网格 300px + 1fr,两卡纵向拉伸、滚动收进卡内 `.tscroll`(表头 sticky),选项条钉在卡底。season:页头 → 拖放条 → 映射预览卡(同样卡内滚动 + 底部操作条)。手机模式单列。

## A. 自由导入(`data-ip="free"`)

### 左列

| 元素 | 内容 |
|---|---|
| 拖放区 `#drop-free` | 「拖拽字幕文件到此处 / 或点击选择文件」+ 格式说明 `.srt · .ass · .ssa · .vtt(P3-f 规划中,vtt 50% 透明)`。click 唤起文件选择,drop 接收文件 |
| 路径条 `.pathpick` | mono 路径 `/media/subtitles/inbox` + 「扫描路径」按钮(**disabled**)+ chip dim「规划中 · B3-A7」 |
| 提示卡 `.hint-card` | 「**导入即任务** · 文件经『解析 → 编码转码 → 转格式』后写回原目录,可同时进入翻译队列;按内容哈希与既有任务去重。」 |

### 右列:处理队列卡

- 卡头:「处理队列」+ meta `#free-meta`「4 个文件 · 3 个可处理」(`freeStats()` 动态:可处理 = 非 opacity<1 行)
- 表 6 列:文件(mono)/ 检出(格式 · 行数 chip)/ 编码(`badge enc`「GB18030 → UTF-8」或 chip ok「UTF-8 ✓」)/ 目标(SRT / 原格式)/ 动作(act-toggle)/ 状态

示例 4 行(状态谱):
1. Rurouni.Kenshin.OVA.chs.srt — SRT · 1 024 行 · 需转码 · →SRT · 「转换＋翻译」· ◐ 等待
2. Blade.Runner.2049.chs.ass — ASS · 1 890 行 · UTF-8 ✓ · →SRT · 「仅转换」· 转写中(track 64%)
3. Severance.S02E03.en.srt — UTF-8 ✓ · 原格式 · 「仅翻译 EN→ZH」· △ warning「命中已有任务 · 建议跳过」
4. Dune.Part.Two.2021.vtt(整行 opacity .55)— VTT dim · 「· 格式支持规划中(P3-f)」

- **底部选项条**(卡内 flex:none,应始终可见):
  | 选项 | 控件 | 值域 |
  |---|---|---|
  | 转码 UTF-8 | switch(on) | 开/关 |
  | 简繁 | drop-chip `data-menu="convert"` | 简体 / 繁体 / 原样 |
  | 双语输出 | switch(off) | 开/关 |
  | 写回 | drop-chip `data-menu="writeback"` | 导入库目录 / 原目录 / 下载 |
  | 冲突 | drop-chip `data-menu="conflict"` | 跳过 / 加后缀 / 覆盖 |
  右侧预估:「预计 3 项 · 转写 2 914 行 · 翻译 2 项 ≈ $0.11」

### 交互

1. **文件接入**:click/drop → 隐藏 `<input type=file multiple accept=".srt,.ass,.ssa,.vtt">`(`fileTarget` 记录来源模式)。
2. **文件嗅探 `sniffFile`**:ArrayBuffer + `TextDecoder('utf-8',{fatal:true})` 探测是否 UTF-8;行数统计 —— srt/vtt 按空行分块计数,ass/ssa 数 `^Dialogue:` 行。
3. **入列**:追加行(文件名/格式·行数/编码结果/目标 SRT/「转换＋翻译」/◐ 等待)+ `freeStats()` + toast「已入列:文件名」。
4. **动作切换**(`.act-toggle` 点击循环):`转换＋翻译 ↔ 仅转换`;`入库＋翻译 ↔ 仅入库`;`仅翻译 EN→ZH → 转换＋翻译`;`入库＋翻译 · 待指派 → 仅入库`。part 类随动作是否含「＋」变化。
5. **选项下拉**:drop-chip → openMenu(当前值勾选,选中回写 chip 文案)。
6. **CTA「开始处理」**(`runFreeImport`):可处理行为 0 → toast「处理队列为空 · 先拖入文件」;否则 toast「处理已开始 · N 项进入管线 · 转码后写回」→ 跳 queue。

## B. 定向导入 · 整季(`data-ip="season"`)

### 页头(`.pb-head`)

标题「Shōgun — 第一季」+ mono 副行「目标 · 10 集 · 已有 ZH 字幕 3/10 · 映射自文件名」+「更换目标 ▾」(菜单:Shōgun · S01(当前)/ The Bear · S03 / 3 Body Problem · S01,选中改写标题)。

### 拖放区 `#drop-season`

「拖入整季字幕包 或 点击选择文件」+ 规则说明「文件名含 SxxExx / E01 / 1x02 / 第x集 均可自动映射 · .srt / .ass · 中文简繁自动归一」。

### 映射预览卡

- 卡头 meta `#season-meta`「10 个文件 · 9 已匹配 · 1 待指派」(`seasonStats()` 动态)
- 表 6 列:文件 / 匹配集(「E01「Anjin」」或「指派到集 ▾」按钮)/ 置信度(✓ 精确 ok / ✗ 未匹配 danger / ◐ 手动指派 part)/ 语言(ZH / ZH·简 / ZH·繁 / EN / + dim「注释轨」)/ 动作(act-toggle:入库＋翻译 / 仅入库 / 转换＋翻译 / dim「待指派」)/ 状态(✓ 就绪 done / ASS → SRT 转写 / △ 哈希命中建议跳过 warn / △ 手动指派后导入 warn)

示例 6 行涵盖:精确匹配(4,含「第x集」中文命名命中、注释轨、哈希命中)+ 未匹配待指派(Shogun.S01.complete.ass → 指派到集 ▾)。

- **底部操作条**:说明文字「导入完成后不自动排队翻译(防覆盖语义)· 写回前清理同视频 .default/.forced 标记」+「排除未匹配」+ primary「开始导入 9 项 · 翻译 7 项 ≈ $0.31」(文案由 `seasonStats()` 动态:导入=匹配数,翻译=max(匹配-2,0),成本=匹配×$0.034 —— 原型示意算法,迁移时换真实估算)。

### 交互

1. **集数解析 `parseEp`**(优先级序):`SxxExx` → `E01`(独立 E+数字)→ `1x02` → `第x集`;均不中 → 未匹配。语言:文件名含 `.eng./.en./english` → EN,否则「ZH · 简」。
2. **入列**:命中(1–10)→ 插入「E0x「标题」/ ✓ 精确 / ✓ 就绪」行;未命中 → 插入待指派行(指派按钮 + ✗ 未匹配)。`seasonStats()` + toast「映射 ✓ E0x / ✗ 未匹配:文件名」。
3. **指派到集**:菜单列全部 10 集标题;选中 → 匹配列固化为「E0x「标题」」、置信度 →「◐ 手动指派」、状态 →「✓ 就绪」、动作恢复可切换;重算统计 + toast。
4. **排除未匹配**:移除全部含「未匹配」行 + 重算;无可排 → toast「没有未匹配项」。
5. **开始导入**(`runSeasonImport`):toast「导入已开始 · N 项入库 · M 项进入翻译」→ 跳 queue。

## tab 切换

`setImportTab(name)`:`data-it` tab 的 `aria-selected` + `data-ip` panel 的 `.on`。外部深链入口:`#import/season`(hash 第二段)、`setImportTab('season')` 全局函数(媒体库/媒体详情的「整季导入」调用)。

## 布局实现要点(已实施的钉卡底方案)

处理队列卡为三段式:卡头固定 / 表格滚动层 `.tscroll`(表头 sticky)/ 底部选项条 `flex:none` 钉卡底 —— 行多时选项条不随内容滚走,滚动只发生在表格层。**工程注意(已踩过):裸 `<table>` 做不了 flex 滚动宿主(指定高度被当最小值,flex:1 不收缩),必须包 `.tscroll` div wrapper**。手机模式零额外规则。

## 状态完备(迁移时补)

两 tab 的零文件空态;哈希去重命中行的「跳过」一键动作;非 UTF-8 编码名展示(GB18030 仅示例);上传中/处理中的行内进度态;VTT 启用后的格式开关;预估成本的实时重算(选项变更即变)。
