# 组件清单(细胞级重构的「细胞」)

原型以 CSS class 体系构成组件库。每个条目 = 迁移时的一个候选组件/组合件,含结构、变体状态、出现位置、交互。组件按职能分五组;「类名」列是原型锚点。

## 一、布局骨架

### AppShell — `.shell` / `.side` / `.main`
左右分栏:侧栏 216px + 主区。`shell` 高 `calc(100vh - 33px)`、min-height 560px。主区纵向:topbar(固定)+ screens(拉伸)。手机模式侧栏隐藏、底部加 `.mnav`。

### SideNav — `.nav button` + `.cnt`
6 项主导航,结构:`svg.ico + span.lbl + span.cnt`。状态:hover(tertiary 底)、`aria-current="true"`(tertiary 底 + 左 2px accent 边)。`.cnt` 数字徽标(胶囊,mono 11px);`.cnt.hot` 反白 accent 底(队列 14)。≤900px 只显示图标。点击 → `go(screen)` + 写 hash。

### MobileNav — `.mnav button`
手机模式底栏 6 项(与主导航同 data-go,标签更短:「队列」「导入」「校对」),`aria-current` 高亮 accent,角标 cnt(9px)。默认 `display:none`,仅 `body.mobile` 显示。

### Topbar — `.topbar`(`#pg-title` `#pg-sub` `#pg-cta`)
页头:H1 19px + 副标题 12.5px + 右侧动作区(`#pg-search` ⌘K 按钮 + `#pg-cta` 主按钮)。内容随屏幕由 `go()` 动态写入(文案表见 README),settings 屏隐藏 CTA。手机模式隐藏副标题与搜索按钮。

### 屏容器 — `.screens` / `.screen.on`
屏切换器:`.screen` 默认 none,`.on` 显示且 flex 纵向拉伸(视口铺满)。每屏内部再划分固定区/滚动区(详见各屏文档)。

## 二、通用元件

### Card — `.card` + `.card-h` + `h2` + `.meta`
标准卡容器(secondary 底 + line 边 + 6px 圆角)。`card-h` 卡头:13px H2 + 右侧 meta(12px,secondary-content)。正文区按内容自定义。**全屏通用**。

### Btn — `.btn` / `.btn.primary` / `.btn.sm`
按钮:透明底 line 边 4px 圆角;`.primary` accent 反白加粗(hover 提亮 6%);`.sm` 紧凑变体(3px 9px,12px 字)。禁用态:原生 disabled(pathpick 扫描路径、Jellyfin 配置)。**全屏通用**。

### Chip — `.chip`(.ok / .part / .busy / .dim)
mono 小标签(11px,tertiary 底 line 边 3px 圆角)。语义色变体:`ok` success、`part` accent、`busy` info、`dim` 55% 透明。行为变体(原型中以附加 class 标记,迁移时应成为组件 props):
- `.act-toggle` — 点击循环切换动作(导入表「动作」列,映射表见 screen-04)
- `.drop-chip` — 点击开下拉选项菜单(简繁/写回/冲突)
- `.langcap` — 回退链语言能力,点击开编辑表单;`title` 说明派生规则
- 日志级别过滤 chip — `aria-pressed` 切换 + accent 选中态

### Badge — `.badge`(.show / .movie / .enc / .fmt)
类型徽章(11px,与 chip 区别:非 mono 默认)。`.show` info 色边、`.movie` 中性、`.enc` warning(编码转换提示)、`.fmt` mono 10.5px。

### Dot — `.dot`(.ok / .warn / .err / .off)
7px 状态圆点:success / warning / danger / line。

### Seg — `.seg` + `button[aria-pressed]`
分段单选(媒体库类型过滤、媒体详情季切换),4px 圆角连体按钮组,选中 tertiary 底。

### Search — `.search` + `svg.ico` + `input`
搜索输入组合(secondary 底 line 边 + 放大镜图标 + 无边框 input),min-width 230px。手机模式占满一行。

### Tabs — `.tabs` + `button[aria-selected]` + `.n`
下划线式 tab(队列过滤 / 导入模式 / 设置分组),选中:主文字色 + 2px accent 底边(负 margin 压住分隔线)。`.n` 计数(mono 11px,队列 tab 用,JS 同步)。手机模式横向滚动、隐藏滚动条。

### Switch — `.switch`(.on)`[role=switch]`
开关:30×17 轨道 + 11px 圆钮。`.on` accent 反白。原型中所有 switch 共用一套点击切换逻辑。tabindex=0 可键盘聚焦。

### Track — `.track` + `.fill` + `.head` ⭐签名组件
进度轨道:6px 高 tertiary 轨 + accent 填充(`.fill` 宽度 %) + 2px×14px 播放头(`.head`,left %)。失败态/done 态用内联 style 覆盖填充色(danger/success)。任务详情大号变体 10px 高。出现在:概览活跃任务、队列行、校对进度、媒体详情历史、导入转写中、任务详情主进度。

## 三、数据展示

### Table — `table` / `th` / `td` + `.tscroll`
标准表格:th 12px 500 secondary-content、td 13px、行 hover tertiary 55%。`.tscroll` 变体:卡内滚动 + `thead th` sticky(sticky top 0 secondary 底)——导入两表用。媒体库表额外横向滚动。

### Pipe 管线图 — `.pipe` + `.pipe-node` + `.pipe-link`
概览签名图:5 节点横排 + 箭头连接线(26px)。节点:label 11.5px + mono 22px 数值 + small 单位;`.hot` 变体 accent 边 + 数值 accent(翻译队列节点)。可点击(hover accent 边,cursor pointer)。≤1080px 换行、隐藏箭头。

### KPI — `.kpi`(.warn)
统计卡:mono 24px 数值 + 12px 标签;`.warn` 数值 warning 色。4 列网格(响应降 2 列)。可点击跳转。

### JobRow — `.jobrow`
活跃任务行,网格 1fr/130px/74px:标题+副行(b 13px ellipsis + span 11.5px)/ track / 百分比剩余。cursor pointer,整行点击 → queue-detail(按钮冒泡排除)。

### QRow — `.qrow`(.run/.wait/.fail/.done/.paused)
队列行,六列网格(3px rail / 标题 / 服务 chip / track / eta / 操作)。`.rail` 3×34px 状态色条:run=accent、fail=danger、done=success、wait=line、paused=warning。操作列按状态给:run=⏸✕、wait=✕、fail=重试+日志、done=校对。整行可点 → queue-detail。手机模式收 3 列(隐藏 chip 与操作,track 跨列)。

### QCol — `.qcol`
队列滚动容器(视口铺满策略:库页滚动收进此容器)。

### PLine — `.pline`(.appr/.rej/.focus/.dirty)⭐签名组件
校对行,四栏:`.tc` 时间码(mono 10.5px,tertiary 底,含 `#idx`)/ `.src` 源文(左 2px track-a 色条)/ `.dst` 译文(左 2px track-b 色条)/ `.mark` 状态标记(34px)。状态类:
- `.appr` 已通过 — mark ✓ success
- `.rej` 已驳回 — mark ✗ danger + 译文删除线(60% danger)
- `.focus` 当前行 — accent 边框,译文区 cursor:text
- `.dirty` 已修改未保存 — 译文区 accent 7% 底
内部可含:`.dst-text` 文本态、`input.syn` 编辑态、`.editbar`(✓通过/✗驳回/术语锁定提示/「已修改,未保存」)、`.gloss` 术语标注、源文 `del.w`/`ins.w` 修订痕迹(danger 删除线/success)。tabindex=0,点击聚焦。

### Gloss — `.gloss`
术语标注:10.5px mono,warning 虚线边框胶囊(如 `黑暗森林 → Dark Forest`),出现在译文后与试译输出。

### HistRow — `.hist-row`
翻译历史行:标题+副行 / 服务 chip / 迷你 track(110px)或完成状态 / 详情按钮。

### TLRow — `.tl-row`
时间线行:58px time / 14px 色点(`.d` ok/warn/err/acc/dim)/ 事件文案(b 强调 + `.m` meta)。

### FBChain / FBRow — `.fb-chain` / `.fb-row`
执行链(任务详情,gemini ✗ → openai ▸ → ollama)与回退链(设置):idx 序号 / dot / 服务名 / 能力 chip(langcap)/ 操作(↑↓移除)。`.arr` 箭头分隔。

### QDStat — `.qd-stat`
任务详情统计块(primary 底!与其他卡区分):mono 16px 数值(small 单位)+ 11.5px 标签。

### LogLine — `.logline`(.warnrow/.errrow)
日志行,三列 70px/46px/1fr:mono time / 级别(INF info 色、WRN warning、ERR danger)/ 单行省略消息。warnrow/errrow 整行消息着色。

### QDep — `.qdep`
队列深度卡:mono 18px 数值 + 11.5px 标签,3 列网格。

### Trend — `.trend` + `.grp` + `.bar.a/.b`
双序列微柱状图(概览近 14 天):72px 高,日组双柱(accent=写回条数、info 75%=成本),各自归一,2px 间隙。配 `.legend` 图例(i 色块 + 说明)。

### Feed — `.feed`
动态流行:mono time 11px + 状态符(ok ✓/acc ▸/err ✗/warn △)+ 文案(b.mono 强调文件名/数字)。

### Prov — `.prov`
服务健康行:dot + 名称 + 右侧延迟/费用(mono 11.5px)。

### JSON 预览 — `pre.json`(`.k`/`.s`/`.n`)
请求预览代码块:primary 底 mono 11.5px,语法高亮 k=info / s=success / n=warning,横向滚动。

## 四、输入与选择

### Drop — `.drop`(`role=button` `tabindex=0`)
拖放区:1.5px accent 55% 虚线边、居中文案 + `.fmt` 格式说明(mono 11px,`.off` 50% 透明标注规划格式)。两种实例:自由导入(34px 内边距)/ 整季(20px)。支持 click(唤起 file input)与 drop(dragover preventDefault)。

### Pathpick — `.pathpick`
路径条组合:mono 路径胶囊 + 「扫描路径」按钮(disabled)+ 规划中 chip。B3-A7 预留位。

### Checkbox — `input[type=checkbox]`
自定义外观:15px 方框 tertiary 底,checked = accent 反白 ✓。媒体库行选择用。

### SynInput — `input.syn`
校对译文编辑框:primary 底 line 边 4px 圆角,聚焦 accent 边。由 E 键或 editbar 进入,替换 `.dst-text`。

### Tryout — `.tryout` + `input.en`
试译预览组合:输入框(同 syn 样式)+ 试译按钮 + meta(mono 11px 耗时/tokens/成本)+ 输出框(primary 底 line 边,可含 .gloss)。

### Vars — `.vars` + `.chip`
模版变量插入条:可用变量 chips + 「＋ 变量」(点击开菜单,选中项插入 textarea 光标处)。

### Keys — `.keys` + `.key`
快捷键提示组:`.key` 键帽(mono 10.5px,tertiary 底 line 边)+ 说明。手机模式隐藏。

### Pager — `.pager`
分页:页码文字 + ‹ › 按钮(1–129 钳制)。

### Tray — `.tray`(.on)⭐
批量操作托盘:sticky bottom,accent 边,勾选 >0 时 `.on` 显示。内容:已选 N 项(accent mono)/ 主操作(翻译所选)/ 加入队列末尾 / 清除已选 / 右侧预估成本。手机模式可换行。

### PBHead — `.pb-head`
下钻屏统一页头:返回按钮(← xxx)+ 标题块(15px b + mono 11.5px 副行)+ flex 间隔 + 右侧动作组(1–3 个按钮)。可含 `.prog`(校对进度:track + 百分比)与 `.legend`。

### PCard — `.pcard`(.off / .add)
翻译服务卡:head(dot + 名称 + 默认 tag + 状态)/ model 行(mono)/ usage 行(三项,数值 mono)/ ops 按钮组 / `.test-res` 内联测试结果(ok 绿 / err 红)。`.off` 未配置 55% 透明;`.add` 虚线占位卡(hover accent)。

### SetList — `.setlist` + `.row`
设置行列表:`.row` = 名称 b + hint(12px)/ `.end` 右侧(按钮 + switch)。末行常为「＋ 新增」虚位(row 通用样式,点击开表单)。

## 五、反馈与浮层

### Toast — `#toasts` + `.toast`(.out)
全局通知:右下角堆叠,secondary 底 + 左 2px accent 边,2.2s 后淡出、2.6s 移除。`aria-live="polite"`。手机模式贴底栏上沿通栏。

### Menu — `#menu`(单例)
下拉菜单:fixed 定位、视口钳制(不溢出屏幕)、菜单项带 `.ck` 勾选位、`.sep` 分隔线。外点关闭 + ESC 关闭。所有 drop-chip / CTA 菜单 / 添加服务菜单共用。

### Modal(⌘K)— `#cmdk` + `.modal-box` + `.mhead` + `.mres`
全局搜索模态:遮罩(primary 72%)+ 560px 盒。`.mhead` 搜索输入 + ESC 提示;`.mres` 分组结果(`.grp` 组标题 + `.item` 结果行:标题 + 右侧 sub)。输入即过滤(data-k 关键词),组无结果整组隐藏。点击项跳转并关模态;点遮罩 / ESC 关闭。

### FModal — `#fmodal`(.fmodal)
表单模态:430px 盒,`.frow` 字段行(label + input/select,password/number/mono 变体),底部取消/保存。保存回调收集全部值;ESC / 遮罩 / 取消关闭。所有「编辑 xxx」共用。

### ProtoBar — `.proto-bar`(非产品)
原型助手条:说明文案 + 手机模式开关 + 配套文档指引。迁移时删除。

## 状态与布尔约定(迁移要点)

原型大量行为依赖 class 切换,对应组件 props/state:

| class/属性 | 组件状态 | 载体 |
|---|---|---|
| `.on` | 容器可见(screen/panel/tray/modal/test-res) | 全局 |
| `aria-current` | 导航选中 | SideNav / MobileNav |
| `aria-pressed` | seg/chip/级别过滤选中 | Seg / Chip |
| `aria-selected` | tab 选中 | Tabs |
| `.hidden` | 行过滤隐藏(库行/队列行/⌘K 项/日志行) | 列表项 |
| `.appr / .rej / .focus / .dirty` | 校对行四态 | PLine |
| `.run / .wait / .fail / .done / .paused` | 任务状态 | QRow(rail 色) |
| `.off` | 未配置/禁用 55% 透明 | PCard / 库行(VTT) |
| `.hot` | 强调(队列徽标/管线节点) | cnt / pipe-node |
| `style.opacity<1` | 行不可处理(导入 free 表) | 自由导入行 |
