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
2. 采用 MQTTnet `5.2.0.1603`，MIT；当前只提交 MQTT 5.0 客户端，
   不宣称 MQTT 3.1.1 版本切换已实现。
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
  取消、Broker 断线与参数非法。

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
