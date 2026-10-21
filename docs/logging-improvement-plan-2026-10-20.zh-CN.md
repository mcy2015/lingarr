# 日志系统完善计划（2026-10-20）

> 目标一句话：让「无 SSH 时只剩前端日志页」这条唯一观测面变得**可持久、可检索、可串链**。本计划只做 observability，不改变任何业务行为。
>
> 状态：**2026-10-20/21 全部八批（L0/L1a/L1b/L2/L4a/L3/L4b/L5）实施完毕**。容器验证：后端 0 编译错误、1482/1483 测试通过（1 个既有条件 skip）、审计门五类违规全部归零；前端 vue-tsc + 生产构建绿。实施偏差见第 9 节。证据标注 `文件:行` 为 2026-10-20 实测核读。

## 1. 背景与问题陈述

**环境**：.NET 后端 + Vue 前端，QNAP NAS Docker 单实例自托管，媒体量 10w 级硬约束。部署即推 main→CI→NAS pull，本机无 dotnet SDK，一切验证走容器构建/CI。

**三次定位困难实锤**（优先级全部来源）：
1. **webhook 全灭**（Hangfire.SQLite 忙等风暴，`JobContextFilter.cs:20-24` 注释留档）：靠 SSH 拿栈才定位；非翻译 job 异常只进易失日志流，重启无痕。
2. **assrt 493 节流「未记载」**：字幕源 HTTP 总闸 `SubtitleHttp.cs`（实测 500 行）零 logger 调用，429/493/406→DailyLimit 判定、节流等待、超时全静默。
3. **字幕源冷却多轮修补**：下载管线决策（CooldownSkipped/黑名单）只落 DB 不进日志流，日志页看不到「为什么没下」。

**根因盘点**（2026-10-20 全仓实测）：
- 无文件持久化：纯 MEL + 自制 ColoredConsole + 1000 条内存环形（`InMemoryLogProvider.cs:34-35`）；docker-compose 无 logging driver 配置。重启即丢。
- 零 correlation：全仓无 BeginScope；`JobContextFilter.cs:9-10` AsyncLocal JobId 存在但不进日志消息。DB 侧键齐全（translation_requests.JobId、subtitle_downloads.SearchSessionId）但日志不带键。
- SSE 用 2s 轮询环形队列 Count 差值（`LogsController.cs:27-46`）：竞态丢/重条、无心跳、重连重放 100 条重复；异常堆栈拼在 message 尾部（`InMemoryLogProvider.cs:79-80`），前端 `LogsPage.vue` 单行 truncate 压扁、v-html + 正则还原 `|Green|` 标记（内嵌 24 处）。
- 异常手法碎片化：整异常 53 / 仅消息 34 / `ex.Message` 内插丢栈 17 / 双重格式 4（如 `SyncMovieJob.cs:65-67`）/ `LogError(e, e.Message)` 1。信噪比倒挂：344 调用点 W/E 占 59%。
- 安全：radarr/sonarr apikey 拼进查询串（`IntegrationService.cs:25,60`），异常 ToString 带 URI 即落 key；LibreService 日志带字幕原文与整响应体（`LibreService.cs:162,179-180`）。

## 2. 目标 / 非目标

**目标**：
- 可读性：日志页可搜索、堆栈独立字段可折叠、无彩色标记污染、无重复条目。
- 定位准确度：异常必带完整堆栈；HTTP 源失败必带原因码（DailyLimit/节流/超时）；一次事故用一个键拉全链。
- 持久性：重启后最近日志可在 NAS 卷（File Station 可读，无需 SSH）追溯，磁盘硬上限 350MB。

**非目标**：ELK/Loki/OTel、日志入库、Activity 全链追踪、job_runs 新表、前端虚拟滚动、多文件分类 sink、换 ILogger facade、告警管道。**永久不做**：任何形式的全库日志导出/上报。

## 3. 方案总览

**核心裁决**：
- 引入 Serilog 仅作 provider 层（文件 sink），344 个日志调用点零改动；先与自制 provider 并存（L1a），L5 退役彩色标记与 ColoredConsoleLogger。
- Correlation 走新建 `CorrelationContext`（照抄 JobContextFilter 的 AsyncLocal 模式），五键 `jobId/requestId/searchSessionId/mediaKey/provider` 与 DB 既有列一一对齐，不上 BeginScope。
- SSE 重构：seq 单调序号 + 每连接有界 Channel(256)（满则丢+gap 标记，规避广播风暴）+ Last-Event-ID 断点续传 + 15s 心跳 + 批量 flush。
- 覆盖补齐走 SubtitleHttp 总闸统一记（不给 7 个 Provider 逐个加）；正常路径聚合 Info、逐条 Debug。
- InMemory sink 加独立最小级（Information），防 Debug 洪峰挤掉 W/E。
- 异常规范：整异常对象第一参、禁 `ex.Message` 模板内插、禁双重格式；补 Hangfire `IServerExceptionFilter`；非翻译 job 失败持久化 = schedule 表加 `last_error/last_error_at` 两列（不建新表）。

**PM 调整 3 处**：脱敏前移至 L1（安全修复不过夜）；L4 拆为 L4a（总闸，仅依赖 L1a，可提前）+ L4b（管线/编排器，依赖 L2）；L3 必修收敛为 22 处丢栈/坏栈点，34 处仅消息降为审计警告顺手修。

## 4. 分批计划（合计 ≈9.5-10 人日，L0 全程并行）

| 批次 | 内容 | 估时 | 依赖/并行 |
|---|---|---|---|
| L0 | 审计基线 `scripts/check-logging.sh` + CI 挂钩（ratchet 门） | 0.5d | 前置门，全程并行 |
| L1a | Serilog 文件 sink 接入 + 脱敏（apikey URI/字幕原文/响应体截断）+ compose stdout 上限 | 1d | ∥ L1b |
| L1b | SSE 后端重构（seq/Channel/Last-Event-ID/心跳/批量 flush）+ InMemory 独立最小级 | 1d | ∥ L1a |
| L2 | Correlation 五键注入（enricher 管道，调用点免手写） | 2d | 依赖 L1 |
| L3 | 异常规范清理（22 处必修）+ Hangfire IServerExceptionFilter + schedule 加两列（M0014）+ dashboard 生产开放（配置开关） | 1.5d | 规范部分 ∥ L2 |
| L4a | SubtitleHttp 总闸日志（限流/节流/超时/原因码） | 1d | 仅依赖 L1a，可 ∥ L2 |
| L4b | 管线决策日志 + 编排器每源聚合 + LibreService 收尾 | 1d | 依赖 L2 键集 |
| L5 | 彩色标记债清理（24 处）+ ColoredConsoleLogger 退役 + 前端日志页五项打磨 | 1.5d | 依赖 L1a |

**验收标准（可断言）**：
- L0：脚本 CI 对 main exit 0，输出全部分类违规计数（apikey=/ex.Message 内插/双重格式/|Green|）；`--gate` 模式对基线 ratchet（新增违规则红）。
- L1a：容器启动后 `/app/config/logs/` 产生当日文件；单测断言滚动参数绑定与 env 提级 Debug 生效；TranslationModule 插件 factory 烟测（共享单例 Serilog、无重复注册）；新增日志语句 `grep apikey=` 0 命中。
- L1b：SSE 单测断言 seq 单调、Channel 满出 gap 标记、Last-Event-ID 续传不重不漏、15s 心跳、批量 flush；新旧前后端组合兼容（字段只增不删，旧前端忽略 seq）；灌 10w 条 Debug 后环内 W/E 仍在（独立最小级生效）。
- L2：集成测试跑一次手动搜索，断言编排器/管线/HTTP 层日志行各携带适用键；五键与 DB 列一一对齐（无新键名）。
- L3：审计脚本丢栈类计数降为 0；job 抛异常 → schedule.last_error/last_error_at 落库（M0014 迁移测试：nullable 加列前滚兼容）；dashboard 生产经 LingarrAccess 守卫可访问、配置开关默认开启。
- L4a：stub 回 493/429/406/超时，各断言恰一条 Warning 含 provider+原因码；成功路径不刷 Info。
- L4b：每源聚合计数行可见；CooldownSkipped/黑名单决策行可见；每轮 Info 行数 = O(源数) 而非 O(媒体数)，10w 模拟轮实测数字写入验收记录并校准参数。
- L5：`grep -rn "|Green|" Lingarr.Server` 0 命中；LogsPage spec：搜索生效、数组 ≤5000、按 seq 去重后重连重复=0、堆栈默认折叠、无 v-html（渲染节点为文本）。

**回滚**：每批独立 revert。L1a 并存设计即回滚路径（日志目录残留无害）；L2 enricher 只加字段无损；L3 nullable 加列有 M0008:53 先例，revert 镜像即可；L4/L5 纯新增语句/纯前端。

## 5. 成功指标（上线后 2 周回收）

**事故演练（一次通过即达标）**：
1. 触发（或 stub）一次 assrt 493 → 前端日志页搜 `493` 或 `rate-limit` 直接看到判定 Warning 行（provider+状态码+判定，现行映射 493/509→RateLimited+1min 冷却；429/406+零余额→DailyLimit），不查 DB。
2. 模拟 SQLite 锁竞争 → File Station 打开日志文件见完整堆栈 + schedule.last_error 非空 + dashboard 见 Failed，全程不 SSH。
3. 复制一次手动搜索的 requestId → 日志页贴入拉出该请求全部行（含 ≥1 条编排器聚合行、≥1 条管线决策行）。

**量化对照**（L0 记基线，上线后同场景复测）：W/E 占比自 59% 基线显著下降且真异常专属；SSE 重连重复条数=0；`grep apikey=` 日志文件 0 命中；磁盘峰值 ≤350MB 且日志量与媒体数解耦。

**反向指标（不能变坏）**：搜索轮 P95 耗时劣化 ≤10%；容器内存无增长（Channel/环形均有界）。

## 6. 裁决记录（2026-10-20 用户已拍板）

| # | 裁决点 | 决定 | 实施口径 |
|---|---|---|---|
| 1 | Hangfire dashboard 生产开放 | **完整开放** | 实现为配置开关（env `HANGFIRE_DASHBOARD_ENABLED`，绑定 `Hangfire:DashboardEnabled`，默认 true），docker-compose.yml 显式配置；完整功能非只读；守卫复用既有 `LingarrDashboardAuthorizationFilter`（`LingarrAccessGuard.cs:71-79` 已就绪，仅注册处 `ApplicationBuilderExtensions.cs:57-60` 挪出 dev 分支） |
| 2 | 文件保留参数 | **默认 7×50MB ≈ 350MB** | 按日滚动 + 单文件 50MB + 保留 7 文件；顺带给 docker-compose 加 json-file `max-size: 50m`/`max-file: 3` 封住 stdout 无上限增长 |
| 3 | LibreService 字幕原文 | **按推荐** | `{Text}`（`LibreService.cs:162`）完全移除——触发场景是限流耗尽与内容无关；`{ResponseContent}`（:179-180）截断 200 字符保诊断价值 |
| 4 | 文件 sink Debug | **Information 起步按需降** | Serilog 最低级走配置节，env `Serilog__MinimumLevel__Default=Debug` 提级、无需重建镜像；内存/SSE sink 独立最小级不受影响 |
| 5 | L5 打磨范围 | **五项全做** | 搜索框、5000 条上限、堆栈折叠、去 v-html、seq 去重；虚拟滚动不做 |

## 7. 风险与缓解

1. 插件 factory 分叉 logger（TranslationModule.cs:59 独立 factory）→ L1a 验收含共享单例烟测，硬门。
2. SSE 前后端同步上线窗口 → 字段只增不删 + 前端 seq fallback，允许分两 PR 先后部署。
3. NAS 挂载卷写入性能未知 → `flushToDiskInterval=2s` + 容量硬上限；异常慢则备选 AsyncWrapper。
4. 迁移加列（M0014）→ nullable、有 M0008:53 先例、迁移测试覆盖。
5. 10w 量级日志量假设未压测 → L4b 的 O(源数) 断言 + 实测校准，参数不拍脑袋。
6. 本机无 dotnet SDK → 容器内构建测试验证，CI 绿灯是部署前置门。

## 8. 上线与回滚策略

- 既有流程不变：每批独立 PR → CI（build+test+check-logging.sh --gate）绿 → 合 main → NAS `docker compose pull && up -d`。
- 每批部署后 3 分钟观测面自检：日志页能看到本批部署的启动 Info 行 + `/app/config/logs/` 出现新写入 = 端到端通。
- 回滚单位=批次（单 PR revert）；L3 迁移与 L1a 文件目录均设计为 revert 后残留无害。
- 上线顺序：L0 → (L1a+L1b) → (L2 ∥ L4a) → L3 → L4b → L5；L4a 提前后事故演练 1（assrt 493）可在 L4b 前先行验证。

## 9. 实施记录与偏差（2026-10-21 补）

**验证口径**：本机无 dotnet SDK，全部构建/测试在 `mcr.microsoft.com/dotnet/sdk:10.0` 容器内镜像 CI 命令完成；前端本机 vue-tsc + vite build。终态：后端 0 编译错误 / 1482 通过 / 1 既有 skip；`check-logging.sh --gate` 五类违规全 0（基线同步收紧）。

**与原方案的偏差**（均为实施时发现的事实所迫，已验证不劣于原设计）：
1. **L3 schedule 持久化改道**：核实后发现根本不存在 schedules 表——`ScheduleService.UpdateJobState` 仅做 SignalR 广播。原「M0014 加 last_error/last_error_at 两列」没有落点。改为：四个吞异常 job（SyncMovie/SyncShow/Statistics/EmbeddedTrackScan）在 catch 记录后 **rethrow**（均带 `AutomaticRetry(Attempts=0)`，无重试风暴）→ Hangfire 落 Failed 态持久化（schedule 屏 LastJobState 重启可见）+ `IServerExceptionFilter` 文件日志 + dashboard 三处痕迹，零迁移。六个钉住旧「不抛出」契约的测试翻转为 `ThrowsAsync` + 副作用断言保留。
2. **审计脚本口径修正**：`ex_message_template` 从逐行扫描改为只对提取出的日志语句计数——异常包装（`throw new ...{ex.Message}`）、DB error 短消息、遥测 verdict 字符串属合法用法不计违规。
3. **L4b 聚合层级修正**：「每源每轮聚合」的正确量级锚点在 `SubtitleSearchJob` 轮级（编排器 SearchAsync 是单媒体×N源，逐媒体聚合仍是 O(媒体)）。落地：编排器每媒体冷却日志降 Debug + job 轮首一条「Cooling down this round」Information + 轮末富化汇总（耗时/下载 tally/每源失败 breakdown）。
4. **AI 服务体检扩展**：DeepL/GTranslator 的重试耗尽日志同样带整段字幕原文，同款移除（LibreService 之外的泄漏面）。
5. **L5 console 语义**：Serilog console sink 接管后，原 ColoredConsoleLogger 的配置时区转换不再存在——容器 `TZ=Europe/Amsterdam` 与配置一致，docker logs 输出不变；文件与 console 共用同一模板。
6. **前端顺手修复**：SSE 手动重连分支换新 EventSource 却从不重挂 handler（既有缺陷，断线一次页面即静默）——重构为 `connectStream()` 单入口；并利用 `readyState===CLOSED` 区分原生重试与终局断开。

**各批交付索引**：L0 `scripts/check-logging.sh`(+baseline+CI 步骤)｜L1a `SerilogFileLogger.cs`+`LogSanitizer.cs`+脱敏( Integration/ImageService)+compose 上限｜L1b `InMemoryLogProvider.cs`(seq/StackTrace/订阅者 Channel)+`LogsController.cs` 重写｜L2 `CorrelationContext.cs` 五键+双 sink 盖章｜L4a `SubtitleHttp.cs` 总闸八记录点｜L3 异常规范+`IServerExceptionFilter`+四 job rethrow+dashboard 开关｜L4b `SubtitleDownloadPipeline.RecordAsync` 单点决策日志+黑名单 Info+轮级聚合｜L5 标记清零+`ColoredConsoleLogger`/`CustomLogFormatter` 退役+LogsPage 五项+重连修复。
