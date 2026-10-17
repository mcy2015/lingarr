# 字幕源变更实施批次任务书（R1–R3）

> 依据 2026-10-17 联网核查：Podnapisi 站点死亡（DNS 不存在，Bazarr 上游 2026-06-29 以 "no longer online" 移除）；xunlei 官方接口存活且已迁新域名、零认证、含简繁双语条目；OpenSubtitles 免费档额度 5~20/天、VIP 1000/天，官方响应头携带配额信息。
>
> 执行约定：批次按 R1→R2→R3 串行派发（同区域文件，避免冲突）；每批次独立验收后再进下一批。实现走 lingarr-dev 代理（白名单制，不 commit 不跑门禁）；服务端测试在容器内跑（本机无 dotnet SDK）；JSON 合同枚举值与仓库线形态逐字对齐（PascalCase）。

---

## 批次 R1（P0）：移除 Podnapisi

**目标**：7 源变 6 源，全仓库无 podnapisi 残留；DB 中已存在的 `subtitle_podnapisi_*` 设置行**不迁移不清理**（registry 忽略未知 key，与 enabled 开关"缺省即启用"同哲学，文档注明即可）。

### 文件白名单

删除：
- `Lingarr.Server/Modules/SubtitleSearch/Providers/PodnapisiProvider.cs`
- `Lingarr.Server/Modules/SubtitleSearch/Manifests/PodnapisiPluginManifest.cs`
- `Lingarr.Server.Tests/Modules/SubtitleSearch/PodnapisiProviderTests.cs`
- `Lingarr.Server.Tests/Fixtures/Subtitles/podnapisi/`（整个目录）

修改：
- `Lingarr.Server/Modules/SubtitleSearch/SubtitleSearchModule.cs` — 删 `AddSource<PodnapisiProvider>` 与 `AddSingleton<ISubtitleSourceManifest, PodnapisiPluginManifest>` 两行；"The seven v1 sources" 注释改为反映现状
- `Lingarr.Server/Modules/SubtitleSearch/SubtitleSourceSettingKeys.cs` — 删 `Podnapisi` 嵌套类
- `Lingarr.Server/Modules/Infrastructure/StartupService.cs` — 删 seed 两行（42–43）
- `Lingarr.Server/Modules/SubtitleSearch/Providers/SubdlProvider.cs:182` — 注释 "same guard shape as Podnapisi" 改指 gestdown/shooter
- 测试：`SubtitleProviderRegistryTests.cs`（167/212 两处源列表、223 `Find("podnapisi")` 断言）、`StartupServiceTests.cs`（314–315 seed 对、325 列表）、`SubtitleSearchControllerTests.cs`（148–151 fake provider、341 列表、881/920 history 用例的 provider 名换源）、`SubtitleTestSupport.cs`
- 文档：`docs/subtitle-search-tech-design.zh-CN.md`（146 行源描述、152 行优先级链 `OpenSubtitles>assrt>SubSource>SubDL>Gestdown>Podnapisi>shooter` 去掉 Podnapisi）、`docs/subtitle-search-prd.zh-CN.md` 同步

### 已定决策
- 默认优先级链去掉 Podnapisi 后为 `OpenSubtitles>assrt>SubSource>SubDL>Gestdown>shooter`（R2 合入后 xunlei 插到 shooter 前，见下）
- 顺带检查项（并入本批）：核对 `AssrtProvider` 的请求限流值与官方"20 次/分钟，token 与 IP 共享配额"对齐；不符则调整（一行级改动，附测试说明）

### 验收标准
1. `grep -ri podnapisi` 全仓库（含测试与 docs）零命中
2. registry/providers 相关测试断言 6 源；`GET /api/subtitle-search/providers` 返回 6 条
3. StartupServiceTests 断言 seed 集无 podnapisi
4. 容器内 dotnet test 全绿，无新增编译警告

---

## 批次 R2（P1）：新增 Xunlei 源（zh 直命中，补位第 7 源）

**目标**：接入迅雷字幕接口——零认证、哈希匹配、简繁双语条目直接命中 zh 免翻译。优先级链：`OpenSubtitles>assrt>SubSource>SubDL>Gestdown>xunlei>shooter`。

### 协议规格（干净室：按本规格实现，不打开 ChineseSubFinder 仓库）

- **端点**：`GET {BaseUrl}/subxl/{hash}.json`，默认 `BaseUrl = http://sub.xmp.xunlei.com:8000`（旧域名 `sub.xmp.sandai.net` 仍是可用 CNAME，取新域名为默认；支持 BaseUrl 覆盖指向 fake 源跑 E2E）
- **哈希（三段采样 SHA1）**：文件大小 < `0xF000`（61440 B）→ 不可哈希，`SearchAsync` 返回 `Empty`（debug 日志，不报错）；否则读三段各 `0x5000`（20480 B）：offset `0`、offset `⌊size/3⌋`、offset `size−0x5000`，对三段字节顺序拼接做 SHA1，**大写十六进制**输出
- **响应**：`{"sublist":[{"scid","sname","language","rate","surl","svote","roffset"},…]}`
- **language 实测取值**：`简体` / `繁体` / `简体&英语`（双语→zh+en）/ `未知语言` / GBK 损毁乱码（形如 `\uFFFD体`）。解析必须容错：无法识别按 unknown 处理，不抛错
- **sname 可能整体 percent-encoded**（`%3A%5C` 形态）→ 展示名 `Uri.UnescapeDataString` 兜底
- **下载**：surl 为明文 HTTP 直链文件（.srt/.ass/.ssa），复用 `SubtitleHttp.SendForBytesAsync` + `FormatFromFileName`，无压缩包
- **匹配语义**：hash-only 源（同 shooter），无文本搜索回退；哈希未命中返回空 sublist

### 文件白名单

新增：
- `Providers/XunleiProvider.cs` — `[SubtitleProvider("xunlei")]`，镜像 ShooterProvider 结构（hash-only、无 credentials、ISubtitleProviderHealth；TestConnection 用 dummy 全零哈希打端点，期望 200 JSON）
- `Providers/XunleiFileHasher.cs` — `static Compute(Stream)` / `Compute(string path)`，镜像 ShooterFileHasher
- `Manifests/XunleiPluginManifest.cs` — BaseUrl + ProxyUrl 字段（无 secret；描述注明"迅雷字幕接口，免认证，简繁双语直命中免翻译"）
- `Lingarr.Server.Tests/Modules/SubtitleSearch/XunleiProviderTests.cs` + fixture `Tests/Fixtures/Subtitles/xunlei/`（正常 sublist / 空 sublist / 乱码 language / percent-encoded sname 四份样本）

修改：
- `SubtitleSourceSettingKeys.cs` 增 `Xunlei` 嵌套类（BaseUrl/ProxyUrl）
- `SubtitleSearchModule.cs`：`AddSource<XunleiProvider>` + manifest 注册
- `StartupService.cs`：seed 增两行
- `Requests/LazyMediaHasher.cs`：增 xunlei 哈希惰性计算与缓存（镜像 `_shooterHashes`）
- `Requests/` 下 `SubtitleSearchRequest` 增 `XunleiFileHash` 字段 + `SubtitleSearchRequestBuilder` 填充
- `scripts/dev/fake-subtitle-sources.py`：若按源枚举则补 xunlei 模拟端点
- 测试：registry/StartupService/controller 三处源列表 6→7
- 文档：tech-design（源清单 + v2 观察池里 xunlei 转正）与 PRD 同步

### 已定决策
- 默认 BaseUrl 用新域名，明文 HTTP 是协议现实（端口 8000 无 TLS），handler 不加仅-HTTPS 约束
- `简体&英语` 按 zh 处理（直命中终态免翻译），与现有双语>单语评分规则衔接
- THIRD-PARTY-NOTICES **不新增**条目（协议按本规格干净室实现，无第三方代码搬运）

### 验收标准
1. 哈希 golden 测试：构造 ≥0xF000 的确定性流，`Compute` 输出与按规格独立手算（shell/python 单独算一遍）一致，且为大写十六进制
2. `<0xF000` 文件 `SearchAsync` 返回 Empty 不抛错
3. 乱码 language / percent-encoded sname 样本解析不抛错、双语映射正确
4. registry = 7 源、`RequiresAuth=false`；providers 端点含 xunlei；设置卡正常渲染（本源无 secret 字段 → 无输入框，符合"BaseUrl 不进 UI"裁决）
5. 容器内 dotnet test 全绿；前端 vue-tsc/oxlint 零错
6. 部署后烟测：对 ≥60KB 视频触发单源搜索，xunlei 返回候选且 zh 直命中不进翻译队列（历史页可见 provider=xunlei 行）

---

## 批次 R3（P1）：OpenSubtitles 配额可观测 + 406 熔断

**目标**：把官方配额信号（`/download` 响应头 `requests`/`remaining`/`reset_time_utc`）变成可观测状态；额度尽（406）时熔断到重置时刻。现状 `OpenSubtitlesProvider.DailyDownloadQuota=40` 常量只是静态水线，本批让它活在运行时。

### 范围（刻意最小）

- `OpenSubtitlesProvider.DownloadAsync`：大小写不敏感捕获三个响应头，存 provider 内线程安全的最新配额快照
- 406 → 抛带 quota 语义的 `SubtitleProviderException` → Orchestrator 记 cooldown **至 reset_time_utc**（复用/扩展 `SubtitleSourceCooldownStore.RecordFailureAsync`），reason=`quota_exhausted`
- `SubtitleProviderResponse` 增可选 `QuotaRemaining` / `QuotaResetUtc`（仅 opensubtitles 有值），`/api/subtitle-search/providers` 聚合返回
- 前端 `SubtitleSourcesCard.vue`：opensubtitles 行有配额时显示"今日剩余 N（HH:mm 重置）"徽章；无值不渲染
- SubDL **不实现**（响应头是否携带配额未查证，留待实测证据）；文档注记

### 验收标准
1. 406 → 该源 cooldown `Until=reset_time_utc`、reason=`quota_exhausted`，providers 端点可见
2. 正常下载后 providers 端点反映最新 remaining
3. 无配额头或其他源 → 字段 null，前端不渲染徽章
4. 前后端测试全绿（后端容器内跑）

---

## 明确不在范围

- BSPlayer / SuperSubtitles(feliratok.eu) 新增 —— 观望池，待英文源 hit 率有实测数据后再立项
- SubDL 配额观测、a4k / subtitle.best（已死）
- 翻译服务层任何变更（无新变量）
