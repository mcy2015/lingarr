# 设计 Token(原型 → 代码的映射基准)

来源:原型 `<style>` 的 `:root` 变量与全局样式。语义命名遵循 docs/DESIGN.md「Timeline 剪辑台」契约。迁移时这批值应进 `tailwind.config` / CSS variables,组件只允许引用 token,不允许字面色值。

## 色彩

三层中性底由深到浅构成纵深:`primary`(页面底)→ `secondary`(卡片/侧栏)→ `tertiary`(悬浮/输入/chip 底)。

| Token | 值 | 用途 |
|---|---|---|
| `--primary` | `#151619` | 页面背景、输入框底、代码块底 |
| `--primary-content` | `#D8DBDF` | 主文字(标题、正文强调) |
| `--secondary` | `#1C1E22` | 卡片、侧栏、toast、菜单底 |
| `--secondary-content` | `#C7CCD2` | 次要文字(meta、hint、副标题) |
| `--tertiary` | `#22252A` | hover 态底、输入/checkbox 底、nav 选中底、时间码栏底 |
| `--tertiary-content` | `#C7CCD2` | 三级文字(与 secondary-content 同值) |
| `--line` | `#2E3238` | 全部边框与分隔线 |
| `--accent` | `#E8833A` | 强调色:主按钮、进度填充、播放头、选中态、焦点环 |
| `--accent-content` | `#20130A` | 强调底上的文字 |
| `--success` | `#6FBF73` | 完成 / 在线 / 通过(`--success-content` `#0C1A0E`) |
| `--warning` | `#E5B567` | 限流 / 降级 / 待处理(`--warning-content` `#1C1408`) |
| `--danger` | `#E25C4A` | 失败 / 驳回 / 错误(`--danger-content` `#1F0D09`) |
| `--info` | `#5B9BD5` | 进行中 / INFO 日志 / 成本序列(`--info-content` `#0D1826`) |
| `--track-a` | `#5B9BD5`(= info) | 校对台源轨(EN)标识色条 |
| `--track-b` | `#E8833A`(= accent) | 校对台译轨(ZH)标识色条 |

派生色用法(组件内 `color-mix`,不是新 token):
- 半透明状态色边框:`color-mix(in srgb, var(--success|accent|info|warning|danger) 40%~55%, var(--line))` — chip.ok/.part/.busy、badge.show/.enc、danger chip
- 悬浮行底:`color-mix(in srgb, var(--tertiary) 55%, transparent)` — 表格 tbody tr:hover
- 已编辑行底:`color-mix(in srgb, var(--accent) 7%, transparent)` — pline.dirty
- 驳回删除线:`color-mix(in srgb, var(--danger) 60%, transparent)` — pline.rej
- 滚动条拇指 26% / 悬停 55%:`color-mix(in srgb, var(--secondary-content) N%, transparent)`
- 遮罩:`color-mix(in srgb, var(--primary) 72%, transparent)` — 模态背景

## 字体

| Token | 值 | 用途 |
|---|---|---|
| `--font-ui` | `"Archivo", -apple-system, "SF Pro Text", "PingFang SC", "Microsoft YaHei", "Noto Sans SC", sans-serif` | 全局 UI |
| `--font-mono` | `"JetBrains Mono", ui-monospace, "SF Mono", Menlo, Consolas, monospace` | `.mono`:数字、时间码、文件名、cron、日志、chip、KPI 大数 |

- `.mono` 同时设 `font-variant-numeric: tabular-nums`(等宽数字,进度/时间对齐的关键)
- 正文基准:`400 14px/1.55 var(--font-ui)`,`-webkit-font-smoothing: antialiased`

## 字号阶梯(原型实测量)

| 值 | 使用位置 |
|---|---|
| 24px | KPI 大数(`.kpi .n`) |
| 22px | 管线节点数值(`.pipe-node .val`) |
| 19px | 顶栏 H1 |
| 17px | 侧栏 Logo(手机模式 H1 降 16px) |
| 16px | qd-stat 大数(手机模式 KPI 降 20px) |
| 14px | 正文基准、pcard 标题 |
| 13px | 卡片 H2、表格 td、btn、tabs、菜单项 |
| 12.5px | btn.sm、topbar.sub、toast、prov 行、feed 行 |
| 12px | meta、hint、lib-count、th、路径条 |
| 11.5px | 副信息行(qrow .t span、hist-row span)、pb 副标题、logline、json |
| 11px | chip、badge、nav cnt、日志级别、json 变量 |
| 10.5px | 时间码(.tc)、gloss、pcard tag、mnav 标签、keys |
| 9px | 手机模式 mnav cnt 徽标 |

字重体系:400 正文 / 500 th / 600 强调(b、h2、日志 lv)/ 700(H1、Logo、pcard tag)。

## 圆角

| 值 | 使用位置 |
|---|---|
| 8px | 模态框(.modal-box) |
| 6px | card、drop、tray、pcard、pb-foot、qd-stat、qdep、menu、toast |
| 4px | btn、seg、search、input(input.syn/en)、nav 选中右侧、pager 按钮 |
| 3px | chip、badge、proto key、keys.key、gloss、trend bar、checkbox |
| 2px | 焦点环圆角、legend 色块 |
| 9px | switch 轨道、nav cnt 胶囊 |
| 50% | avatar、dot |

## 关键尺寸

- 侧栏 216px;≤900px 收窄为 58px 图标模式;手机模式隐藏侧栏改 56px 底栏
- 内容区 padding:22px 26px 40px(手机 14px 12px 18px)
- track 进度条:高 6px、播放头 2px×14px(任务详情大号 10px / 头 18px)
- switch:30×17px、钮 11px
- checkbox:15×15px
- pline 网格列:172px(时间码)/ 1fr(源)/ 1fr(译文)/ 34px(标记);手机单列
- qrow 网格列:3px rail / 1.4fr(标题)/ 172px(服务 chip)/ 1fr(进度)/ 90px(eta)/ 92px(操作)
- jobrow 网格列:1fr(标题)/ 130px(进度)/ 74px(百分比)

## 布局间距

- 卡片内边距:12px 16px(标准行)、14px 16px(pcard)、16px(详情卡)
- 网格 gap:10px(kpis/qd-stats)、12px(set-grid/卡间距)、14px(cols/import-grid/tpl-grid)
- 卡片垂直间距:12px / 14px(margin-top)

## 交互态

- **焦点**:`outline: 2px solid var(--accent); outline-offset: 2px; border-radius: 2px` — 全部可聚焦元素(button/a/input/[tabindex] 的 `:focus-visible`)
- **hover**:btn → tertiary 底;nav → tertiary;表格行 → tertiary 55%;pipe-node/kpi → accent 边框;pcard.add → accent 虚线边;menu 项 → tertiary;⌘K 结果项 → tertiary;滚动条拇指提亮
- **选中态**:nav `[aria-current="true"]` → tertiary 底 + 左侧 2px accent 边;seg `[aria-pressed="true"]` / tabs `[aria-selected="true"]` → tertiary 底 / accent 下边线
- **动效**(均尊重 `prefers-reduced-motion`):logo 光标 blink 1.2s steps(1) infinite;toast 进入 `tin .18s ease`(上移 6px 淡入)、退出 opacity .3s

## 滚动条(纳入设计系统)

- 8px 细条、拇指 `secondary-content` 26% 透明、圆角 4px
- 悬停提亮至 55%;容器 hover 时提亮至 48%
- 双内核:WebKit/Blink 用 `::-webkit-scrollbar` 伪元素全套;Firefox 用 `scrollbar-width: thin` + `scrollbar-color`(以 `@supports` 隔离,标准属性不得出现在 webkit 继承链上,否则伪元素被禁用)
- `scrollbar-gutter: stable`:screens、qcol、#plines、tpl-editor textarea、settings panel —— 滚动出现/消失时布局不跳
- 手机模式 tabs 与 pline 时间码栏隐藏滚动条

## 断点

| 断点 | 行为 |
|---|---|
| ≤1080px | kpis 降 2 列、cols/tpl-grid 单列、pipe 换行(隐藏箭头)、qd-stats 3 列、qdepth 2 列 |
| ≤900px | 侧栏收窄 58px(只留图标)、qrow 收成 3 列(隐藏 eta/操作)、pline 收成 2 列(隐藏译文与标记) |
| ≤760px(`body.mobile`) | 手机模式:侧栏隐藏、56px 底部导航、单列布局、表格行操作简化、toast 通栏;由媒体查询自动或 proto-bar 手动触发 |

## 视口铺满策略(与滚动配套)

外层 `.shell` 占满视口(`height: calc(100vh - 33px)`);顶栏/工具栏/tab 为固定区不滚动,主内容区 flex 拉伸,滚动收进各屏的内部容器(库表卡、qcol、plines、import 两卡、settings panel、各下钻屏右卡)。逐屏容器见各屏文档「布局」节。
