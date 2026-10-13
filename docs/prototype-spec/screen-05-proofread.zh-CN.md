# Screen 05 · 校对台(`data-screen="proofread"`)

主导航第 5 屏。机翻结果的逐行人机校对:对照编辑、通过/驳回、术语回流、快照导出、写回。

- **入口**:侧栏「校对台」(徽标 1)/ 快捷键 `5` / 队列 done 行「校对」/ 媒体详情集行「校对」/ 任务详情「校对」
- **顶栏**:校对台 · 3 Body Problem S01E02 · 128/486 行 · CTA「暂存 ⌘S」→ toast「已暂存 · 128/486 行」

## 布局

pb-head(固定)→ `#plines` 滚动(行列表)→ pb-foot(sticky bottom,快捷键提示 + 三个动作)。手机模式行单列、时间码横滚、隐藏快捷键提示。

## 元素清单

### 1. 页头(`.pb-head`)

| 元素 | 内容 |
|---|---|
| 标题块 | 「3 Body Problem — S01E02「Red Guard」」+ mono 副行「EN → ZH · deepl api-free → 人工精校 · 486 行」 |
| 进度 `.prog` | `.track` 26% + 「已校对 128 / 486 行」(mono) |
| 图例 `.legend` | ■ track-a 源轨 EN / ■ track-b 译文轨 ZH |

### 2. 校对行(`.pline`,四栏:172px 时间码 / 源文 / 译文 / 34px 标记)

| 栏 | 内容 |
|---|---|
| `.tc` | `#412` 序号(mono,`#idx` 主色)+ 起止时间码两行(00:12:41,520 / → 00:12:44,080),tertiary 底 |
| `.src` | 英文原文,左侧 2px track-a 色条;可含修订痕迹 `del.w`(danger 删除线,如 ~~interpreter~~) |
| `.dst` | 中文译文,左侧 2px track-b 色条;文本态 `.dst-text`,编辑态 `input.syn` |
| `.mark` | ✓(appr)/ ✗(rej)/ ·(待定),title 对应「已通过/已驳回/待校对/待定」 |

行状态类(可组合):
- `.appr` — 已通过:mark ✓ success
- `.rej` — 已驳回:mark ✗ danger + 译文删除线
- `.focus` — 当前行:accent 边框,译文区 cursor:text
- `.dirty` — 已修改未保存:译文底 accent 7%

**编辑态行内结构**(`.dst` 内):
```
input.syn(译文输入框)
.editbar:
  ✓ 通过(primary,pb-ok)   ✗ 驳回(pb-rej)
  「术语 叶文洁 → Ye Wenjie 已锁定」(warning 11.5px)
  「● 已修改,未保存」(accent,右对齐)
```

**术语标注 `.gloss`**:译文后的虚线小胶囊(`黑暗森林 → Dark Forest`、`Anjin → 按针`),警示该词受术语表控制。

示例 6 行状态谱:appr×2(纯文本)/ rej(含 gloss)/ focus+dirty(编辑态完整结构)/ 待校对×2。

### 3. 底栏(`.pb-foot`)

| 元素 | 行为 |
|---|---|
| `.keys` 快捷键 | J K 上下行 / Enter 通过当前行 / E 编辑 / ⌘S 暂存(键帽样式) |
| 「✎ 3 处修改 → 建议入术语表」 | → settings 并 `setSettingsTab('quality')`(术语回流深链) |
| 「导出当前快照」 | `exportProofread()`:遍历全部行拼 SRT(序号/时间码/译文 —— 编辑态取 input.value,文本态取 .dst-text),download `3body-s01e02.snapshot.srt` + toast |
| 「完成校对并写回(128/486)」(primary) | toast「已写回 3 Body Problem S01E02.zh.srt · 486 行」→ 跳 queue |

## 交互逻辑(键盘系统核心)

1. **点击行** → 该行 `.focus`(互斥单选)。
2. **J / K** → 下/上一行聚焦 + `scrollIntoView({block:'nearest'})`,首尾钳制(输入焦点时不触发)。
3. **Enter** → 切换聚焦行 `.appr`(mark ✓/· 互切)。
4. **E** → 聚焦行进入编辑:`.dst-text` 替换为 `input.syn`(值=原文,聚焦全选),行加 `.dirty`;已在编辑态则仅聚焦。
5. **pb-ok / pb-rej**(editbar 按钮)→ 行 `.appr`(mark ✓)/ 去 appr + mark ✗(danger)。
6. **⌘S** → toast「已暂存 · 128/486 行」(preventDefault)。
7. 进度环节数据:26% 与「128/486」为示例;真实实现按 appr 行数/总行数实时计算(pb-foot 主按钮文案也应同步)。

## 状态完备(迁移时补)

全部行已校对时主按钮文案与写回流;驳回行必填修改意见(当前驳回无理由录入);术语锁定行的编辑约束(锁定词不可改,应给出可视限制);长行溢出与滚动锚定(focus 行保持可见已实现);双语对照写回预览;自动暂存(⌘S 之外)。
