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
| ADR-004 | 日志与脱敏 | JSONL 结构化日志 + 统一 Redaction 层 | 已采纳（实现细节见 ADR-033） |
| ADR-005 | HTTP/API | HttpClientFactory + 自研请求模型 | 提议 |
| ADR-006 | MQTT | MQTTnet（纯托管），不用 Paho C | 提议 |
| ADR-007 | RTSP/媒体 | 自研信令诊断 + 播放组件抽象 | 提议（分发许可待定） |
| ADR-008 | 凭据存储 | Windows DPAPI（CurrentUser）+ SecretReference | 已采纳（实现见 ADR-039） |
| ADR-009 | 外部进程调用 | `ProcessStartInfo.ArgumentList`，禁止 shell 拼接 | 已接受（安全红线） |
| ADR-010 | HTML 报告 | 内置模板 + `HtmlEncoder` 转义 | 提议 |
| ADR-011 | 插件与原生 SDK | 进程内契约先行，原生 SDK 进程外隔离 | 提议 |
| ADR-012 | 打包与分发 | V1.0 采用未签名 Inno Setup 用户级双架构安装器 + 双架构便携包 | 已采纳（见 ADR-036） |
| ADR-013 | 与既有资产的边界 | 独立新建，不修改 `towerlens` | 已接受 |
| ADR-014 | 默认主题策略 | 跟随系统，浅色为设计基准，深色对等 | 提议（待 D-03 确认） |
| ADR-015 | 令牌命名空间 | 语义令牌统一 `Lf` 前缀 | 提议 |
| ADR-016 | 状态模型分层 | 任务层与步骤层分离，显式映射 | 提议 |
| ADR-017 | 主题令牌引用方式 | 颜色令牌一律 `DynamicResource` | 已采纳 |
| ADR-018 | 控件库资源键映射 | 集中在调色板内，保留控件库模板 | 已采纳 |
| ADR-019 | 模板内令牌引用方式 | 控件模板内一律 `DynamicResource` | 已采纳 |

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
- 结构化日志：零新增依赖的 JSON Lines 文件，由应用级聚合服务向 UI
  提供最多 5000 条内存记录；不引入 Serilog。
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

**状态：已由 ADR-036 扩展（2026-10-22 预览阶段先采用便携 ZIP；2026-10-24
V1.0 增加 Inno Setup 双架构用户级安装器）**

候选：
1. 便携 zip（MVP 首选，无需管理员权限）
2. MSIX（需签名证书，更新体验好）
3. WiX/Inno Setup（传统安装器，卸载与升级可控）

**决策**
- 预览阶段 LF-013 只交付未签名的 `win-x64` 自包含便携 ZIP，不把 ZIP 包装成安装器。
- 预览版本为 `0.5.0-preview.1`，升级方式是退出后替换程序目录；用户数据位于
  `%LOCALAPPDATA%\LinkForge`，不随程序目录替换自动删除。
- V1.0 已增加双架构 Inno Setup 用户级安装器；代码签名、时间戳、SmartScreen
  与自动回滚仍未完成。详见 ADR-036。

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
| Serilog | 结构化日志 | 未引入 | Apache-2.0 | LF-005 未采用 | 内置 JSONL + 统一 Redaction（ADR-033） |
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

## ADR-019 控件模板内一律使用 DynamicResource

**状态**：已采纳（阶段 02 实施期间由运行时崩溃发现并验证）

**背景**
阶段 02 完成基础组件后，应用启动即抛
`XamlParseException: 无法找到名为"LfFontSizeCaption"的资源`，
而同一时刻 `dotnet build` 与全部 185 个测试均为绿色。

**根因（已实测确认）**
控件模板是**延迟实例化**的。模板内的 `StaticResource` 在
`FrameworkTemplate.LoadContent` 阶段求值，
而那时模板根元素尚未进入逻辑树，`StaticResource` 沿逻辑树向上查找必然落空。

`Dimensions.xaml` 里的令牌确实存在，但它们挂在
`ThemeResources.xaml` 这层**嵌套**字典内；
`MainWindow` 的 XAML 引用是可以解析的（Window 自身在逻辑树里），
模板内的引用则不行——这就是"Window 能显示、模板一应用就崩"的原因。

**决策**
`Themes/Controls/*.xaml` 内所有 `Lf*` 令牌引用一律使用 `DynamicResource`，
不论其类型是颜色、画刷、尺寸、间距还是字号。

**与 ADR-017 的关系**
ADR-017 只要求颜色类令牌用 `DynamicResource`（为跟随主题替换）。
本条把范围**扩大到全部令牌类型**，理由不同：
这里不是为了跟随主题，而是因为模板求值时不在逻辑树上。
因此即便是不随主题变化的 `LfFontSizeCaption`，在模板内也必须用 `DynamicResource`。

**已用 Mutation 验证的例外**
`Converter={StaticResource LfBoolToVisibility}` **必须保持 StaticResource**。
`Binding.Converter` 不是依赖属性，`DynamicResource` 在此会直接抛
"只能在 DependencyObject 的 DependencyProperty 上设置 DynamicResourceExtension"。
批量替换时若不加区分，会把 9 处转换器引用改坏。

**约束**
- 静态守卫 `Controls/ResourceReferenceTests` 扫描 `Themes/Controls/*.xaml`，
  发现 `{StaticResource Lf*}` 即失败，仅豁免 `Converter=` 位置。
- 新建控件模板时，任何 `Lf*` 引用默认写 `DynamicResource`。

## ADR-020 供模板绑定的属性必须是依赖属性

**状态**：已采纳（阶段 02 实施期间由渲染测试发现并验证）

**背景**
`LogViewer` 的 `View` 属性曾是普通 CLR 属性
（`internal ICollectionView? View => _viewSource?.View;`），
而模板里写的是 `ItemsSource="{Binding View, RelativeSource=TemplatedParent}"`。

**症状**
构造 `LogViewer` 时直接赋 `Entries` 完全正常
（那时 `OnApplyTemplate` 尚未执行，视图在首次布局时才建立）。
但**模板已应用之后再替换 `Entries`**，视图对象换了、绑定却收不到任何通知，
结果 `Items` 仍为空——界面表现为"换了日志源，一行都不显示"。

这是本产品的高频场景：诊断任务跑完一批、再跑下一批就是换数据源。

**根因**
WPF 绑定只监听数据源发出的变更通知。普通 CLR 属性没有通知机制，
属性指向的对象被整体替换后，绑定仍持有旧值（或 null）。

**决策**
任何供模板绑定的属性都必须是依赖属性（读写或只读皆可），
并在内部重建对象时通过依赖属性赋值把变更推送出去。

**约束**
- 契约测试 `LogViewerContractTests` 用反射断言
  `View` / `Entries` / `MinimumLevel` / `SearchText` / `AutoScroll`
  均存在对应 `DependencyProperty` 静态字段。
  新增供模板绑定的属性时若不遵守，测试立即失败。
- 渲染测试 `Replacing_entries_after_the_template_is_applied_should_repopulate_rows`
  从行为侧锁定同一缺陷：先渲染、后换源、断言行数更新。
  行为测试与契约测试并行，避免"改对了结构却没人验证行为"。

## ADR-021 WPF-UI 主题变更必须回灌 Lf* 令牌

**状态**：已采纳（阶段 03 实施期间由真实窗口启动发现并验证）

**背景**
阶段 03 的壳层窗口使用控件库的 `SystemThemeWatcher.Watch` 监听系统主题。
该调用在注册**第一个**窗口时会强制执行 `ApplicationThemeManager.ApplySystemTheme()`，
系统主题变化时也会再次调用。

**症状**
窗口出现"半截主题"：控件库提供的部分（按钮、输入框、滚动条）跟着系统变成深色，
而 `Lf*` 令牌驱动的部分（窗口背景、文字、导航、状态栏）仍是浅色。
两者拼在一个窗口里，视觉上像渲染错乱。

**根因**
`ApplicationThemeManager.Apply*` 只替换 **WPF-UI 自己的** 字典，
**不会**替换 `Themes/Tokens/Palette.*.xaml`。
而本项目的颜色全部来自调色板字典（ADR-015），两套主题系统互不知情。

这与 ADR-017 要修的缺陷同源，只是触发路径不同：
ADR-017 是"用户点了主题按钮但令牌没换"，本条是"控件库自己换了但令牌没换"。

**决策**
订阅 `ApplicationThemeManager.Changed`，在事件里调用
`ThemeManager.SyncWithWpfUiTheme()`，把 `Lf*` 令牌拉回**用户偏好**对应的主题。

必须用 `ApplyResolved` 而不是 `Apply`：后者会改写 `Preference`，
把"跟随系统"意外固化成事件发生那一刻的系统主题。

**终止性**
若用户显式选了浅色/深色，本方法会把控件库主题改回去。
`ApplicationThemeManager.Apply` 只在字典**确实变化**时抛 `Changed`，
因此第二次进入时偏好与实际一致、不再产生事件；
`_isSyncingWithWpfUi` 标志是额外的硬保险，确保任何版本的控件库都不会把这里变成死循环。

**约束**
- 窗口在 `Closing` 时解绑 `Changed`（与 `SystemThemeWatcher.UnWatch` 同一时机，见 ADR-023）。
- `ShellViewModelTests` 断言：`OnSystemThemeChanged` 在非"跟随系统"偏好下不生效；
  `SyncThemeWithWpfUi` 能把显式主题拉回来。

## ADR-022 图标字形集中管理并验证码位存在

**状态**：已采纳（阶段 03 实施期间由字形探测发现）

**背景**
导航与状态组件需要图标。候选字体有两个：
Windows 11 的 `Segoe Fluent Icons` 与 Windows 10 的 `Segoe MDL2 Assets`。

**实测结论（不可想当然）**
两套字体是**两套码表**，并非同一个字体的新旧版本：
`Wpf.Ui` 的 `SymbolRegular.Home24` = `0xF481`，
该码位在 `Segoe Fluent Icons` 中存在，在 `segmdl2.ttf` 中**缺失**。
因此"写一个看起来合理的码位、在字体族里排两个候选"这种做法会
在其中一个系统上渲染成空白方块（豆腐块），而编译与测试全绿。

**决策**
1. 所有图标码位集中在 `Navigation/LfGlyph.cs` 常量中，禁止在 XAML 里散写 `&#xE7BA;` 之类的字面量。
2. 字体族统一走 `LfIconFontFamily` 令牌，不各页各写字体名。
3. 新增 `GlyphTests`：用 `GlyphTypeface.CharacterToGlyphMap` 逐个断言
   本项目使用的**每一个**码位在 `SegoeIcons.ttf`（Fluent）**和** `segmdl2.ttf`（MDL2）中**都存在**。
4. 同时断言 `SymbolRegular.Home24`（`0xF481`）在系统字体中**缺失**，
   把"两套码表不通用"这一结论固化下来 —— 避免后人再次引入控件库符号常量。

**约束**
- 新增图标时若只存在于其中一套字体，`GlyphTests` 立即失败并指出码位。
- `Themes/Controls/*.xaml` 内仍允许写图标字面量（模板需要），
  但必须使用双字体都存在的码位，由同一组测试覆盖。

## ADR-023 截图证据必须显式设定被测状态并固定窗口位置

**状态**：已采纳（阶段 03 验收期间发现阶段 02 遗留的证据缺陷）

**背景**
阶段 02 起，验收证据是 `screenshots/` 下的浅/深色 PNG。
阶段 03 加入壳层截图后，全量测试跑出来的阶段 02 截图发生了变化，
排查过程中发现这条证据链上有**三个独立的缺陷**，都会让"截图是对的"这一结论失真。

**缺陷 1：证据根本没设定主题（阶段 02 遗留）**
`MainWindowScreenshotTests` 原本只在 `Application.Current is App` 时注入 `ThemeManager`。
而测试宿主里的 `Application.Current` 是**基类** `Application`（见 `StaRunner` 的说明），
该分支**永不成立**。后果：
- 主题从未被显式设定，截图反映的是"上一个测试残留的调色板"；
- `(Application.Current as App)?.ThemeManager` 在测试里恒为 null，
  状态栏永远显示"浅色"。
实测确认：阶段 02 提交的 `stage02-mainwindow-light.png` 与 `-dark.png`
**SHA-256 完全相同**（均为 `D1AB10E7...`）——所谓"两张主题截图"其实是同一张。
而当时的用例只断言"文件非空、非全透明"，所以全绿。

**缺陷 2：证据依赖测试执行顺序**
截图类之间通过进程级的 `ApplicationThemeManager` 全局状态互相影响：
单独跑 `MainWindowScreenshotTests` 与连跑两个截图类，得到**不同字节**的"浅色"截图。

**缺陷 3：证据依赖窗口落点（ClearType 子像素相位）**
即使主题一致，窗口默认落在屏幕不同位置时，
ClearType 的子像素抗锯齿结果不同，同一主题的两张截图产生约 **0.9%** 的像素差异。
实测：固定 `Left`/`Top` 后差异消失。

**决策**
1. 截图测试必须**显式**设定被测状态，不得依赖环境残留。
   `MainWindow` 增加 `internal ThemeManager` 注入点（生产路径仍从 `App` 取）。
2. 必须断言"两个主题真的不同"（差异像素 > 5%），
   而不只是"文件非空"。只断言非空等于没断言主题。
3. 截图窗口固定 `WindowStartupLocation = Manual`、`Left = 0`、`Top = 0`。
4. 关闭 xunit 的测试集合并行（`xunit.runner.json` 的
   `parallelizeTestCollections: false`）：
   被测对象共享进程级 WPF 全局状态（主题、Application 资源），
   并行执行会让结果不可复现。

**约束**
- 新增截图测试必须走与既有测试相同的 `Render(...)` 入口，
  避免"修好一处、漏改另一处"导致证据互相矛盾。
- 已知限制（如实声明）：即使固定位置，跨**进程**运行仍可能因
  首帧字形缓存预热产生极小差异（实测 0.87%，集中在按钮边框与文字边缘）。
  因此截图用于**人工目视比对**，不作为字节级回归基线。


## ADR-024 Plugin.Abstractions 依赖 Domain；Application 依赖 Plugin.Abstractions

**状态**：已采纳（阶段 04 实施期间发现既有依赖边缺失）

**背景**

阶段 04 引入 `IDiagnosticAdapter`（Plugin.Abstractions）与 `DiagnosticTaskRunner`（Application）。
两个实际问题必须在编译期解决：

1. 适配器的 `Plan` / `ExecuteStepAsync` 需要返回 `DiagnosticStepResult`、引用 `DiagnosticStatus`、
   `EvidenceArtifact`——这些类型都在 Domain。
2. Application 的编排器要消费适配器返回的领域结论，必须引用 Plugin.Abstractions。

原架构文档的依赖图只写了 `App → Application → Domain` 与 `App → Plugin.Abstractions`，
没有覆盖这两条边，导致按原文实现会出现"契约引用不到领域类型"或"编排器引用不到契约"的编译错误。

**决策**

两条新依赖边正式纳入架构约束：

- `LinkForge.Plugin.Abstractions → LinkForge.Domain`（契约必须能表达领域结论）
- `LinkForge.Application → LinkForge.Plugin.Abstractions`（编排器消费契约）

依赖图（完整）：

```
LinkForge.App
  ├→ LinkForge.Application
  │    ├→ LinkForge.Domain
  │    └→ LinkForge.Plugin.Abstractions ─→ LinkForge.Domain
  ├→ LinkForge.Domain
  └→ LinkForge.Plugin.Abstractions
```

**约束**

- Domain 仍然是叶子：不得引用任何其他项目（`ArchitectureTests` 已锁定）。
- Plugin.Abstractions 只允许引用 Domain，不得反向引入 Application / App。
- Application 不得引用 WPF 程序集（`ArchitectureTests.Application_assembly_should_not_reference_wpf_assemblies` 已锁定）。

**配套实现约束（阶段 04 竞态缺陷沉淀）**

Application 层编排器与 App 层视图模型共享一条红线：
**凡是"投递/通知"之后还要读取被改集合的调用点，投递必须可等待。**
fire-and-forget 的 UI 投递（`SynchronizationContext.Post` 不等待）会让编排器读到旧依赖快照、
让"清空历史后列表仍有数据"——这类缺陷编译期无感知，测试能暴露但容易被误判为"偶发"。
`DashboardViewModel.PostAsync` 以 `TaskCompletionSource` 实现；后续业务页面沿用同一模式。

## ADR-025 网络适配器独立成项目并统一经过任务编排器

**状态**：已采纳（阶段 05 / LF-006）

**背景**

网络基础诊断需要 Ping/ICMP、TCP、DNS、本机网卡/路由和 Traceroute。
如果把这些系统 API 直接写进 WPF 页面或视图模型，会出现三个问题：

1. UI 线程可能被同步的系统调用或进程启动阻塞；
2. 每个页面都要自行处理超时、取消、异常和状态映射，口径容易漂移；
3. 测试只能依赖真实网卡、公网或系统 `tracert.exe`，不可稳定复现。

**决策**

1. 新增 `LinkForge.Adapters.Network`，由 `WindowsNetworkProbe` 封装系统边界，
   `NetworkDiagnosticAdapter` 只负责把观察结果映射为领域步骤结论。
2. `LinkForge.App` 只注入具体适配器，网络页通过
   `DiagnosticTaskRunner` 执行，不直接调用 `Ping`、`Dns`、`TcpClient` 或 `Process`。
3. 本机网络快照必须在线程池读取；所有网络操作必须响应取消令牌。
4. 结构化事实与原始输出在进入 UI、复制、导出前统一脱敏；
   `LogEntry.TargetId` 同样在构造时脱敏。
5. ICMP 无响应、Traceroute 中间跳点无响应只能得到 `Unknown`，
   不得自动升级为“主机离线”或“目标失败”。

**依赖边**

```text
LinkForge.App
  ├→ LinkForge.Adapters.Network
  │    ├→ LinkForge.Domain
  │    └→ LinkForge.Plugin.Abstractions ─→ LinkForge.Domain
  ├→ LinkForge.Application ─→ LinkForge.Plugin.Abstractions
  └→ LinkForge.Domain
```

**约束**

- Adapters.Network 不得引用 App 或 Application。
- UI 不得绕过编排器直接执行探测。
- 外部目标必须显式授权；默认只执行用户选择的单个工具，
  不自动扩大端口或网段范围。
- 新增网络能力必须同时补成功、失败、超时、取消和未知结论测试。

## ADR-026 HTTP 适配器独立成项目并禁止 UI 直连 HttpClient

**状态**：已采纳（阶段 05 / LF-007）

**背景**

API 工作台需要真实的 HTTP 请求、超时、取消、重定向、响应截断和失败分类。
如果页面或视图模型直接创建 `HttpClient`，会出现：

1. TLS、重定向、Cookie、超时和取消策略散落在 UI 代码中；
2. 4xx/5xx 容易被误判为网络失败；
3. 凭据可能随请求对象进入历史、日志或导出；
4. 集成测试依赖公网，无法稳定复现。

**决策**

1. 新增 `LinkForge.Adapters.Http`，以 `IHttpApiTransport` 暴露单一发送边界；
   UI 只依赖该接口。
2. 传输层不提供关闭 TLS 证书验证的选项，不启用自动 Cookie；
   URL userinfo 直接拒绝，认证只能走显式字段。
3. 收到任何 HTTP 响应（含 4xx/5xx）返回 `HttpApiResponse`；
   DNS/连接/TLS/超时/非法请求才抛出分类后的传输异常。
4. 响应按字节上限读取并返回截断标志，避免超大响应占满内存。
5. App 层负责历史、日志、复制与导出脱敏；
   敏感 Header 按名称整体遮蔽，而不是只依赖值的内容猜测。

**依赖边**

```text
LinkForge.App ─→ LinkForge.Adapters.Http
LinkForge.App ─→ LinkForge.Application / Domain
LinkForge.Adapters.Http 不引用 App / Application / WPF
```

**约束**

- 页面和视图模型不得直接创建 `HttpClient` 或 `SocketsHttpHandler`。
- 集成测试使用本地 mock server，不依赖公网。
- 新增请求能力必须同时覆盖成功、HTTP 错误、传输失败、超时、取消与截断。

## ADR-027 MQTT 适配器独立成项目并采用 MQTTnet 5.2.0.1603

**状态**：已采纳（阶段 05 / LF-008）

**背景**

MQTT 工作台需要真实连接、断开、订阅、发布和消息流。如果页面或视图模型直接
创建 MQTTnet 客户端，会产生协议库版本泄漏、连接生命周期跨导航丢失、
凭据/载荷进入 UI 导出和集成测试依赖公网等问题。

**决策**

1. 新增 `LinkForge.Adapters.Mqtt`，以 `IMqttTransport` 暴露传输边界；
   页面与视图模型不直接引用 MQTTnet。
2. 采用 MQTTnet `5.2.0.1603`，MIT；2026-10-24 增量支持 MQTT 3.1.1 / 5.0
   显式选择，默认 3.1.1。选择结果必须真实传入传输层并记录在摘要中。
3. TLS 证书验证始终启用；`MqttTransport` 不提供关闭证书验证、忽略链错误
   或忽略吊销错误的选项。
4. 用户名/口令不进入日志、订阅列表、复制与导出；Topic 与载荷构造为
   领域对象时即脱敏。
5. `MqttTransport` 同时实现 `IAsyncDisposable` 与 `IDisposable`。
   WPF `Application.OnExit` 会同步释放 DI 容器；只有 `IAsyncDisposable`
   会让真实关闭路径崩溃。
6. 集成测试使用本地进程内 Broker，不依赖 Docker、公网或固定外部服务。

**依赖边**

```text
LinkForge.App ─→ LinkForge.Adapters.Mqtt
LinkForge.App ─→ LinkForge.Application / Domain
LinkForge.Adapters.Mqtt 不引用 App / Application / WPF
```

**约束**

- UI 不得绕过 `IMqttTransport` 直接创建 `MqttClientFactory`。
- Broker 断线与主动断开必须分别表达；断线后不得继续显示“已连接”。
- 消息筛选只影响显示与复制/导出范围，原始缓冲仍需保留完整消息。
- 新增 MQTT 能力必须同时覆盖认证失败、连接失败、订阅失败、发布失败、
  取消、Broker 断线、参数非法与协议映射错误。

---

## ADR-028：RTSP 分阶段信令诊断（LF-009）

**决策时间**：2026-10-21

**背景**

阶段 05 需要接入视频/RTSP 能力，支持真实 RTSP 目标的连接、信令与 SDP 诊断。
海康/大华 SDK 尚未就位（D-09），完整媒体播放需要 RTP 接收与解码，
超出本阶段范围。

**决策**

1. 新增 `LinkForge.Adapters.Video`，以 `IRtspProbe` 暴露 RTSP 信令诊断边界；
   页面与视图模型不直接引用适配器内部类型。
2. 分阶段执行：DNS → TCP → RTSP OPTIONS → DESCRIBE → SDP 解析；
   RTP、SETUP/PLAY、解码、首帧未执行，明确标记 `Skipped`。
3. URL 支持 `rtsp://`、`rtsps://`、IPv4、IPv6、userinfo；
   userinfo 仅用于 Basic 认证，请求行不携带 userinfo。
4. 401 后按 `WWW-Authenticate` 判断认证方案；当前仅支持 Basic 重试，
   Digest 明确失败，不伪造成功。
5. RTSP URL 密码在显示、日志、事实、复制与导出中统一脱敏。
6. 集成测试使用本地 `LocalRtspServer`，真实 TCP 和 RTSP 报文，
   不依赖 Docker、公网或固定外部服务。

**依赖边**

```text
LinkForge.App ─→ LinkForge.Adapters.Video
LinkForge.App ─→ LinkForge.Application / Domain
LinkForge.Adapters.Video 不引用 App / Application / WPF
```

**约束**

- UI 不得绕过 `IRtspProbe` 直接创建 RTSP 客户端。
- 未实现的阶段（RTP/解码/首帧）必须明确标记 `Skipped`，不伪造结果。
- 当前结论只能证明"RTSP 信令和 SDP 可用"，不能证明"视频已经播放"。
- 新增 RTSP 能力必须同时覆盖 DNS 失败、TCP 超时、OPTIONS 超时、
  401/认证失败、无效 SDP、用户取消与 IPv6。
- RTSP 页空状态必须明确说明媒体接收/解码/首帧未执行。

---

## ADR-029：报告服务独立成项目并强制生成前扫描（LF-010）

**决策时间**：2026-10-21

**背景**

阶段 05 需要把真实诊断结果导出为可离线交付的 HTML 报告。
如果报告模板、脱敏规则与 WPF 页面混在一起，会出现：

1. 报告格式与某个页面耦合，新增协议必须修改 UI 工程；
2. 模板引擎的转义差异（`{{ }}` 与 `{{{ }}}`）带来 HTML 注入风险；
3. 扫描与脱敏共用同一份正则，同一缺陷会让两者同时失效；
4. 写入中断留下半个 HTML，用户误以为报告完整；
5. 扫描命中后仍写出文件，把“检测泄漏”变成“制造泄漏”。

**决策**

1. 新增 `LinkForge.Reporting`（`net10.0`，不引用 WPF），
   以 `IReportGenerator` 暴露“扫描 / 生成”两个契约。
2. 不使用模板引擎：`ReportHtmlRenderer` 是唯一模板，
   所有外部数据只经过 `WebUtility.HtmlEncode` 一处编码；
   属性一律双引号包裹，输出不含脚本、外链样式、字体或图片。
3. 生成流程固定为“扫描 → 渲染 → 原子写入”。敏感信息扫描独立于
   `SecretRedactor` 实现，避免共用正则导致同时失效；
   扫描结果只记录位置与类别，不保存命中原文。
4. 扫描不通过时**绝不写文件**，包括不创建 `.tmp`；
   写入使用临时文件 + `File.Move(overwrite)` 原子替换。
5. 报告使用 UTF-8 无 BOM，保证 `<!DOCTYPE html>` 位于文件首字节。
6. 取消与写入失败用 `ReportGenerationResult` 表达，
   不向 UI 抛 `OperationCanceledException`；非法路径转换为可读反馈。
7. 报告数据源只有 `IRunHistoryStore` 的真实任务历史；
   不提供示例报告，不重新推断结论。

**依赖边**

```text
LinkForge.App ─→ LinkForge.Reporting ─→ LinkForge.Application / Domain
LinkForge.App ─→ LinkForge.Application / Domain
LinkForge.Reporting 不引用 App、适配器或 WPF
```

**约束**

- 报告中的状态、计数、步骤与建议必须与任务历史逐字一致，
  不得为提高可读性而改写结论。
- 报告版式必须区分“观测事实”“推断建议”与“数据范围声明”。
- 新增报告字段必须同时加入 `ReportDocument.EnumerateTexts()`，
  否则该字段不会进入生成前扫描。
- `Skipped` 不得渲染为失败，`Unknown` 不得渲染为通过，
  `Cancelled` 不得渲染为失败。
- 报告页属性更新必须回到 UI 线程；否则会出现“数据已空但占位不出现”。
- 新增报告能力必须覆盖：转义注入、扫描阻断、取消、写入失败、
  无 BOM、自包含离线与中文字符保真。

---

## ADR-030：开发者工具采用零依赖离线转换库（LF-011）

**决策时间**：2026-10-21

**背景**

阶段 05 需要提供 JSON、URL、Base64、时间戳、UUID 与 JWT 等常用调试能力。
如果把这些逻辑直接写在 WPF 视图模型里，会出现：

1. 转换逻辑无法脱离 UI 做边界测试；
2. 大输入在 UI 线程执行导致窗口假死；
3. JWT 容易被误认为“已验证”，或敏感载荷进入日志/历史；
4. URL 解码、Base64 与 JSON 的异常路径各自处理，错误语义不一致。

**决策**

1. 新增 `LinkForge.DeveloperTools`（`net10.0`，零第三方依赖），
   以 `DeveloperTransforms.Execute` 统一执行离线文本转换。
2. 输入按 UTF-8 字节预检，上限 **1 MiB**；超限返回明确失败，
   不截断后继续执行。
3. 转换在 `Task.Run` 中执行并支持取消；视图模型只负责状态、
   授权与结果展示，不直接做重计算。
4. JWT **仅解码**，不验证签名、有效期或签发者；界面必须醒目标注
   “签名未验证”，不得把可解码表述为令牌可信。
5. 非法 UTF-16 代理对、非法百分号序列、非法 Base64、无效 JSON/JWT
   均返回结构化可读失败，不把异常直接抛给 UI。
6. 工具不写日志、历史或报告；剪贴板通过 `IDeveloperResultClipboard`
   抽象，便于测试并限制副作用。若未来增加持久化，必须先接入统一脱敏。

**依赖边**

```text
LinkForge.App ─→ LinkForge.DeveloperTools
LinkForge.DeveloperTools 不引用 App / Application / WPF / 适配器
```

**约束**

- 本任务范围严格限定为 JSON、URL、Base64、时间戳、UUID 与 JWT 解码；
  XML / YAML、Hex、文件哈希、正则与文本差异不得以占位或假结果冒充已实现。
- 新增转换必须同时覆盖 Unicode、非法输入、边界时间戳与取消路径。
- 新增结果字段若进入日志、历史或报告，必须先经过统一脱敏。
- 大输入不得在 UI 线程执行，也不得用“截断后成功”掩盖超限。

## ADR-031：插件采用清单校验与进程外执行，签名只作声明（LF-012）

**背景**

PRD 要求接入海康 / 大华开放平台 SDK（ADR-011 已确定"原生 SDK 进程外隔离"）。
这类 SDK 是 C/C++ 原生库，存在三类无法在进程内安全处理的风险：
崩溃或 AccessViolation 会直接带走 WPF 主进程；SDK 版本与运行时位数冲突只能在加载时暴露；
一旦插件能引用宿主类型，凭据库就可能被间接读取。D-09（厂商 SDK 许可与再分发）至今未关闭，
因此本任务只能交付**宿主机制**，不能交付任何具体厂商适配。

**决策**

1. 新增 `LinkForge.Plugin.Hosting`（`net10.0`，零 NuGet 依赖），只依赖
   `LinkForge.Domain` 与 `LinkForge.Plugin.Abstractions`。
2. **不加载插件程序集**：每个步骤启动独立子进程，用标准输入/输出交换单行 JSON
   （`hello` 握手 + `executeStep`）。插件崩溃只终止子进程。
3. 清单严格校验：`manifest.json` 上限 64 KiB、JSON 深度 16、`id` 正则约束、
   字段长度上限、步骤数上限 64、超时 1–3600 秒；API 版本**精确匹配** `1.0`，
   不做"未来版本可能兼容"的推断。
4. 入口文件必须是插件目录内的相对路径，拒绝 `..`、拒绝重解析点，
   并**重新计算 SHA-256 与清单比对**；入口路径由宿主解析，不接受页面传入。
5. 架构校验按当前进程架构判定；x64 宿主可运行 x86 插件，反之拒绝。
6. **签名字段只有 `Unsigned` 与 `DeclaredSignedUnverified`，不存在 `Verified`。**
   宿主没有证书链与发布者信任配置，无法验证签名；提供"已验证"枚举值迟早会让界面把声明当事实。
   界面只能显示"声明已签名（未验证）"或"未签名"。
7. 运行需同时满足三道边界：清单校验通过、用户显式确认来源、入口路径重新解析。
   前两道在页面与执行器各判一次（`AsyncRelayCommand.Execute` 可绕过 `CanExecute`）。
8. 参数只下发 `AllowedParameters` 白名单键，并二次拒绝疑似秘密键名；
   宿主不向插件提供凭据库 API，插件只收到 `secretRef` 引用键。
9. **插件返回文本视为不可信来源**：`PluginSecretFilter` 在宿主出口对摘要、建议、
   事实与原始输出过滤凭据形态，页面层再逐字段脱敏，导出与复制同样处理。
10. 状态映射保守：崩溃映射为 `Failed`，未知状态保持 `Unknown`，
    取消继续抛 `OperationCanceledException` 交由编排器区分取消与超时；
    步骤标识不匹配或摘要为空时**不采纳**插件结论。
11. 结果上限：事实最多 32 条、原始输出截断至 64 KiB、摘要 2048 字符、单行响应字节上限。
12. 测试用真实子进程夹具（`LinkForge.TestPluginHost`），不 mock 进程边界；
    夹具不访问网络与文件。

**依赖边**

```text
LinkForge.App ─→ LinkForge.Plugin.Hosting ─→ { LinkForge.Domain, LinkForge.Plugin.Abstractions }
LinkForge.Plugin.Hosting 不引用 App / Application / WPF，且零 NuGet 依赖
```

**剩余风险（明确接受）**

- 插件以**当前用户身份**运行，仍可自行读取该用户可访问的文件与网络；
  同用户的恶意本地进程不受本边界保护。这不是沙箱，只是崩溃隔离与凭据不下发。
- 签名声明未经任何验证；哈希校验只能证明"入口文件与清单一致"，
  不能证明发布者身份，也不能防止发布者同时篡改清单与文件。
- 进程外协议无鉴权，依赖 stdin/stdout 管道；本机同用户进程理论上可附着子进程。

**约束**

- 不得把 `DeclaredSignedUnverified` 在任何界面、日志或报告中表述为"已验证"。
- 不得为图方便把插件改为进程内加载；那会同时丢掉崩溃隔离与凭据边界。
- 不得向插件下发秘密明文，也不得因"插件需要"而绕过参数白名单。
- 厂商 SDK（D-09）、ONVIF、RTP 与媒体解码不得以占位或假结果冒充已实现。
- 新增插件能力必须同时覆盖：清单非法、API 不兼容、哈希不符、入口越界、
  崩溃、静默超时、取消、未知状态与秘密回传九类路径。

## ADR-032：项目持久化采用 SQLite + EF Core 迁移，凭据只存引用键（LF-004）

**背景**

阶段 04 落地了 `Project` / `EnvironmentProfile` / `Asset` 三个领域模型，
但它们只是内存里的对象：应用一关，用户建的项目、配置的环境、登记的设备全部消失。
本阶段要解决的是"数据能落下去、重启还在、坏了能救回来"。

同时，工具箱类应用天然会接触到摄像头密码、Broker 口令这类东西，
所以"存什么"和"怎么存"必须一起设计，不能等写完存储再补过滤。

**决策**

1. **存储用 SQLite + EF Core 10.0.13**，文件位于
   `%LOCALAPPDATA%\LinkForge\linkforge.db`。
   选 SQLite 而不是 JSON 文件：资产要支持查询、排序、级联删除与唯一约束，
   这些在 JSON 上都得自己手写，且写一半崩溃就会损坏整个文件。
2. **结构升级走 EF Core 迁移**（首个迁移 `20261021193558_InitialCreate`），
   不手工改表。迁移使"旧版本数据库能被新版打开"成为可测事实。
3. **每次操作新建 DbContext**。DbContext 不是线程安全的，
   而诊断任务与界面会并发读写存储；共用长生命周期上下文会出现
   "另一个线程正在使用此上下文"这类只在现场偶发的中断。
   不用 `AddDbContext` / `IDbContextFactory`，是为了让生命周期语义留在本层，
   而不是取决于容器注册方式。
4. **契约在 Application，实现在 Persistence**。
   视图模型依赖 `IProjectStore` / `IEnvironmentStore` / `IAssetStore` 三个契约；
   换存储实现时不需要改任何调用方。
5. **数据库损坏必须隔离而非静默重建**：损坏文件改名为
   `linkforge.db.corrupt-<时间戳>` 保留现场，然后重建，并**如实告知用户**原数据已隔离。
   但"磁盘满 / 无权限 / 文件被占用"**不**当作损坏处理 —— 把这些错误当成数据损坏去重建是破坏性行为。
6. **凭据只存引用键**。领域模型与数据库表都不含任何秘密字段；
   资产的 `credential` 列只接受白名单键（字母、数字与 `.` `_` `-` `:`，≤128），
   真实密码几乎必然含白名单之外的字符，会在保存前被拦下。
   因此"项目数据里没有明文密码"是结构保证，而不是靠导出代码记得过滤。
7. **环境变量拒绝秘密语义**。变量名按分隔符与驼峰切成词段后比对，
   命中 `password` / `token` / `secret` / `key` 等词段的整行被拒绝并说明原因；
   命中判定是**按词段**而不是整串子串，否则 `TOKENIZER`、`COOKIEJAR` 会被误杀，
   逼用户给变量改名 —— 那比漏拦更难排查。
8. **CSV 手写 RFC 4180 编解码**，UTF-8 BOM + CRLF。
   导入的 URL 与裸 `userinfo` 凭据被剥离并给出警告；
   无效行给出行号与原因，成功行照常入库（部分成功是显式结论，不是异常路径）。
9. **资产 id 用独立白名单**（`.` `_` `-`，≤64，**不含** `:`），
   与凭据引用键分开校验：两者字符集不同，混用会导致其中一方被放宽。

**依赖边**

```text
LinkForge.App ─→ LinkForge.Persistence ─→ { LinkForge.Application, LinkForge.Domain }
LinkForge.Persistence 不引用 App / WPF / 任何适配器
LinkForge.Application 与 LinkForge.Domain 都不依赖 EF Core
```

**剩余风险（明确接受）**

- 数据库文件本身**不加密**。只是不存明文凭据，不等于文件被保护。
- 损坏隔离靠"改名保留"，占用的是同一目录；磁盘满时隔离也会失败，此时直接抛可读错误。
- `CredentialReference` 在 LF-004 交付时只是指针，尚未有凭据库本体。
  V1.1 的 LF-016 已补齐 Windows CurrentUser DPAPI 凭据库，见 ADR-039。

**约束**

- 不得让界面直接 `new DbContext`；统一经由 `SqliteContextFactory`。
- 不得在持久化层引入 WPF 或任何适配器依赖。
- 不得把唯一约束冲突的原始 SQL 透给用户；必须映射为可读的 `StorageException`。
- 不得为图方便把明文口令塞进 `Project` / `EnvironmentProfile` / `Asset` 的字段。
- 不得把"磁盘满 / 无权限"当成"文件损坏"处理。
- 改动 `*.csproj` 的 `ProjectReference` 后必须先还原再构建，
  否则 `--no-restore` 会用过期产物报出与本次改动无关的编译错误。

## ADR-033：日志采用按日 JSONL、写入前双重脱敏与异步持久化（LF-005）

**背景**

阶段 03 已有日志面板，但日志只存在进程内；应用关闭后无法追查。
同时，诊断目标、HTTP Header、RTSP URL 和插件返回值都可能携带凭据，
仅靠界面显示时脱敏不够：一旦明文进入磁盘文件，后续复制、归档或误发附件
都会扩大泄漏面。需要在 LF-005 中同时解决持久化、筛选、复制、导出与脱敏。

**决策**

1. **不引入 Serilog**。使用 `System.Text.Json` 输出按日滚动的 JSON Lines：
   `%LOCALAPPDATA%\LinkForge\logs\linkforge-yyyyMMdd.jsonl`。
   当前需求只需要结构化字段、追加写和最近记录读取，额外日志框架会增加
   依赖、配置面与许可证维护成本。
2. **应用级日志聚合与页面局部日志解耦**。`StructuredLogService` 通过
   `Attach` 订阅工作台、网络、API、MQTT、RTSP、插件等页面已有的日志集合，
   统一进入壳层日志面板；页面无需知道磁盘存储。
3. **异步 Channel 写入**。UI 线程只发布记录，磁盘写入由单消费者后台任务执行；
   `FlushAsync` 供关闭前尽可能落盘，不把文件 I/O 放到 UI 线程。
4. **内存上限 5000 条**。超出后在 UI 调度线程移除最旧记录；
   历史初始化只读取当前日文件最近 5000 条。
5. **写入前双重脱敏**。`LogEntry` 构造时先脱敏；存储层在序列化前再次调用
   同一个 `ITextRedactor`，读取时也再脱敏。报告生成前另有独立扫描。
6. **稳定 `EventId`**。日志事件使用稳定标识，不生成随机 ID；RunId 用于关联
   诊断证据，TargetId 仅存脱敏后的目标标签。
7. **坏行跳过而不阻断启动**。读取 JSONL 时单行损坏只跳过该行；
   文件权限、磁盘错误等存储故障通过独立 `StorageError` 反馈，不伪装成“无日志”。
8. **筛选后复制/导出**。日志面板按消息、模块、RunId、TargetId、EventId
   搜索，并支持最低级别过滤；复制与导出只处理当前可见结果。

**依赖边**

```text
LinkForge.App ─→ LinkForge.Application.Logging
LinkForge.Application.Logging ─→ LinkForge.Application.Diagnostics（ITextRedactor）
LinkForge.App 不直接写日志文件
```

**剩余风险（明确接受）**

- 日志文件本身**不加密**；只是保证写入、UI、复制与导出前不放明文凭据。
- 未实现日志轮转保留策略、压缩、归档、全文索引与签名/完整性校验。
- 原始异常堆栈的独立“安全详情查看面”尚未实现；页面仍以用户可读摘要为主。
- 关闭时只等待有限时间 flush；强制终止进程可能丢失尚未写入队列的记录。

**约束**

- 不得绕过 `LogEntry` 或 `StructuredLogRecord` 直接把未脱敏字符串写盘。
- 不得在 UI 线程执行日志文件读写。
- 不得把读取失败显示成“没有历史日志”。
- 不得把取消导出记为失败，也不得在导出失败时留下半成品文件。
- 新增日志字段时必须同步更新脱敏、筛选、复制、导出与测试。

## ADR-034：预览发布采用可追溯便携 ZIP 与干净检出验证（LF-013）

> 本节记录 2026-10-22 的预览发布决策；V1.0 的安装器与双架构调整见 ADR-036。

**背景**

当前目标是把已实现的调试能力形成可分发、可追溯的 Windows 预览包，而不是提前
承诺安装器、代码签名和自动更新。若直接把未签名 ZIP 描述为“正式安装包”，用户会
无法区分“能运行”“发布者已验证”“可安全升级”这三件事。

**决策**

1. **发布形态**：未签名、自包含、免安装的 `win-x64` 便携 ZIP；当前不发布 x86。
2. **版本来源**：根目录 `Directory.Build.props` 统一维护 `0.5.0-preview.1`；
   `InformationalVersion` 与 `release.json` 记录提交号，脏工作区必须显式
   `-AllowDirty` 并在文件名、元数据中标记 `.dirty`。
3. **门禁**：`scripts/publish.ps1` 默认先执行 Release 全量构建与测试；
   `-SkipTests` 只能用于本地诊断，不能作为交付证据。
4. **追溯材料**：包内生成版本文件、依赖/许可证清单、CycloneDX 1.5 SBOM、
   全文件 SHA-256 和 ZIP SHA-256。许可证只按 NuGet nuspec 声明提取，
   不替代法律审查。
5. **确定性边界**：启用确定性编译并让 SBOM 序列号由版本/提交派生；
   但包内含构建时间等元数据，不宣称 bit-for-bit 可复现。
6. **干净检出**：`scripts/verify-clean-checkout.ps1` 克隆已提交 `HEAD`，
   重新还原、构建与测试，防止未跟踪文件或本机缓存掩盖缺失引用。
7. **安装边界**：当前没有 MSIX/WiX/Inno Setup、代码签名、时间戳、SmartScreen、
   升级回滚与卸载残留验收。

**依赖边**

```text
scripts/publish.ps1 → LinkForge.App（Release publish）
scripts/verify-clean-checkout.ps1 → scripts/build.ps1
发布元数据 → Git HEAD + Directory.Build.props
```

**约束**

- 不得把 `.dirty` 诊断包当作正式发布产物。
- 不得在未签名时显示“已验证发布者”或暗示 SmartScreen 信任。
- 不得把 NuGet 声明许可证清单称为已完成法律审查。
- 变更发布字段、SBOM 结构或打包流程时必须同步更新 `docs/RELEASE.md`、
  `CHANGELOG.md` 与构建记录。

## ADR-035：ONVIF 只做标准信令诊断，不伪造媒体播放（LF-014）

**背景**

视频接入需要同时覆盖 RTSP 与标准 ONVIF。若把“发现到设备”或“取得 Stream URI”
直接显示成“视频已播放”，会把信令可用、媒体接收、解码和首帧渲染混为一谈；
若关闭 TLS 校验或自动扫描整个网段，也会扩大未经授权探测的风险。

**决策**

1. **独立适配器边界**：采用 `Adapters.Onvif`，
   依赖 `App → Adapters.Onvif → Domain / Plugin.Abstractions`；
   适配器不引用 App、Application、WPF，也不访问项目数据库或日志文件。
2. **能力范围**：仅实现 WS-Discovery、`GetDeviceInformation`、
   `GetCapabilities`、`GetProfiles` 与 `GetStreamUri`。这些结果是标准 SOAP
   信令和设备响应，不是媒体播放结果。
3. **发现边界**：WS-Discovery 固定使用 UDP `239.255.255.250:3702`，
   只执行用户明确启动的一次发现窗口；单条损坏报文被隔离，不中断其他设备响应。
4. **传输安全**：HTTP/HTTPS 请求始终启用 TLS 证书校验，不提供绕过开关；
   不执行自动网段扫描、端口扫描或厂商私有探测。
5. **凭据最小化**：当前只支持 WS-Security UsernameToken。凭据只进入
   SOAP Header，不进入 URL；显示、日志、结构化事实、复制和 JSON 导出统一脱敏。
6. **资源限制**：响应体上限 2 MiB，禁止 DTD，并限制 XML 深度与实体扩展；
    HTTP 错误、401、超时、非法 XML、超大响应分别表达。
7. **未知语义**：发现窗口内没有设备响应时保持 `Unknown`，
   不得表述为“网络中没有 ONVIF 设备”。
8. **媒体边界**：不实现 RTP 接收、媒体解码、首帧、持续播放、PTZ、事件、
   录像、固件升级、厂商 SDK 或 GB/T 28181。Stream URI 可用不等于视频可播放。

**验证**

- 本地设备服务测试覆盖发现、设备信息、能力、Profile、Stream URI、
  WS-Security、401、超时、非法 XML、超大响应、取消与脱敏。
- App 层测试覆盖双页面导航互斥、依赖注入、空/运行/成功/失败/未知/取消状态、
  1366×768 布局与浅深色截图。
- 当前未覆盖真实设备矩阵、复杂多网卡组播环境与厂商兼容性差异。

**约束**

- 不得把 `Unknown` 映射为 `Completed`，也不得把 `Skipped` 映射为 `Failed`。
- 不得在 UI、日志、历史、复制、导出或测试输出中出现 WS-Security 明文口令。
- 不得用示例设备、固定 Stream URI 或静态结果冒充真实 ONVIF 响应。
- 新增 ONVIF 服务时若涉及媒体、PTZ 或事件，必须单独评审授权、脱敏与资源上限。

## ADR-036：V1.0 采用未签名用户级双架构安装器（LF-013）

**背景**

预览版只有 `win-x64` 未签名便携 ZIP。V1.0 需要可安装、可卸载、可覆盖升级的
Windows 分发物，同时保留便携使用方式。产品没有代码签名证书，且不应假设
普通用户拥有管理员权限。最低支持 Windows 10 x64，同时需要验证 x86 自包含载荷。

**决策**

1. **安装器采用 Inno Setup 6**，不使用 MSIX/WiX；单个安装包同时嵌入
   `win-x64` 与 `win-x86` 自包含载荷，安装时以 `IsWin64` 选择架构。
2. **用户级安装**：默认目录 `%LOCALAPPDATA%\Programs\LinkForge`，
   `PrivilegesRequired=lowest`，应用清单使用 `asInvoker`，不触发管理员提权。
3. **卸载保留用户数据**：只删除程序文件与快捷方式，保留
   `%LOCALAPPDATA%\LinkForge` 下的项目、日志、报告和数据库。
4. **覆盖安装等同升级**：同一 AppId 重复安装覆盖程序目录；本版不提供
   自动更新、版本降级阻止或失败自动回滚。
5. **中文语言包随仓库固定**：采用 MIT 许可的简体中文 Inno 语言包，
   保存来源、版本提示、SHA-256 与许可证；安装器发布物附带第三方声明。
6. **正式门禁**：`build-release.ps1 -Formal` 要求 `VersionSuffix` 为空、
   工作区干净、Release 全量测试通过，并同时生成双便携包、安装器、SHA-256、
   SBOM、依赖清单和发布 JSON。
7. **未签名边界**：V1.0 安装器与便携包未签名；文档、About 页和发布记录
   不得声称发布者已验证，必须提示 SmartScreen 可能出现警告。

**验证**

- 安装、重复安装升级、启动/关闭、卸载与用户数据保留真实执行通过。
- x86 自包含载荷真实启动/关闭，退出码 `0`；PE 头确认载荷架构为 x86。
- 安装器与语言包编译成功；脚本会拒绝已有同名开始菜单目录，避免误删。

**剩余风险**

- 未在 Windows 10 真机执行安装/升级/卸载。
- 未在 32 位 Windows 上实测安装器的 x86 选择流程；仅在 x64 WOW64 下验证载荷。
- 无代码签名、时间戳、SmartScreen 信任和安装包完整性签名。
- 无自动更新与升级失败回滚；依赖清单不替代法律审查。

**约束**

- 不得把未签名产物写成“已签名”“已验证发布者”或“可安全自动升级”。
- 不得在卸载时删除用户项目、日志、报告或数据库。
- 不得用 `/GROUP` 参数测试固定分组安装器；Inno 在
  `DisableProgramGroupPage=yes` 时会忽略该参数。
- 发布脚本只有在干净提交和正式版本门禁通过后才能生成正式产物。

## ADR-037：About 页只报告本机可验证的版本与签名事实

**背景**

发布页很容易用静态字符串伪造版本、架构或“已是最新版本”。V1.0 安装器未签名，
若界面显示虚假的发布者信任，会直接误导用户在 SmartScreen 警告下继续运行。

**决策**

1. About 页从当前程序集、运行时、操作系统、进程架构和实际安装目录读取事实。
2. Authenticode 通过 `WinVerifyTrust` 只验证本机信任链，结果分为
   `Trusted / Unsigned / Invalid / Unknown`，不联网查询发布者信誉。
3. 不实现联网更新检查，不显示“已是最新版本”等未经服务器确认的结论。
4. 签名读取、路径解析等操作在后台线程执行，可取消；不把文件 I/O 放在 UI 线程。
5. 复制摘要只含版本、提交、系统、架构、路径与签名结果，不读取项目凭据。

**边界**

- 本机 `Trusted` 不代表供应链审计、代码质量审查或官方发布者身份已确认。
- 未签名版本必须显示警告，并提示 SmartScreen 可能拦截。
- 网络更新检查、下载签名验证和自动更新不在 V1.0 范围。

## ADR-038：项目包严格交换非秘密配置并使用单事务导入（LF-015）

**背景**

项目、环境和资产配置需要可迁移，但资产凭据引用不能演变成明文密码导出。
同时，交换文件如果静默接受未知字段或部分导入，用户将无法判断目标数据库
究竟发生了哪些变化。覆盖现有项目也可能在无提示的情况下破坏现场配置。

**决策**

1. **版本化格式**：使用 `linkforge.project-package` schema v1；未知版本拒绝。
2. **严格读取**：拒绝未知/重复字段、未知枚举、重复 ID、跨项目引用、
   多个激活环境、超过 2 MiB 的文件、200 个以上环境或 5000 条以上资产。
3. **秘密边界**：项目包仅允许 `CredentialReference` 引用键；秘密形状字段名
   和无效引用值整体拒绝，不依赖导出后文本过滤。
4. **默认不覆盖**：项目 ID、项目名称、环境 ID 或资产 ID 冲突时拒绝整包，
   不提供隐式 upsert 或“部分成功”。
5. **单事务导入**：所有记录在一个 SQLite 事务中写入；冲突、写入异常或取消
   均回滚，不留下部分项目。
6. **异步与取消**：编码、解码、文件读写和数据库导入均异步执行；
   UI 提供取消入口，取消不映射为失败。
7. **层次边界**：格式与导入契约在 Application，SQLite 实现在 Persistence，
   文件对话框在 App；适配器不得引用项目包或 WPF。

**验证**

- 编解码测试覆盖严格字段、版本、秘密边界、确定性导出与大小上限。
- Persistence 测试覆盖真实 SQLite 成功导入、ID/名称冲突、触发器失败和取消回滚。
- App 测试覆盖导入/导出成功、读取失败、写入失败、取消、互斥和页面状态。
- 全解决方案 1034 / 1034 测试通过，真实启动/关闭退出码 0。

**约束**

- 项目包不是秘密库，不得添加密码、令牌或 Cookie 明文字段。
- 未实现覆盖/合并策略前，不得把冲突导入提示成成功。
- 文件格式有效不代表来源可信；需要来源认证时必须另行引入签名机制。

## ADR-039：Windows CurrentUser DPAPI 凭据库（LF-016）

**背景**

V1.0 之前资产只保存凭据引用键，项目数据不含明文密码，但用户仍无法在应用内
安全保存和检查真正的凭据。引入明文文件、数据库列或可跨机复制的秘密存储都会
破坏已有的“项目包只交换非秘密配置”边界。

**决策**

1. **范围**：只实现 `SecretProvider.DpapiCurrentUser`；其他提供方（包括
   Windows Credential Manager）明确拒绝，不伪装为可用。
2. **落盘格式**：每条秘密独立保存为 `.dpapi` 文件，位于
   `%LOCALAPPDATA%\LinkForge\secrets`；文件名为引用键的 SHA-256，
   磁盘路径不泄漏引用键。
3. **绑定与校验**：DPAPI 附加熵绑定应用标识、存储格式版本、提供方和引用键；
   解密后的 JSON 信封再次校验 schema、提供方、类型和引用键。
   密文改名后不能作为另一条秘密使用。
4. **状态语义**：`Missing` 表示没有文件；`Available` 表示可解密；
   `Unreadable` 表示文件存在但损坏、被替换、权限不足或属于其他用户。
   不得把 `Unreadable` 误报成 `Missing`。
5. **输入边界**：秘密值上限 64 KiB UTF-8；保存尝试后立即清空视图模型中的
   明文输入；引用键变化时也清空旧输入，避免把旧秘密保存到新引用下。
6. **异步与 I/O**：文件读写和 DPAPI 调用通过后台任务执行，支持取消；
   使用临时文件、`WriteThrough` 与原子替换，避免把半写文件当成有效秘密。
7. **不扩散**：复制、日志、历史、导出、报告和项目包永远不读取秘密值；
   只有未来真正发起认证的适配器才可显式调用 `ISecretStore.ReadAsync`。

**验证**

- DPAPI 往返、明文不在密文文件中出现、不同引用隔离、损坏文件报告
  `Unreadable`、删除、非法引用、上限、取消和资产页状态测试。
- 全量构建 0 警告 0 错误；测试 1049 / 1049 通过；真实启动/关闭退出码 0。

**边界与剩余风险**

- CurrentUser DPAPI 只保护当前 Windows 用户在本机的离线文件，不提供跨机同步，
  也不能防御已经以同一用户身份运行的恶意软件。
- 本项目不实现 Windows Credential Manager、数据库加密、密钥轮换或备份恢复。
- 凭据引用键本身不是秘密；项目包仍可能暴露它指向的业务命名。
