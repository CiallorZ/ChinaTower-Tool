# LinkForge 技术决策记录（ADR）

状态：**草案，待评审确认**
日期：2026-10-15
范围：LF-000 产出物之一。本文件中的"候选"不等于"已定"；标注 **待定** 的条目必须在对应任务开工前关闭。

> 许可证与支持周期均需在引入依赖时于官方页面复核，并登记到本文第 13 节。不得把本文件的候选选型当作永久结论。

## 0. 决策摘要

| 编号 | 主题 | 结论 | 状态 |
| --- | --- | --- | --- |
| ADR-001 | 技术栈与目标框架 | C# / .NET 10 LTS / WPF / MVVM | 待确认（依赖 SDK 安装） |
| ADR-002 | MVVM 与 UI 控件库 | CommunityToolkit.Mvvm + WPF-UI，自建设计 Token | 待确认 |
| ADR-003 | 持久化 | SQLite + EF Core，项目包用 JSON | 提议 |
| ADR-004 | 日志与脱敏 | Serilog + 统一 Redaction 层 | 提议 |
| ADR-005 | HTTP/API | HttpClientFactory + 自研请求模型 | 提议 |
| ADR-006 | MQTT | MQTTnet（纯托管），不用 Paho C | 提议 |
| ADR-007 | RTSP/媒体 | 自研信令诊断 + 播放组件抽象 | 提议（分发许可待定） |
| ADR-008 | 凭据存储 | Windows DPAPI（CurrentUser）+ SecretReference | 提议 |
| ADR-009 | 外部进程调用 | `ProcessStartInfo.ArgumentList`，禁止 shell 拼接 | 已接受（安全红线） |
| ADR-010 | HTML 报告 | 内置模板 + `HtmlEncoder` 转义 | 提议 |
| ADR-011 | 插件与原生 SDK | 进程内契约先行，原生 SDK 进程外隔离 | 提议 |
| ADR-012 | 打包与分发 | MVP 便携包，后续评估 MSIX/WiX | 待定 |
| ADR-013 | 与既有资产的边界 | 独立新建，不修改 `towerlens` | 已接受 |
| ADR-014 | 默认主题策略 | 跟随系统，浅色为设计基准，深色对等 | 提议（待 D-03 确认） |
| ADR-015 | 令牌命名空间 | 语义令牌统一 `Lf` 前缀 | 提议 |
| ADR-016 | 状态模型分层 | 任务层与步骤层分离，显式映射 | 提议 |

---

## ADR-001 技术栈与目标框架

**背景**
PRD 与架构文档指定 C# / .NET / WPF / MVVM，目标 Windows 10/11 x64。环境实测：本机**无任何 .NET SDK**，仅有 `Microsoft.NETCore.App 8.0.14` 运行时；`winget` 可安装 `Microsoft.DotNet.SDK.10`（当前 10.0.402）。

**决策**
- 采用 **.NET 10（LTS）** + `net10.0-windows` 作为目标框架；语言版本随 SDK 默认。
- 目标架构 **x64**；ARM64 与 Windows 10 最低小版本号在 LF-013 前定稿。
- 开发机安装 SDK：`winget install --id Microsoft.DotNet.SDK.10`（系统级变更，执行前需用户确认）。

**理由**
- .NET 10 为 LTS 通道，支持周期覆盖本项目预期维护窗口；.NET 8 LTS 已于 2026-11 进入支持末期。
- WPF 是 Windows 桌面长任务工具最成熟的选择，UI 虚拟化、DPI、键盘可达性开箱可用。
- 现有 `towerlens` 为 Python + pywebview 实现，与"本地优先 + 原生网络/进程能力 + 凭据 DPAPI"的目标不匹配，**不复用为宿主**，仅参考其领域知识（见 ADR-013 备注）。

**替代方案**
- `net8.0-windows`：兼容性更好，但需在 2026-11 前升级，返工成本高于直接起步 .NET 10。
- Avalonia / WinUI3：跨平台或更现代，但 Windows 单平台收益不足，且 WinUI3 打包与驱动场景约束更多。

**影响与风险**
- 需在干净环境验证 SDK 10 的 WPF 模板与 CI 镜像可用性。
- 若后续要求支持 Windows 10 早期版本，需复核 .NET 10 的 OS 支持矩阵。

## ADR-002 MVVM 与 UI 控件库

**决策**
- MVVM 基础设施：`CommunityToolkit.Mvvm`（source generator，`[ObservableProperty]` / `[RelayCommand]`）。
- 控件库：`WPF-UI`（Fluent 风格）作为基础控件，**所有颜色/间距/字号走自建设计 Token**，不直接把控件库默认样式当作视觉规范。
- 导航：`Frame` + ViewModel 驱动，禁用 code-behind 承载业务逻辑。

**理由**
与 `UI_WIREFRAMES.md` 的"深色优先 + 紧凑表格 + 时间线"方向一致，同时避免自研控件库带来的长期维护成本。

**约束**
- 状态色必须"图标/文字 + 颜色"双重表达（可访问性红线）。
- 控件库版本升级需回归主窗口、表格、日志面板三类页面。

## ADR-003 持久化

**决策**
- 运行时数据：SQLite + EF Core（Code First + Migrations），数据库存放于用户数据目录。
- 项目导入导出：带 `schemaVersion` 的 JSON 包，**默认排除秘密**，只导出 `SecretReference`。
- 写入策略：事务或原子替换（临时文件 + 替换），避免半写状态。

**理由**
PRD 7 节数据模型关系型特征明显；SQLite 单文件便于备份与现场拷贝；JSON 包便于评审与差异比对。

**影响**
- 需要 Migrations 回归测试与"旧版本数据库打开"用例。
- 大证据文件（抓包、视频片段）不进数据库，只存路径 + 哈希 + MIME。

## ADR-004 日志与脱敏

**决策**
- 结构化日志：Serilog（文件 sink + 内存环形缓冲供 UI 面板读取）。
- **脱敏在写入前执行**：统一 `IRedactor`，覆盖密码、Token、Cookie、`Authorization`、URL userinfo、私钥、连接串。
- 日志字段：`Timestamp`、`Level`、`Module`、`RunId`、`TargetId`、`EventId`。

**红线**
脱敏不得只在 UI 层做；UI、磁盘日志、报告三条路径必须共用同一实现，并有自动化测试覆盖嵌套 JSON。

## ADR-005 HTTP/API

**决策**
- `IHttpClientFactory` + 命名客户端；超时、重定向策略、代理按环境配置。
- 请求模型自研（方法、URL、Query、Headers、Body、认证），不使用 Postman 类外部依赖。
- TLS 证书校验默认开启；**禁止**为"解决报错"而关闭校验。
- 响应体设大小上限 + 截断标记，避免大响应拖垮 UI。

**P1 扩展**：OpenAPI 导入、断言、请求串联、OAuth2、HMAC/自定义签名（沙箱限制）。

## ADR-006 MQTT

**决策**
- 采用 **MQTTnet**（纯托管 .NET 实现，MIT），不采用 Eclipse Paho C/C++。

**理由**
- 纯托管避免原生库 P/Invoke、位数与再分发问题，与 ADR-001 的 x64 单目标一致。
- 原生支持 MQTT 3.1.1 / 5.0、TLS、遗嘱、Retain、QoS，满足 P0 与大部分 P1。
- Paho C 的跨平台封装会增加打包与崩溃面，收益不足。

**替代方案**
`MQTTnet` 若维护状态恶化，备选 Paho C 或自研最小 3.1.1 客户端（成本高，仅兜底）。

**验收依赖**
集成测试需本地 Broker（Docker 起 `eclipse-mosquitto`，本机已具备 Docker）。

## ADR-007 RTSP 与媒体

**决策**
- **信令诊断自研**：DNS → TCP → RTSP 握手（OPTIONS/DESCRIBE）→ 鉴权（401/`WWW-Authenticate`）→ SDP 解析 → SETUP/PLAY；错误分类映射到统一状态。
- **媒体接收**：MVP 只做 RTP 包统计（收包数、丢包、时间戳、码率估算），**不解码**；RTP over TCP（interleaved）优先，UDP 可选。
- **播放预览**：抽象为 `IMediaPlayer`，实现候选 LibVLCSharp（LGPL-2.1）或 FFmpeg 进程；**MVP 不承诺预览**，未实现时明确标注 `Skipped / Not Supported`。

**分发许可警告**
本机已装 FFmpeg 9.0.2（gyan.dev `full_build`）。该类构建通常**包含 GPL 组件**，随商业产品分发需重新评估合规或改用 LGPL 构建；开发期仅作诊断参考，不进入发布包。

**红线**
RTSP URL 中的 userinfo 必须在 UI、日志、报告中统一脱敏。

## ADR-008 凭据存储

**决策**
- 秘密（密码、Token、私钥）经 **Windows DPAPI（CurrentUser 作用域）** 加密后落盘，业务层只持有 `SecretReference`。
- 项目导出默认剔除秘密；显式勾选时二次确认并给出明文警告。
- 禁止把密码放入命令行参数（避免进程列表泄露）。

**替代方案**
Windows Credential Manager（P/Invoke `CredWrite`）：跨用户共享更好，但 API 更重、可移植性差；如后续需要"系统级共享凭据"再评估。

## ADR-009 外部进程调用

**已接受（安全红线，不因便利性妥协）**
- 一律 `ProcessStartInfo.ArgumentList`（或等价参数数组），**禁止** `cmd.exe /c` 拼接用户输入。
- 固定可执行路径或白名单查找，限定超时，捕获退出码与 stdout/stderr，记录工具版本。
- 用户输入若需进入参数，必须做类型/范围校验。

## ADR-010 HTML 报告

**决策**
- 内置 HTML 模板（自研，无模板引擎依赖）+ `System.Text.Encodings.Web.HtmlEncoder` 转义所有外部数据。
- 报告生成前执行一次敏感信息扫描；报告内标注产品版本、生成时间、脱敏说明。
- 区分"观测事实"与"推断建议"两个区块。

**理由**
避免为报告引入模板引擎（及其供应链与转义陷阱）；转义交给标准库更可靠。

## ADR-011 插件与原生 SDK

**决策**
- 先定义进程内契约 `IDiagnosticAdapter`（Id / DisplayName / ApiVersion / Capabilities / ExecuteAsync）。
- 涉及**不稳定原生 SDK**（海康、大华）、驱动（Npcap/Pktmon）、不可信第三方模块时，一律**独立进程 + 进程间协议**隔离。
- 插件清单记录：Id、Version、ApiVersion、Capabilities、Publisher、Hash、SignatureStatus、权限提示。
- 插件**不得**直接访问宿主凭据库。

**待定**
海康/大华 SDK 的版本、位数、授权与再分发条款须在 LF-012 前逐项核验并书面记录；在此之前不引入任何厂商二进制。

## ADR-012 打包与分发

**状态：待定（LF-013 关闭）**

候选：
1. 便携 zip（MVP 首选，无需管理员权限）
2. MSIX（需签名证书，更新体验好）
3. WiX/Inno Setup（传统安装器，卸载与升级可控）

约束：抓包驱动（Pktmon/Npcap）不得成为 MVP 的强制依赖；若引入必须明示管理员权限与卸载残留。

## ADR-013 与既有资产的边界

**背景**
工作目录 `C:\Users\0x00\Documents\中国铁塔` 下已有：
- `towerlens/`：Python + pywebview 的"铁塔视联联调台"，24 个平台接口，含签名/AES 实现与已打包 exe。
- 多份平台 API 与安全评估资料（`视联平台API要点.md`、`api_spec_full.txt`、`sso_*` 等）。

**决策**
- LinkForge 独立新建工程，**不修改、不覆盖** `towerlens`。
- 可复用**领域知识与样例数据**（签名算法、接口清单、脱敏后的响应样例）用于 LinkForge 的 HTTP 适配器测试夹具，需确认不含真实凭据。
- `towerlens` 继续作为轻量工具存在；是否合并/迁移留待 MVP 后评估。

## 13. 依赖登记表（随引入更新）

引入任何依赖时补一行；许可证与版本以 NuGet 页面实测为准。

| 依赖 | 用途 | 版本 | 许可证 | 引入任务 | 替代方案 |
| --- | --- | --- | --- | --- | --- |
| CommunityToolkit.Mvvm | MVVM 基础设施 | 待定 | MIT（待复核） | LF-002 | 手写 INotifyPropertyChanged |
| WPF-UI | Fluent 控件 | 待定 | MIT（待复核） | LF-002 | 自研样式 |
| Microsoft.EntityFrameworkCore.Sqlite | 持久化 | 待定 | MIT（待复核） | LF-004 | Dapper + 手写 SQL |
| Serilog | 结构化日志 | 待定 | Apache-2.0（待复核） | LF-005 | Microsoft.Extensions.Logging |
| MQTTnet | MQTT 客户端 | 待定 | MIT（待复核） | LF-008 | Paho C |
| xUnit / Shouldly | 测试与断言 | 待定 | Apache-2.0 / BSD-3（待复核） | LF-001 | NUnit / 内置断言 |

**注**：FluentAssertions 8.x 起为商业许可，本项目不采用，统一用 Shouldly。

---

## ADR-014 默认主题策略

**背景**
`UI_WIREFRAMES.md` §10 写"深色为默认候选主题"，UI 规范 §2.1 要求"主体使用浅色主题，提供完整深色主题；跟随系统主题作为默认设置"。两者表述不一致。

**决策**
默认"跟随系统"；浅色为**设计基准**（令牌以浅色值先定，再派生深色值，但深色需独立校核对比度，不做机械反色）；深色为完整对等主题。

**理由**
规范为 UI 强约束且版本更新（V1.1 > 线框 V1.0），线框原文为"候选"非承诺；"跟随系统"是 Windows 11 原生习惯，符合规范 §1"原生感"目标。

**影响**
阶段 01 需同时交付两套完整令牌；阶段 06 需对深色单独做对比度验收。

**待确认**：D-03（用户确认）。

## ADR-015 令牌命名空间

**决策**
自定义语义令牌统一使用 `Lf` 前缀，例如 `LfColorTextPrimary`、`LfSpacingUnit`、`LfRadiusPanel`、`LfControlHeight`。

**理由**
WPF-UI 4.3.0 会注入大量自身资源键（`ApplicationBackgroundBrush`、`TextFillColorPrimaryBrush` 等）。若使用通用名，令牌可能被控件库默认值覆盖或反之，导致主题切换时出现"部分控件变、部分不变"的隐性 bug（对应风险 R-06）。

**约束**
业务页面只引用 `Lf*` 键；控件样式可将 `Lf*` 映射到 WPF-UI 期望的键，但映射集中在一处维护。

## ADR-016 状态模型分层

**背景**
`ARCHITECTURE.md` §3.2 定义步骤级 7 态；UI 规范 §4.8 定义任务级 7 态（含"部分成功"）。二者语义不同但易被实现者合并。

**决策**
保留两个独立枚举：

- `DiagnosticStatus`（步骤层）：`Passed / Failed / Warning / Skipped / Unknown / Cancelled / TimedOut`
- `TaskRunState`（任务层）：`Pending / Preparing / Running / Completed / PartiallySucceeded / Failed / Cancelled`

并提供**显式映射函数**（可单元测试）：

| 任务层 | 判定条件 |
| --- | --- |
| `Completed` | 所有步骤 `Passed`（或 `Warning` 且无失败） |
| `PartiallySucceeded` | 至少一个 `Passed`，且至少一个 `Failed`/`TimedOut` |
| `Failed` | 无 `Passed` 且存在 `Failed`/`TimedOut` |
| `Cancelled` | 用户取消 |
| `Preparing` / `Running` | 执行中 |
| `Pending` | 未开始 |

**红线**
- `Skipped` 不计入失败，但必须在结论中说明跳过原因。
- `Unknown` 不得归入 `Completed`。
- 不得把 `Skipped`/`Unknown` 渲染为 `Failed`（UI 规范 §5.4、§5.6）。

**待确认**：需回写 PRD，见 `PRD_DELTA.md` PD-01。

---

## ADR-017 主题令牌必须用 DynamicResource 引用

**状态**：已采纳（阶段 01 实施期间由运行时截图发现并验证）

**背景**
阶段 01 首次实现后，在深色主题下截图发现：主窗口头部与令牌卡片仍为白色，
按钮在深色背景上文字不可读，即"只有一部分元素跟随主题"。

**根因（已实测确认）**
`StaticResource` 在 XAML 解析时求值一次并持有结果对象。
`ThemeManager` 通过替换 `Themes/Tokens/Palette.*.xaml` 字典来切换主题，
但已用 `StaticResource` 解析过的元素不会重新求值，因此保持旧主题颜色。

已用测试锁定该机制（`ResourcePropagationTests`）：

- `DynamicResource` 引用在调色板替换后**跟随变化**；
- `StaticResource` 引用在调色板替换后**保持不变**。

**决策**
所有颜色/画刷类令牌（`LfBrush*`）在视图与样式中的引用**必须**使用 `DynamicResource`。
尺寸、间距、字号、圆角等不随主题变化的令牌继续使用 `StaticResource`（更高效）。

**约束**
- `tests/LinkForge.App.Tests/DesignTokens/ThemeResourceUsageTests.cs` 静态扫描
  `src/LinkForge.App/**/*.xaml`，发现 `{StaticResource LfBrush*}` 即失败
  （`Themes/Tokens/` 目录内除外——那里是调色板自身的 Color -> Brush 组装）。
- 新增页面时若出现"主题切换后部分区域不变色"，首先检查是否误用了 `StaticResource`。

## ADR-018 控件库资源键映射集中在调色板内

**状态**：已采纳（阶段 01 实施期间由运行时截图发现并验证）

**背景**
在按钮上直接设置 `Background="{DynamicResource LfBrushSurface}"` 后，
深色主题下被点击/悬停的按钮出现"浅蓝底 + 近白字"、文字几乎不可读。

**根因（已实测确认）**
WPF-UI 4.3.0 的 `Button` 默认模板不读取 `Border.Background`，
而是通过 `DynamicResource` 读取自身资源键：
`ButtonBackground`、`ButtonBackgroundPointerOver`、`ButtonBackgroundPressed`、
`ButtonForeground`、`ButtonForegroundPointerOver`、`ButtonBorderBrush` 等。
进入 `PointerOver`/`Pressed` 状态时，模板触发器用控件库的浅色画刷覆盖局部设置值。

同时确认：**显式 `Style` 会替换 WPF-UI 的隐式按钮样式**，回退到旧版 Aero2 模板
（其悬停色为硬编码浅蓝），使问题更严重。

**决策**
- 保持 WPF-UI 隐式样式，不自建按钮 `Style` 覆盖模板。
- 在 `Palette.Light.xaml` / `Palette.Dark.xaml` 内集中映射控件库资源键
  （`Button*`、`AccentButton*`），值全部引用 `Lf*` 颜色令牌。
- 映射写在调色板内，使主题切换时这些键随字典一起重建，模板的 `DynamicResource` 立即解析到新值。

**理由**
符合 ADR-015"映射集中在一处维护"的约束；避免在页面里逐处覆盖控件库内部键而分散失控。

**约束**
- 键名以 WPF-UI 4.3.0 实测为准；升级 WPF-UI 后需重新枚举模板触发器并核对映射。
- 阶段 02 若需自定义按钮视觉，必须同时提供正常/悬停/按下/禁用/焦点/忙碌六态，
  且不得回退到控件库默认的浅色悬停值。
