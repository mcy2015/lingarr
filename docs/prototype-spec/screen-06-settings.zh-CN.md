# Screen 06 · 设置(`data-screen="settings"`)

主导航第 6 屏。四组 tab:翻译服务 / 媒体与自动化 / 字幕与质量 / 系统。**无顶栏 CTA**(自动保存模型,按钮隐藏)。滚动收进当前 panel。

- **入口**:侧栏「设置」/ 快捷键 `6` / 概览 KPI(成本)/ ⌘K(可带 data-panel 深链)/ 校对台术语回流(→quality)
- **顶栏**:设置 · 四组:服务 / 媒体与自动化 / 字幕与质量 / 系统

## tab 与深链

`data-p` = providers / sources / quality / system,与 `.panel[data-panel]` 对应切换。`setSettingsTab(name)` 供外部调用(⌘K 的 `data-panel`、校对台回流、媒体库无)。

## A. 翻译服务(providers)

说明行:「多服务并存,任务级可指定;卡片显示真实用量与健康。默认服务用于自动化批次。」

### 服务卡网格(`.set-grid`)

| 卡 | 状态 | model 行 | usage | 操作 |
|---|---|---|---|---|
| OpenAI(dot ok + tag「默认」) | 在线 | gpt-4o-mini · temperature 0.2 · 批 500 行 | 本月 $3.12 / 延迟 820 ms / 成功 99.4% | 测试连接(✓ 200 · 812 ms · 「你好」→「Hello」)/ 请求模版(→ request-template)/ 编辑 |
| DeepL | 在线 | api-free · 500k 字符/月 · 已用 312k | 本月 $0 / 310 ms / 100% | 测试连接(✓ 200 · 310 ms · 500k 配额余 188k 字符)/ **设为默认** / 编辑 |
| Gemini(dot warn) | 限流中 | 2.5-flash · 429 ×3(近 1 小时) | 本月 $0.44 / 1.1 s / 91.2% | 测试连接(**✗ 429 · 限流中 · 窗口 42 分钟后重置**)/ 编辑 |
| Ollama 本地 | 在线 | qwen3:14b · http://localhost:11434 | 本月 $0 / 2.4 s / 98.1% | 测试连接(✓ 200 · 2.4 s · qwen3:14b 就绪 · 14.2 GB 显存)/ 编辑 |
| LibreTranslate(`.off` 55%) | 未连接 | 未配置 URL | 本月 — | 配置 |
| ＋添加服务(`.add` 虚线卡) | — | 「内置 10 种 · 或挂载插件」 | — | 点击开菜单 |

每卡底部 `.test-res`:点击「测试连接」后显示内联结果(ok 绿 / err 红)。这是 PM 分析的 P0 能力之一。

**交互**:
1. **测试连接** — 结果写入本卡 `.test-res`(data-test/data-ok 属性驱动;迁移时换真实 API 探测)。
2. **编辑**(表单模态)— 字段:API Key(password)/ 模型 / temperature / 批大小(行);保存 toast「xx 配置已保存」。
3. **设为默认** — 清除全部卡的「默认」tag → 本卡加 tag;回退链中同名行提到首位 + 重排序号;toast「默认服务已切换为 xx · 回退链同步调整」。
4. **配置**(LibreTranslate,表单:服务 URL)— 保存后卡复活:移除 `.off`、dot→warn、状态「已配置 · 待测试」、model 写入 URL、按钮变「测试连接」并绑定。
5. **＋添加服务**(菜单)— 列内置服务(DeepSeek/Mistral/xAI/Yandex/Bing 翻译/Google 翻译/Microsoft 翻译/LocalAI,**排除已配置**);选中 → 插入 `.off` 新卡(「未配置」+ 配置按钮);全部加完 → toast「内置服务已全部添加」。
6. 新卡「配置」(表单:API Key / 服务 URL)— 填写保存 → 卡激活(dot ok、在线、model=值);留空 → toast「未填写 · 保持未配置」。

### 回退顺序卡

卡头 meta:「主服务失败自动按序降级 · 降级过程在任务详情时间线可见」。

`.fb-row` ×3(OpenAI 主服务 · gpt-4o-mini / DeepL api-free / Ollama qwen3:14b · $0)+ 末行「＋ 添加服务到回退链」。

每行:`idx` 序号(renum 维护)/ dot / 服务名 / 角色 chip / **langcap chip** / 操作(↑ 上移 / ↓ 下移 / 移除)。

**langcap chip 三态**(title 提示规则):
| 文案 | 含义 | 行为 |
|---|---|---|
| 未受限 | AI 服务无语言表 · 不做语言门控,配 restrictions 后可受限 | 可编辑 |
| 全语言 | 语言表经 API 查证 · 派生只读 | 点击仅 toast「派生只读」 |
| 仅 EN → ZH | 受限语言对(provider_language_restrictions) | 可编辑 |

**交互**:↑↓ 移动行(边界钳制)+ renum;移除 → 行删 + renum + toast;「＋添加服务到回退链」菜单 = 已配置但不在链上的服务(无候选 → toast「所有已配置服务都已在链上」),插入行(「回退位」chip + 未受限 langcap)。
**langcap 编辑**(表单:源语言 / 目标语言,逗号分隔):两格都空 = 清除限制(→「未受限」);只填一格 → toast「要么两格都填,要么都留空」;正确 → 「仅 S → T」(part 色)+ `syncLangCoverage()` 重算 + toast「回退资格即时生效」。

### 派生:链上语言覆盖

`syncLangCoverage()` 解析链上全部成员能力,对语言对 **EN→ZH、JA→ZH** 各统计可用服务数,回写 quality 面板「链上语言覆盖」行:
- 输出示例:`EN→ZH 3/3 · JA→ZH 3/3`;有缺口 → 前缀 ⚠ + warning 色 + 不支持名单,如 `EN→ZH 2/3(Gemini 不支持)`。
- 链为空 → 「链上无服务」(warning)。**回退链任何增删/移位(renum 被包装)都会触发重算。**

## B. 媒体与自动化(sources)

### 媒体源卡(meta:「Sonarr / Radarr · webhook 实时 + 轮询兜底」)

| 行 | 状态 | hint | 操作 |
|---|---|---|---|
| Sonarr | ok | `http://sonarr:8989 · 46 剧集已同步` | 重新同步 / 编辑 |
| Radarr | ok | `http://radarr:7878 · 312 电影已同步` | 重新同步 / 编辑 |
| Jellyfin | — | 「webhook 直连,补齐 *arr 之外的媒体服务器」+ chip「规划中 P3-e」 | 配置(**disabled**) |

重新同步 → toast「xx 同步已触发 · 完成后媒体库自动刷新」;编辑(表单:地址 mono + API Key password)→ toast「配置已保存」。

### 自动化批次卡(meta mono:「cron」)

| 行 | hint | 控件 |
|---|---|---|
| 每日补缺 | `0 4 * * * · 限额 50 任务/次 · 延迟新片 48h` | 编辑 + switch(on) |
| 每周全库巡检 | `0 5 * * 0 · 只检不翻 · 生成缺字幕报告` | 编辑 + switch(off) |
| ＋新增批次 | — | 点击开表单 |

**编辑表单**(参数与开关解耦):每日补缺 = cron + 单次限额(任务/次)+ 新片延迟(h),校验 cron 必填 / 限额正整数 / 延迟非负整数(失败 toast);巡检 = 仅 cron;保存回写 hint + toast(批次未启用时附「批次未启用」)。
**新增批次**(表单:名称 + cron,均必填)→ 插入行(默认未启用,switch 可切)+ 绑定编辑逻辑 + toast。

## C. 字幕与质量(quality)

### 字幕输出卡

| 行 | hint | 控件 |
|---|---|---|
| 双语输出 | 译文与原文同行输出 | switch(off) |
| 文件命名 | `{原名}.{语言}.srt · 与媒体同目录写回` | 编辑(表单:命名模板 mono) |
| 格式 | `SRT ✓ · SSA ✓ · VTT`+chip「规划中 P3-f」 | —(只读) |

### 术语表卡(卡头:tag「新增 · P3-a」;meta:「全局 24 条 · 按条目覆盖 6 条」)

| 行 | hint | 操作 |
|---|---|---|
| `Ye Wenjie`(mono) | → 叶文洁 · 锁定(校对台同步) | 编辑 |
| `Wallfacer`(mono) | → 面壁者 · 上下文匹配 | 编辑 |
| ＋添加术语 · 支持 CSV 导入 | — | 点击开表单 |

编辑(表单:源术语 / 译名 / 匹配模式 select[锁定,上下文匹配]);添加(同表单,源+译必填)→ 插入行 + 卡头计数 +1 + toast「术语已添加:x → y」。

### 语言策略卡(meta:「翻译管线的语言范围 · 缺字幕统计与自动化批次均遵循 · 每服务能力见『回退顺序』卡」)

| 行 | hint | 控件 |
|---|---|---|
| 目标语言 | chips:`ZH ✓`(ok)+ `＋ 追加`(dim) | 编辑(表单:逗号分隔 ISO 码,保存回写 chip) |
| 源语言白名单 | `EN · JA` | 编辑(逗号分隔) |
| 链上语言覆盖 | **派生只读**,title 说明「派生值:回退链成员语言能力 ∩(源白名单 × 目标语言),随回退顺序卡增删实时重算」 | — |

## D. 系统(system)

卡头 meta mono:`v1.4.0 · sqlite · /app/config`。

| 行 | dot | hint | 右侧 |
|---|---|---|---|
| 认证 | ok | 单用户 henry · PBKDF2 · Cookie + API Key(webhook) | chip ok「API Key 已发放」 |
| 日志 | ok | `Information · 环形缓冲 2 000 条` | 「打开日志」→ logs |
| 定时任务 | ok | `Hangfire · 6 队列 · 并发 2` | 「面板」→ schedule |

## 状态完备(迁移时补)

API Key 变更/吊销流;服务删除(仅回退链移除,卡片无删除);Jellyfin(P3-e)与 VTT(P3-f)启用后从规划态转正式;「＋追加」目标语言的加语言交互;CSV 导入术语;设置变更的脏态与自动保存反馈(当前全部即时 toast,无 diff 提示)。
