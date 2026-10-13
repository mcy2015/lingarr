# 全局交互系统(跨屏逻辑)

原型所有 JS 都在一个 `<script>` 内,按职责拆为:路由、顶栏 CTA、⌘K 搜索、四个通用 UI 原语(toast / menu / form modal / download)、手机模式、键盘系统。迁移时这套系统对应路由层 + 全局 store + 通用组件,各屏只订阅。

## 1. 屏幕路由(hash 路由)

- **切换函数 `go(name)`**:对全部 `.screen` 切 `.on`(匹配 data-screen)→ 同步侧栏与手机底栏 `aria-current` → 写入顶栏标题/副标题(titles 表)→ 写入 CTA 文案(ctas 表)与点击行为;settings 屏隐藏 CTA。原型对 `go` 做了一次包装(CTA 显示逻辑),迁移时应合并为单一实现。
- **入口**:侧栏/底栏按钮点击(`go` + 写 hash);`hashchange` 监听(支持前进后退/外链);初始化时读当前 hash 还原屏幕。
- **深链子路由**:hash 形如 `#import/season` —— 第一段为屏,第二段触发 `setImportTab('season')`(仅 import 屏支持)。
- **titles / ctas 表**:12 屏的顶栏文案见 README 状态表(迁移时应随路由参数动态生成,而非硬编码文案)。

## 2. 顶栏 CTA 系统(`#pg-cta`)

每屏一个主行动按钮,行为集中在一张 act 映射表:

| 屏 | CTA 动作 |
|---|---|
| overview | 跳 import |
| library | `ctaLibrary()`:若全库未勾选则自动勾第一行并同步托盘 → 开菜单「EN→ZH·简体(默认勾)/ 繁体」→ 执行对应托盘动作 |
| queue | 跳 library + toast「从媒体库勾选条目即可新建翻译任务」 |
| import | `runFreeImport()`:统计可处理行(剔除 opacity<1),空则 toast「处理队列为空」;否则 toast + 跳 queue |
| proofread | toast「已暂存 · 128/486 行」 |
| settings | null(按钮 display:none —— 自动保存模型) |
| media-detail | `mdTranslateMissing()`:全部未排队集入队(见 screen-07) |
| media-detail-movie | toast「翻译进行中 · 请求 #2418 · 38%」 |
| queue-detail | `qdTogglePause()`:暂停/恢复,同步屏内按钮与 CTA 文案 |
| request-template | `tplSave()` toast「模版已保存 · 新批次起生效」 |
| logs | `exportLogs()` 下载 txt |
| schedule | 开菜单:每日补缺 / 每周全库巡检 / 分隔线 / 自定义 cron…(表单:名称+cron → toast 已创建) |

注意:CTA 与屏内主按钮共享同一动作函数(library↔托盘、queue-detail↔屏内暂停、request-template↔保存),迁移时应为同一 store action。

## 3. ⌘K 全局搜索(`#cmdk`)

- **打开**:`⌘K/ctrl+K`(切换)、顶栏搜索按钮;打开时清空输入、过滤重置、聚焦。
- **关闭**:ESC、点遮罩、选中结果后。
- **索引结构**:三组——媒体(4 项)/ 任务(3 项)/ 设置(6 项)。每项 `data-k` 为关键词串(中英文别名,如 `shogun 幕府将军 剧集`、`#2417 shogun s01e05 进行中`),匹配 = data-k + 可见文本,小写包含。
- **过滤**:逐项 toggle `.hidden`;随后逐组检查,组内无可见项则隐藏组标题。
- **跳转**:`data-go-h` 目标屏 hash + `go()`;可带 `data-panel`(providers/quality)→ `setSettingsTab()` 切设置分组;关闭模态。
- 迁移时应改为全局搜索接口(媒体/任务/设置的查询),交互骨架不变。

## 4. 通用 UI 原语

### toast(msg)
`#toasts`(aria-live=polite)追加 `.toast`;2 200ms 加 `.out`(0.3s 淡出),2 600ms 移除。全局唯一入口,所有反馈走它。

### openMenu(anchor, items) — 下拉菜单
单例 `#menu`。items:`{label, checked?, pick?, sep?}`;渲染按钮 = 勾选位(`.ck`,选中显示 ✓)+ 文案;点击 → 关菜单 + 执行 pick。定位:锚点下方 6px,水平/垂直均做视口钳制(≥8px 边距)。关闭:再点击外部(document click 委托)、ESC。消费方:CTA 菜单、库过滤下拉、导入选项下拉、指派到集、添加服务、回退链添加、变量插入、目标语言/状态菜单。

### openForm(title, fields, onSave) — 表单模态
单例 `#fmodal`。fields:`{label, value?, type?('password'|'number'), options?[], mono?}` —— 有 options 渲染 select,否则 input;mono 用等宽字体。保存 → 收集全部值数组回调 onSave(校验在回调内,失败 toast 且不关—原型实现为关闭后 toast,迁移时应改为校验失败保持打开);自动聚焦首字段。关闭:取消/保存/ESC/遮罩。消费方:服务编辑/配置、媒体源编辑、自动化批次、术语、命名模板、语言策略、langcap 限制、自定义 cron、新增批次。

### download(name, text)
Blob + 临时 `<a download>` 触发下载,4s 后 revokeObjectURL。消费方:日志导出、校对快照导出。

## 5. 手机模式(`body.mobile`)

- **触发**:媒体查询 `(max-width: 760px)` 自动;proto-bar「📱 手机模式」手动预览(手动值 `mobileManual` 覆盖自动,再点还原跟随)。
- **生效内容**:侧栏隐藏 + `.mnav` 底栏出现;外壳居中限宽 430px;布局全部单列(见 tokens 断点节);topbar 隐藏副标题与搜索按钮;toast 通栏贴底栏上沿;校对行单列(时间码横滚);tabs 横滚。
- 迁移时:真机断点保留;手动预览开关是原型工具,不迁移。

## 6. 键盘系统(全表)

| 键 | 作用 | 生效条件 |
|---|---|---|
| `1`–`6` | 切换主导航屏 | 焦点不在 input/textarea |
| `⌘K / ctrl+K` | 开/关全局搜索(preventDefault) | 全局 |
| `ESC` | 关搜索 / 关菜单 / 关表单模态 | 对应浮层打开时 |
| `J` / `K` | 校对台下/上一行(聚焦 + scrollIntoView nearest,首尾钳制) | 校对台,非输入焦点 |
| `Enter` | 切换当前校对行通过态(appr ↔ 待定) | 校对台,非输入焦点 |
| `E` | 编辑聚焦行译文(文本态→input.syn,聚焦全选;已在编辑态则聚焦) | 校对台,非 input/textarea/select |
| `⌘S / ctrl+S` | 校对台暂存(preventDefault + toast) | 校对台 |

## 7. 可访问性模式清单

- 焦点环:统一 `:focus-visible` accent 外框(tokens「交互态」)
- 语义:nav `aria-label="主导航"`、tablist/tab + `aria-selected`、switch `role=switch` + `aria-checked`、drop `role=button` + tabindex、模态 `role=dialog` + `aria-label`、图表 `role=img` + `aria-label`(管线图/趋势图)
- 行选择 checkbox 带 `aria-label`(「选择 Shōgun」)
- toast 容器 `aria-live="polite"`
- 分页按钮 `aria-label`(上一页/下一页)
- `prefers-reduced-motion` 下停用 logo 闪烁

## 8. 视口铺满与滚动分工(总览)

原则:外层不滚,滚动收进屏内主内容容器;固定区(工具栏/tab/表头/操作条)始终可见。逐屏:

| 屏 | 固定区 | 滚动容器 |
|---|---|---|
| overview | 无工具区 | `.screens` 整体(cols 拉伸) |
| library | toolbar + tray(sticky)+ pager | 库表卡(overflow auto,表头 sticky 由 `.tscroll` 规则作用于导入表;库表为整体滚动) |
| queue | tabs | `.qcol` |
| import | tabs + 卡底选项条 | 两卡各自 `.tscroll`(表头 sticky) |
| proofread | pb-head + pb-foot(sticky) | `#plines` |
| settings | tabs | 当前 `.panel.on` |
| media-detail / movie | pb-head + toolbar | 左右卡各自 |
| queue-detail | pb-head + 统计卡 | 时间线卡 |
| logs | pb-head(过滤/自动滚动) | 日志卡 |
| schedule | pb-head + qdepth | 任务表卡 |
| request-template | pb-head | 右列请求预览卡 / 左 textarea 自身 |

`scrollbar-gutter: stable` 应用清单见 tokens。
