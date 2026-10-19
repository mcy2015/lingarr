# R10 每服务语言对路由 · 技术设计（tech-lead 产，2026-10-19）

> **前提修正**：PM 计划原述"回退链不判语言对"已过时——`SubtitleTranslationService.GetTranslationCandidates`（SubtitleTranslationService.cs:329-365）早已对每服务调 `GetLanguagePair`，null 即跳过；全跳过已有清晰报错。**R10 真实缺口 = 用户可配置的每服务语言对约束（收窄层）**：单键设置 `service_language_pairs` + 单点消费 + 跳过可见。工作量从"中大"降为约 2.5 人日。

## 裁决点

1. **能力清单：复用 `GetLanguages()`**（`ITranslationService`，GTranslate 静态 JSON 叉积方向化 / DeepL·LibreTranslate 端点拉取 / AI 系=全支持）。不建新能力层、不做启动预热。拉取失败维持现状（跳过该服务）——能力未知时放行会造成静默错配。已知技术债（不在 R10 修）：LibreTranslate 吞错返回空表=视为全支持。**用户过滤器独立于能力表**：配了 allowlist 的服务即使能力表拉取失败也按用户清单判定（纯静态无网络依赖）。
2. **存储：单键 `service_language_pairs`**（JSON map `{"deepl":{"sources":["en","ja"],"targets":["zh"]}}`，sources×targets 叉积，`"*"` 通配；缺省=跟随内建能力）。写侧逐码过 `LanguageCodeService.GetNormalizedCode`，非法码拒写。与 `service_type` 单键存整链同构，PrepareAsync 批量读零额外往返。
3. **消费点：仅运行时一处**（`GetTranslationCandidates`，`GetLanguagePair` 之前）。不在请求创建时判——链成员与配置在创建到执行间可变，且创建时判定需实例化服务（DeepL/Libre 有 HTTP）。新增 `TranslationEventType.PairSkipped`，照 Degraded 先例按 (service,pair) 每请求去重，message 区分 "not in configured pairs" vs "not supported by provider"。
4. **入口覆盖**：pipeline 下发（`TranslationDispatch` 新属性 `LanguagePairFilter`）天然覆盖 file/embedded-track/bulk/content/自动化全路径；`TranslateController.TranslateLine`（418-434，绕过 pipeline 直接 new）需显式读设置传入；**proofread 排除**（语义不同，UI 注明）。
5. **过滤器只做准入不改写码**：匹配用 `GetBestMatch` tier（en-US 命中 "en"）；发给服务的实际码仍由能力表解析，两层不混写。
6. **UI：扩展 FallbackOrderCard**（langcap 三态 chip 从只读升级为点击开每服务语言对编辑弹窗：sources/targets 双多选 + "不限"开关）。不动 ProviderEditModal（manifest 插件 ABI，加硬编码节成本高）。`GET /api/translate/languages/{serviceType}` 响应加 `configuredPairs`，编辑器同时展示内建能力表（只读参考）。

## 消费点改造清单（file:line）

- `TranslationPipeline.PrepareAsync`（44-53）：批量读加新键 → `TranslationDispatch.LanguagePairFilter`。
- `TranslationPipeline.RunAsync`（102-104、128-130）：两处 `new SubtitleTranslationService` 传入。
- `SubtitleTranslationService`（81-88 构造、341-356 循环）：`IsAllowed` 先行 + `RecordPairSkippedAsync`（仿 415-432）。
- `TranslationEventType`（:23 旁）：加 `PairSkipped`；事件查询端枚举透传。
- 新文件 `Modules/Translation/ServiceLanguagePairFilter.cs`（`IsAllowed(service, source, target): bool?`，null=未配置）。
- `SettingKeys.cs`（:44 旁）：`Translation.ServiceLanguagePairs`。
- `TranslateController.GetServiceLanguages`（502-549）：响应加 `configuredPairs`；`TranslateLine`（418-434）注入过滤器。

## 批次拆分

- **批次 1（后端）**：SettingKeys.cs、ServiceLanguagePairFilter（新）、ITranslationPipeline.cs、TranslationPipeline.cs、SubtitleTranslationService.cs、TranslationEventType.cs、TranslateController.cs、TranslationRequestEventService.cs + 3 测试文件。验收：配 deepl 仅 EN→ZH 后 JA→ZH 请求跳过它，时间线见 PairSkipped 事件含原因；全部被过滤时报错列出每服务原因。
- **批次 2（前端）**：types/setting.ts、FallbackOrderCard.vue、新 LanguagePairDialog.vue、chainCoverage.ts + spec、i18n 两 locale。保存前对全局 target_languages 逐对跑 chainCoverage，某对 available=0 时阻断保存。

## 风险与兜底

配错全跳过=运行时异常兜底（文案列出原因）+ UI 保存前链路预检阻断；单设置键清空即回滚到现状；过滤器随 dispatch 每请求重读（配置变更对暂停恢复即时生效）；归一化走 tier 匹配不做字符串等值。
