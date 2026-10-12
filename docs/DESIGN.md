# Lingarr 设计系统 · Timeline(剪辑台)

方向定稿:字幕的本质是轨道。用剪辑软件(NLE)的空间语言:石墨面板、中性细线、
功能色轨道、NLE 橙唯一强调色、等宽数字。暗色优先。

## 语义 token(CSS 变量,挂 `:root`,14 套主题共享契约)

### 核心(既有,重定义默认值)
| Token | 值 | 用途 |
|---|---|---|
| `--primary` | `#151619` | 应用背景 |
| `--primary-content` | `#D8DBDF` | 主文字 |
| `--secondary` | `#1C1E22` | 面板/卡片/侧栏 |
| `--secondary-content` | `#C7CCD2` | 次级文字 |
| `--tertiary` | `#22252A` | 凹陷区/表头/轨道底 |
| `--tertiary-content` | `#C7CCD2` | |
| `--accent` | `#E8833A` | NLE 橙——CTA/激活/播放头 |
| `--accent-content` | `#20130A` | 橙底上的文字 |

### 新增
| Token | 默认值 | 用途 |
|---|---|---|
| `--line` | `#2E3238` | 中性细线(替代旧 border-accent 泛滥) |
| `--success` / `--success-content` | `#6FBF73` / `#0C1A0E` | 完成/双语 ✓ |
| `--warning` / `--warning-content` | `#E5B567` / `#1C1408` | dev 版本/注意 |
| `--danger` / `--danger-content` | `#E25C4A` / `#1F0D09` | 错误/删除 |
| `--info` / `--info-content` | `#5B9BD5` / `#0D1826` | 信息/源语言轨道 |
| `--track-a` / `--track-b` | `#5B9BD5` / `#E8833A` | 轨道功能色(源/译文) |

状态色在主流主题(dracula/nord/gruvbox/solarized×2)单独覆写,其余主题继承默认。
亮色主题(solarized-light)覆写 `--line` 为深色。

## 字体(自托管,不依赖外网)

- **UI**:Archivo Variable + 系统中文栈(PingFang SC / Microsoft YaHei / Noto Sans SC)
- **数据**:JetBrains Mono——时间码、语言徽章、版本号、统计数字(`tabular-nums`)
- 页面标题:Archivo 700 紧字距;正文 400/500;数字一律 mono+tabular
- 一处_boldness_:全站只有 accent 橙承担"正在发生"语义;线是中性的,色块只出现在轨道和状态

## 组件规范要点

- **Card**:`bg-secondary` 扁平面 + `border-line` 1px,去掉旧渐变+投影(SaaS 卡片脸)
- **Button**:primary=accent 实底;secondary=透明底+line 边;ghost=无边;`focus-visible` ring accent
- **Badge**:mono、大写语言码、`bg-tertiary border-line`;状态徽章用 success/danger
- **Progress(签名组件)**:轨道条 = tertiary 底 + accent 分段填充 + 2px 播放头竖线
- **表格**:行 hover `bg-tertiary/50`;表头正常大小写(禁全大写)
- **侧栏**:激活项 = 2px accent 左轨 + `bg-tertiary`;词标 `LINGARR_`(橙色下划线光标)

## 移动端规范

- 侧栏 <md 抽屉化(已存在),触控目标 ≥40px;按钮 h-10
- 列表 <md 由表格退化为卡片堆叠(沿用现有 md:grid-cols-12 模式),操作按钮右沉
- 统计网格:3 列 → 2 列(<lg)→ 1 列(<sm)
- 轨道条在窄屏保持水平,时间码换行隐藏
- 双语文案长度差(中/英)在 flex 布局下自然换行,不截断

## 主题兼容

13 套既有主题不动含义,只新增 token;默认主题(:root)值即 Timeline。
