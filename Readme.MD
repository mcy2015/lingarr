# Lingarr(重构版)

自托管字幕翻译管理器。fork 自 [lingarr-translate/lingarr](https://github.com/lingarr-translate/lingarr),在其基础上完成了前端整屏重写、后端模块化、数据库单引擎化与认证安全加固的个人深度重构分支。沿用 AGPL-3.0。

接入 Radarr/Sonarr 媒体库,自动扫描缺字幕的影视,经可配置的翻译服务链(13 家内置 + 插件)批量翻译并落盘;配套队列监控、逐行校对台、成本统计与术语表管理。

## 与上游的核心差异

| 维度 | 上游 | 本分支 |
|------|------|--------|
| 前端 | 原版页面 | 全部 12 屏按原型重写(cells 架构):仪表盘管线导航、媒体库矩阵、队列事件时间线、导入拖拽工作台、双轨校对台、设置五 tab;⌘K 全局搜索、键盘流、暗/亮主题、手机模式 |
| 后端 | 单体 Services | 按域拆 9 个 Module(DI/实现同域同目录),重构后行为由 660+ 服务端测试锁定 |
| 数据库 | MySQL/Postgres/SQLite 三引擎 | **仅 SQLite**(29 个历史迁移 squash 为单基线,可全新建库;数据落在单目录,备份即拷贝) |
| 认证 | 可选,默认弱 | **默认启用**:PBKDF2(60 万轮)自描述哈希、登出/改密即吊销会话、登录限流、首登强制改密、API Key 仅会话可铸且可吊销 |
| 产品化外壳 | 遥测/引导向导/多用户/文档站 | 全部移除:单用户模型、无遥测、无外联(除翻译服务自身) |
| 成本与质量 | 无 | 费率配置 + 成本统计/14 天趋势、导入前成本预估、试译预览、测试连接、术语表 CRUD+CSV、校对修正回流提示、翻译事件时间线与降级链归属 |
| 调度 | 简单 cron | 自动化批次单点驻留(cron/每次限额/新片延迟/下次执行),全链路时区标注(TZ 未设置界面出警告),运维任务与业务任务分卡 |

## 功能特性

**翻译服务**
- 13 家内置:LibreTranslate、LocalAI/Ollama(OpenAI 兼容)、DeepL、Anthropic、OpenAI、DeepSeek、Mistral、xAI、Gemini、Google、Bing、Yandex、Microsoft;另有插件机制
- 回退链:主服务失败按序降级,队列详情时间线可视化每次接管
- 每服务语言能力限制与链上覆盖推导;服务健康/延迟/成功率统计;配置期即可测试连接与试译预览

**自动化**
- 每日补缺批次:cron、每次翻译限额、新片延迟阈值、下次执行时间(带时区标注)单卡管理
- 定时任务表管理 4 个运维任务(媒体同步 ×2 / 统计快照 / 每周清理):cron 编辑、启停、手动触发、上次/下次执行

**媒体与导入**
- Radarr/Sonarr 集成:定时同步、语言覆盖率统计、按目标语言求差算缺字幕
- 路径映射配置期体检(可达性三态判定,防"字幕静默不落盘")
- 手动导入:拖拽 SRT/ASS,剧集编号自动匹配,导入前字符量/成本预估

**队列与校对**
- 队列:进度、重试、取消/暂停,SignalR 实时推送,事件时间线
- 校对台:源/译双轨逐行,J/K/Enter 键盘流,行内编辑,术语命中提示,SRT 快照导出

**统计**
- 产量/字符量/成本按月聚合,14 天趋势,按服务/媒体类型/语言分维

## 快速开始

```bash
# 1. 构建 Release 镜像(前端产物已打进镜像)
docker build -f Lingarr.Server/Dockerfile \
  --build-arg BUILD_CONFIGURATION=Release \
  -t lingarr:latest .

# 2. 起容器(仓库根目录附带 docker-compose.yml,按需改媒体路径/端口/TZ)
docker compose up -d
```

打开 `http://localhost:9878`(按 compose 端口),全新库默认启用认证:以 `admin/admin` 登录后**强制改密**。之后在「设置 → 媒体源」接入 Radarr/Sonarr 并配置路径映射,在「设置 → 翻译服务」上链至少一个翻译服务,再到「自动化」启用每日补缺。

数据(SQLite + Hangfire 队列)全部落在 compose 里挂载的 `./data` 目录,备份该目录即可;升级 = 重新 build 镜像后 `docker compose up -d`,迁移自动执行。

使用 LibreTranslate 时可独立部署(见 dev compose 中注释掉的服务块),应用内服务地址按实际可达地址填写;其余内置服务在界面配置密钥即用。

## 配置(环境变量)

| 变量 | 默认 | 说明 |
|------|------|------|
| `TZ` | UTC(回退) | **定时任务 cron 的解释时区与界面时间显示时区**;未设置或无效时回退 UTC,定时任务页会出警告条 |
| `BASE_PATH` | 空(根路径) | 反代需要子路径时设置,如 `/lingarr` |
| `LINGARR_ADMIN_USER` / `LINGARR_ADMIN_PASSWORD` | `admin` / `admin` | 首启种子管理员;缺省 admin/admin 且首登强制改密 |
| `AUTH_ENABLED` | `true` | 仅在数据库无值时作为首启种子生效 |
| `SQLITE_DB_PATH` | `lingarr.db`(相对 `/app/config`) | 主库路径 |
| `DB_HANGFIRE_SQLITE_PATH` | `/app/config/Hangfire.db` | 队列库路径 |
| `ENCRYPTION_KEYS` | 自动生成并持久化 | 服务商密钥的加密密钥(轮换用,逗号分隔,末位为活跃键) |
| `MAX_CONCURRENT_JOBS` | `4` | 翻译任务并发 worker 数 |
| `JOB_TIMEOUT_MINUTES` | `30` | 单任务静默超时(分钟),超时 Hangfire 重试 |

可作为环境变量持久化的应用内设置清单见 [Settings.MD](Settings.MD);容器支持 `user: "1000:1000"` 以非 root 运行(注意 `./data` 与媒体挂载目录的属主)。

## 开发

```bash
# dev 栈:Lingarr.Server(Debug)+ Vite(9876 直连 HMR)+ Traefik(lingarr.localhost)+ radarr/sonarr
docker compose -f docker-compose.dev.yml up -d

# 服务端测试(本机无需 dotnet,SDK 容器跑)
docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet test Lingarr.slnx --filter "Category!=Integration"

# 前端:测试 / 类型检查 / lint
cd Lingarr.Client && npx vitest run && npx vue-tsc --noEmit && npx oxlint ./src
```

结构:`Lingarr.Server`(API + Hangfire 作业,Modules/ 按域拆分)· `Lingarr.Core`(领域实体与配置)· `Lingarr.Migrations`(FluentMigrator,SQLite 单基线)· `Lingarr.Client`(Vue 3 + Vite + Tailwind,src/cells/ 按屏分域:kernel 纯组件 / app 跨屏共享 / 各业务 cell)。

> 走查提示:dev 栈经 `lingarr.localhost` 访问的是打进 Server 镜像的构建版,改前端后需 `up -d --build Lingarr.Server` 才在走查地址生效;`localhost:9876` 直连 Vite 才有 HMR。

## 文档

- [docs/refactoring-status.zh-CN.md](docs/refactoring-status.zh-CN.md) — 重构全史与各批次验收记录
- [docs/prototype-spec/](docs/prototype-spec/) — 12 屏原型逐屏规格(元素清单/交互/状态完备性)
- [docs/settings-guide.zh-CN.md](docs/settings-guide.zh-CN.md) — 设置页各项语义
- [docs/module-analysis.zh-CN.md](docs/module-analysis.zh-CN.md) — 服务端模块分析

## 致谢与许可

AGPL-3.0(见 [LICENSE](LICENSE))。基于 [lingarr-translate/lingarr](https://github.com/lingarr-translate/lingarr) 衍生,感谢上游的工作。

上游依赖:图标 [Lucide](https://lucide.dev/icons) · 字幕解析 [AlexPoint/SubtitlesParser](https://github.com/AlexPoint/SubtitlesParser) · 翻译 [LibreTranslate](https://libretranslate.com) / [GTranslate](https://github.com/d4n3436/GTranslate)。
