# Screen 10 · 请求模版(`data-screen="request-template"`)

下钻屏。单服务的 System Prompt 模版编辑:变量插入、请求体预览、**试译预览**(PM 分析 P0 三洞之一「试译预览」的落点)。

- **入口**:设置 → 翻译服务 → OpenAI 卡「请求模版」/ ⌘K「请求模版」
- **顶栏**:请求模版 · openai · gpt-4o-mini · prompt 编辑与试译预览 · CTA「保存模版」
- **布局**:pb-head(固定)→ 左右网格 1.1fr / 1fr(手机单列);左 = 模版编辑卡(textarea 自身滚动),右 = 请求预览卡 + 试译卡(右列首卡滚动)。

## 元素清单

### 1. 页头(`.pb-head`)

| 元素 | 内容/行为 |
|---|---|
| 「← 翻译服务」 | 返回 settings |
| 标题块 | 「请求模版 · OpenAI」+ mono 副行「gpt-4o-mini · temperature 0.2 · 批 128 行 · 术语表注入开」 |
| 「重置为默认」 | textarea 恢复初始模版 + toast「已重置为默认模版」 |
| 「保存模版」(primary) | `tplSave()`:toast「模版已保存 · 新批次起生效」(与顶栏 CTA 同一函数) |

### 2. 模版编辑卡(左列)

- 卡头:「System Prompt」+ meta「4 个变量可用」
- `textarea`(spellcheck=false,aria-label「System Prompt 模版」),初始模版:

```
You are a professional subtitle translator.
Translate {sourceLang} to {targetLang}.
One line in, one line out — never merge or split cues.
Preserve names via glossary: {glossary}
Previous-line context:
{context}
```

- **变量条 `.vars`**:`{sourceLang}` `{targetLang}` `{glossary}` `{context}` 四 chip + 「＋ 变量」(点击开菜单,选中项**插入 textarea 光标/选区处**并恢复光标位置)
- **提示卡**:「**断点续传** · 模版变更只影响新批次;进行中的任务按创建时的模版执行完毕。」

### 3. 请求预览卡(右上)

卡头 meta mono:`POST /v1/chat/completions`。`pre.json` 语法高亮体:

```json
{ "model": "gpt-4o-mini", "temperature": 0.2,
  "messages": [
    { "role": "system", "content": "…模版渲染后…" },
    { "role": "user", "content": "[128 行字幕块]" } ] }
```

k=info / s=success / n=warning 三色。迁移时:应由当前模版 + 参数**实时渲染**。

### 4. 试译预览卡(右下)

卡头 meta:「单行 · 走真实请求」。

| 元素 | 内容 |
|---|---|
| 输入框 `input.en` | 初值「The Shōgun commands the Anjin to sail west.」 |
| 「试译 ▸」按钮(primary) | 重跑:meta 刷新为「798 ms · 17 tokens · $0.0001」(模拟延迟变化) |
| meta `#try-meta` | mono 11px「812 ms · 17 tokens · $0.0001」(耗时/tokens/单次成本) |
| 输出框 | 「将军命令按针向西航行。」+ `.gloss`「Anjin → 按针」(术语命中展示) |

## 交互逻辑

1. 「＋ 变量」菜单 → 插入光标处(记录 selectionStart/End,插入后光标落在变量尾)。
2. 试译按钮 → 刷新 meta(原型模拟;迁移时调真实翻译接口,含 loading 态)。
3. 重置/保存见页头;保存语义 =「新批次起生效」(断点续传约束在提示卡说明)。

## 状态完备(迁移时补)

试译进行中 loading / 失败态(当前仅成功路径);模版语法校验(未闭合变量提示);未保存离开的确认;非 AI 服务(DeepL 等)的模版形态(当前仅 OpenAI 示例);参数区(temperature/批大小)与模版同页编辑。
