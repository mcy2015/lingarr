# 原型规格总览(prototype-spec)

本目录是 `design-previews/refactor-prototype.html`(2 491 行单文件原型)的完整元素与逻辑拆解,是「细胞级重构」的施工图:每个屏、每个元素、每个交互行为都登记在册,迁移到 Vue 组件时以此为验收依据。

配套文档:
- `docs/DESIGN.md` — 「Timeline 剪辑台」视觉契约(token 的源头定义)
- `docs/redesign-proposal.zh-CN.md` — 重设计提案(为什么这样设计)
- `docs/refactoring-status.zh-CN.md` — 重构执行状态

## 文档结构

| 文档 | 内容 |
|---|---|
| [tokens](tokens.zh-CN.md) | 色彩 / 字体 / 字号 / 圆角 / 间距 / 焦点 / 滚动条 / 断点全部设计 token |
| [components](components.zh-CN.md) | 复用组件清单(细胞):结构、变体、状态、出现位置、交互 |
| [global-interactions](global-interactions.zh-CN.md) | 跨屏系统:路由、顶栏 CTA、⌘K 搜索、toast、下拉菜单、表单模态、手机模式、键盘快捷键 |
| screen-01 ~ screen-12 | 逐屏规格:布局、元素清单、状态、交互逻辑、跳转关系 |

## 屏地图

主导航 6 屏(侧栏 / 手机底栏,快捷键 1–6):

| # | data-screen | 屏名 | 文档 |
|---|---|---|---|
| 1 | `overview` | 概览 | [screen-01](screen-01-overview.zh-CN.md) |
| 2 | `library` | 媒体库 | [screen-02](screen-02-library.zh-CN.md) |
| 3 | `queue` | 任务队列 | [screen-03](screen-03-queue.zh-CN.md) |
| 4 | `import` | 导入与转换 | [screen-04](screen-04-import.zh-CN.md) |
| 5 | `proofread` | 校对台 | [screen-05](screen-05-proofread.zh-CN.md) |
| 6 | `settings` | 设置 | [screen-06](screen-06-settings.zh-CN.md) |

下钻屏 6 屏(不在主导航,从各屏深入,支持 URL hash 深链):

| # | data-screen | 屏名 | 入口 | 文档 |
|---|---|---|---|---|
| 7 | `media-detail` | 媒体详情 · 剧集 | 媒体库行「详情」 | [screen-07](screen-07-media-detail.zh-CN.md) |
| 8 | `media-detail-movie` | 媒体详情 · 电影 | 媒体库电影行「详情」 | [screen-08](screen-08-media-detail-movie.zh-CN.md) |
| 9 | `queue-detail` | 任务详情 | 队列行 / 活跃任务行点击 | [screen-09](screen-09-queue-detail.zh-CN.md) |
| 10 | `request-template` | 请求模版 | 设置 → 服务卡「请求模版」 | [screen-10](screen-10-request-template.zh-CN.md) |
| 11 | `logs` | 日志 | 队列失败行 / 设置 · 系统 | [screen-11](screen-11-logs.zh-CN.md) |
| 12 | `schedule` | 定时任务 | 设置 · 系统 | [screen-12](screen-12-schedule.zh-CN.md) |

## 跳转关系图

```
概览 ──管线节点/KPI──→ 媒体库 · 任务队列 · 设置
      └─活跃任务行──→ 任务详情

媒体库 ──行操作/CTA──→ 导入与转换(free | season 深链)
       ├─剧集行「详情」→ 媒体详情(剧集)
       ├─电影行「详情」→ 媒体详情(电影)
       └─托盘「翻译所选」→ 任务队列

任务队列 ──行点击──→ 任务详情
         ├─失败行「日志」→ 日志
         ├─完成行「校对」→ 校对台
         └─CTA「＋新建任务」→ 媒体库(提示勾选)

导入与转换 ──开始导入/开始处理──→ 任务队列
校对台 ──术语回流──→ 设置(quality 组深链)
       └─完成写回──→ 任务队列
设置 ──服务卡──→ 请求模版
     ├─系统组──→ 日志 · 定时任务
媒体详情 ──整季导入──→ 导入与转换(season)
         ├─集「校对/详情」→ 校对台 · 任务详情
         └─「翻译缺失」──→ 任务队列
⌘K 全局搜索 ──→ 任意屏(可带 settings 面板深链)
```

## 顶栏状态表(go() 每屏写入)

| 屏 | 标题 | 副标题 | CTA(点击动作) |
|---|---|---|---|
| overview | 概览 | 管线正在运行 · 数据 5 秒前更新 | ⇪ 导入字幕 → 跳 import |
| library | 媒体库 | 1 284 项 · 剧集与电影合并视图 | 翻译所选 ▸ → 勾选联动菜单(简/繁) |
| queue | 任务队列 | 实时 · SignalR 推送 | ＋ 新建任务 → 跳 library + 提示 toast |
| import | 导入与转换 | 拖拽上传或服务器路径 · 转码 / 转格式 / 去重后入管线 | 开始处理 ▸ → 执行自由导入 |
| proofread | 校对台 | 3 Body Problem S01E02 · 128/486 行 | 暂存 ⌘S → toast「已暂存」 |
| settings | 设置 | 四组:服务 / 媒体与自动化 / 字幕与质量 / 系统 | 无 CTA(自动保存模型,按钮隐藏) |
| media-detail | 媒体详情 | Shōgun · S01 · 逐集字幕轨与翻译历史 | 翻译缺失 ▸ → 全部未排队集入队 |
| queue-detail | 任务详情 | S01E05 · 请求 #2417 · 72% · 剩 04:12 | ⏸ 暂停 ↔ ▶ 恢复(与屏内按钮同步) |
| request-template | 请求模版 | openai · gpt-4o-mini · prompt 编辑与试译预览 | 保存模版 → toast「新批次起生效」 |
| logs | 日志 | 环形缓冲 2 000 条 · SSE 实时 | ⇩ 导出 → 下载 lingarr-logs.txt |
| schedule | 定时任务 | Hangfire · 6 队列 · recurring 一览 | ＋ 新建任务 → 下拉菜单(预设 + 自定义 cron) |
| media-detail-movie | 媒体详情 | Dune: Part Two · 电影 · EN → ZH | 翻译缺失 ▸ → toast(任务进行中) |

## 深链格式

- `#<data-screen>` — 任意屏,如 `#queue-detail`
- `#import/season` — 导入屏直达「定向导入 · 整季」tab(hash 第二段,仅 import 支持)
- ⌘K 结果项可带 `data-panel="providers|quality"` — 跳设置后自动切到对应分组

## 阅读约定

- 元素清单里的「类名」均为原型 HTML 的 CSS class,即组件迁移时的对应锚点
- 状态列标注的是原型中实际可触发的状态(`appr`/`dirty`/`paused` 等均为 JS 切换的 class)
- 原型顶部有一条 `proto-bar`(助手条,含手机模式开关)——**非产品 UI**,迁移时剔除
