# LinkForge 构建与测试记录

本文件记录**实际执行**的构建/测试命令与真实结果。禁止在此写入未运行的结论。

---

## 2026-10-15 · 第一轮（LF-000 勘察 + LF-001 骨架）

### 环境

| 项目 | 值 |
| --- | --- |
| .NET SDK | 10.0.402（用户级 `%USERPROFILE%\.dotnet`） |
| 运行时 | Microsoft.WindowsDesktop.App 10.0.13 |
| 命令前缀 | `& "$env:USERPROFILE\.dotnet\dotnet.exe"` |

### 1. 工具链可行性探测（临时目录，非仓库）

先验证 `net10.0-windows` + WPF + 候选 UI 包可还原、可构建，避免把未验证选型写进基线。

```powershell
& $dotnet new wpf -n ProbeWpf -o $probe --framework net10.0
& $dotnet add package WPF-UI --prerelease
& $dotnet add package CommunityToolkit.Mvvm
& $dotnet build -v q --nologo
```

结果：`已成功生成。0 个警告 0 个错误`；解析到 `WPF-UI 4.3.0`、`CommunityToolkit.Mvvm 8.4.2`、`WPF-UI.Abstractions 4.3.0`。

### 2. 解决方案首次构建

```powershell
& $dotnet build LinkForge.slnx -c Debug
```

结果：**失败 1 项**。

```text
src\LinkForge.App\App.xaml.cs(10,28): error CS0118: "Application"是 命名空间，但此处被当做 类型 来使用
```

**根因**：`LinkForge.App` 引用了 `LinkForge.Application` 项目。在 `namespace LinkForge.App` 内裸写 `Application`，C# 名称解析优先匹配到 `LinkForge.Application` 命名空间，而非 `System.Windows.Application`。

**修复**：`App.xaml.cs` 与 `MainWindow.xaml.cs` 的基类改为全限定名 `System.Windows.Application` / `System.Windows.Window`，并在代码中留下注释说明原因。未改动项目名（改名会偏离 `ARCHITECTURE.md` §4 的既定结构）。

### 3. 修复后构建

```powershell
& $dotnet build LinkForge.slnx -c Debug
```

结果：

```text
已成功生成。
    0 个警告
    0 个错误
```

### 4. 测试（含一次真实失败与修正）

首次运行 `dotnet test` 时，`Application_assembly_should_reference_domain` **失败**：

```text
referenced should contain "LinkForge.Domain" but was actually ["System.Runtime"]
```

**根因**：断言方法有误。`LinkForge.Application` 当时无任何代码使用 Domain 类型，C# 编译器会裁掉未使用的程序集引用，因此 IL 层看不到 `LinkForge.Domain`。这不是产品缺陷，是测试设计缺陷。

**修正**：改为分两类断言——

- 读 **csproj** 的项目依赖图（约束声明层依赖方向，空项目也有效）
- 读 **IL** 程序集引用（约束编译产出层，可捕获代码中新增的 WPF 依赖）

### 5. 最终验证结果

```powershell
& $dotnet build LinkForge.slnx -c Debug
& $dotnet test  LinkForge.slnx -c Debug
```

| 项目 | 结果 |
| --- | --- |
| 构建 | 成功，0 警告，0 错误 |
| LinkForge.Domain.Tests | 通过 2 / 失败 0 / 跳过 0 |
| LinkForge.Application.Tests | 通过 5 / 失败 0 / 跳过 0 |

当前测试内容（均为真实断言，非占位）：

- Domain 程序集不引用 WPF/EF Core/Serilog/Wpf.Ui
- Domain 项目无项目引用、无 NuGet 引用（保持叶子节点）
- Application 程序集不引用 WPF
- Application 项目声明引用 Domain
- App 项目引用 Application / Domain / Plugin.Abstractions

### 6. 尚未验证（如实声明）

- WPF 窗口**未做运行时启动验证**（当前仍为模板 `MainWindow`，无实质内容；阶段 03 壳层完成后验证）。
- 未验证 Windows 10 早期版本与 ARM64（无对应环境）。
- 未建立 CI 流水线（LF-001 剩余项）。

---

## 阶段 01：设计令牌（2026-10-15）

### 1. 统一入口

新增 `scripts/build.ps1`，本机 SDK 位于用户级目录，脚本显式解析 `dotnet` 而不依赖 PATH。

```powershell
pwsh -File scripts/build.ps1
```

等价于：`restore` -> `build -warnaserror` -> `test --no-build`。

### 2. 真实执行结果

| 项目 | 结果 |
| --- | --- |
| 构建（`-warnaserror`） | 成功，0 警告，0 错误 |
| LinkForge.Domain.Tests | 通过 2 / 失败 0 / 跳过 0 |
| LinkForge.Application.Tests | 通过 5 / 失败 0 / 跳过 0 |
| LinkForge.App.Tests | 通过 31 / 失败 0 / 跳过 0 |
| **合计** | **通过 38 / 失败 0** |

`LinkForge.App.Tests` 为阶段 01 新增，覆盖：

- 浅色与深色调色板暴露**完全相同的令牌键**（缺键会导致切换后取不到值）
- 每个 `LfColor*` 都有配套 `LfBrush*`，且画刷颜色与颜色令牌一致
- WCAG 2.1 对比度：正文文本 >= 4.5、辅助文本 >= 3.0、交互边界 >= 3.0（1.4.11）、
  语义状态色对全部表面 >= 4.5、状态底色与表面可辨识差异 >= 1.10
- 主题切换后全部必需画刷令牌仍可解析
- 调色板字典缺失时抛出可操作异常（而非静默无反应）
- `DynamicResource` 跟随调色板替换、`StaticResource` 不跟随（锁定 ADR-017 的机制依据）
- 静态守卫：视图内不得出现 `{StaticResource LfBrush*}`

### 3. 运行时验证（真实启动 + 截图）

```powershell
pwsh -File scripts/run.ps1
```

| 验证项 | 结果 |
| --- | --- |
| 应用启动 | 成功，窗口标题 "LinkForge — 设计令牌验证"，可正常关闭 |
| 浅色主题 | 截图确认：表面层级、文本层级、七类状态语义、强调色均正确渲染 |
| 深色主题 | 截图确认：全部区域随主题切换，按钮文字可读 |
| x64 构建与运行 | 通过 |
| x86 构建 | 通过（`PlatformTarget=x86`，0 警告 0 错误） |
| x86 运行 | **失败**，退出码 `-2147450750`（0x80008082，缺少 x86 .NET 10 运行时） |

### 4. 本阶段发现并修复的真实缺陷

| # | 现象 | 根因 | 处理 |
| --- | --- | --- | --- |
| 1 | `{StaticResource LfPagePadding},0` 编译报 MC3044 | WPF 不允许在 `{StaticResource}` 后追加字面量 | 新增完整组合边距令牌（`LfHeaderPadding` 等） |
| 2 | 深色主题下头部与卡片仍为白色 | `StaticResource` 只在解析时求值一次 | 视图内 57 处画刷引用改为 `DynamicResource`；见 ADR-017 |
| 3 | 深色下按钮"浅蓝底 + 近白字"不可读 | WPF-UI 按钮模板经 `DynamicResource` 读取 `ButtonBackground` 等键，覆盖局部设置值 | 在调色板内集中映射控件库键；见 ADR-018 |
| 4 | 显式 `Style` 反而使悬停色变成硬编码浅蓝 | 显式 Style 替换了 WPF-UI 隐式样式，回退到 Aero2 模板 | 移除自建按钮 Style，保留控件库模板 |
| 5 | 深色 `DependencyMissingSubtle` 与表面差异仅 1.08 | 取值未达 1.10 可辨识阈值 | 调整为 `#FF332813`（实测 1.116） |
| 6 | 测试加载调色板报 "The URI prefix is not recognized" | 测试宿主缺少 WPF `Application` 上下文，pack URI 无法解析 | 测试改为在带 `Application` 的 STA Dispatcher 线程上执行 |
| 7 | `dotnet test` 报无法加载 loose XAML | 测试直接读磁盘文件，与生产加载 BAML 的路径不一致 | 测试改用与生产相同的 pack URI |

### 5. 环境约束（重要，影响启动方式）

本机存在两套 .NET 运行时，且**均不含 x86 的 .NET 10**：

| 位置 | 内容 |
| --- | --- |
| `C:\Program Files\dotnet` | 仅 .NET 8.0.14（x64） |
| `C:\Program Files (x86)\dotnet` | 仅 .NET 8.0.14（x86） |
| `%USERPROFILE%\.dotnet` | .NET SDK 10.0.402 + 运行时 10.0.13（x64） |

直接双击 `LinkForge.App.exe` 会弹出
"You must install or update .NET to run this application"，
因为 apphost 默认只查找 `C:\Program Files\dotnet`，不会查找用户级目录。

`scripts/run.ps1` 通过设置**进程级** `DOTNET_ROOT` 解决（不修改系统或用户环境变量）。

### 6. 尚未验证（如实声明）

- 未在 Windows 10 上实测（本机为 Windows 11 10.0.26300）。
- 未做高 DPI 缩放（125% / 150% / 200%）实测。
- 未做键盘焦点环与 Tab 顺序人工走查（阶段 02/06）。
- 未做深浅色对比度的人工目视复核以外的工具校验（如颜色盲模拟）。
- x86 运行未通过，原因已定位为缺少 x86 .NET 10 运行时，非代码缺陷。

## 阶段 02 · 基础组件（2026-10-16）

### 1. 交付物

| 类别 | 内容 |
| --- | --- |
| 状态组件 | `EmptyState` / `LoadingState` / `ErrorPanel` / `DependencyMissingPanel` / `TaskProgressPanel` / `ResultSummary` / `StatusBadge` |
| 输入组件 | `LabeledTextBox`（可见标签 + 必填标记 + 就近错误）/ `SecretTextBox`（SecureString + 默认禁复制 + 明文警示） |
| 列表组件 | `LogViewer`（虚拟化 + 级别过滤 + 搜索 + 默认脱敏 + 计数条） |
| 展示模型 | `LogEntry`（构造时脱敏，无明文成员）/ `LogLevelPresentation` |
| 测试 | 181 个 WPF 测试（ComponentStyles 26 / SecretTextBox 14 / LogViewer 12 / LogEntry 11 / RenderSmoke 14 / Contract 2 / ResourceReference 2 / Screenshot 2 …） |

### 2. 本阶段发现并修复的真实缺陷

| # | 现象 | 根因 | 处理 |
| --- | --- | --- | --- |
| 1 | 应用启动即抛 XamlParseException"无法找到 LfFontSizeCaption" | 控件模板延迟实例化，LoadContent 时模板根不在逻辑树，StaticResource 沿树查找落空 | 模板内全部 `Lf*` 引用改 `DynamicResource`（91 处）；新增 ADR-019 + 静态守卫 |
| 2 | 测试全绿但生产崩溃 | 测试宿主把令牌字典扁平合并，与生产的嵌套结构不同，掩盖了 StaticResource 缺陷 | ThemedHost 只合并 ThemeResources.xaml，与 App.xaml 逐字对齐 |
| 3 | LogViewer 换日志源后界面不刷新 | `View` 是普通 CLR 属性，模板绑定收不到变更通知 | 改为只读依赖属性 + 重建时推送；新增 ADR-020 + 契约测试 + 行为测试 |
| 4 | MainWindow 测试后其余用例 NullReferenceException | 窗口 Close 触发默认 OnLastWindowClose 关停 Application | 测试宿主 ShutdownMode 改 OnExplicitShutdown |
| 5 | 日志行 Margin 报"4 不是 Margin 有效值" | `{DynamicResource LfSpacingXs}`（double）当 Thickness 用 | 换用 `LfInlineSpacingXxs` |
| 6 | Binding.Converter 上不能设置 DynamicResource | Converter 不是依赖属性 | 批量替换时豁免 `Converter=` 位置（9 处） |
| 7 | 深色启动后状态栏仍显示"浅色" | UpdateThemeStatus 在构造时读主题，早于 App.OnStartup 应用 | 改为 Loaded 后更新 |

### 3. Mutation 验证记录

| 验证项 | 操作 | 结果 |
| --- | --- | --- |
| RenderSmoke 测试能抓模板 StaticResource 缺陷 | 把 EmptyState `LfRadiusPanel` 改回 StaticResource | 5 个用例失败（含 MainWindow 用例，需 Show） |
| 契约测试能抓 View 退化 | 删除 `View = _viewSource.View` 赋值 | Replacing_entries 用例失败 |
| 静态守卫能抓 ADR-019 违规 | LoadingState `LfFontSizeCaption` 改回 StaticResource | ResourceReferenceTests 失败并指出文件与键名 |

### 4. 阶段验收

| 验证项 | 结果 |
| --- | --- |
| 全解决方案构建（-warnaserror） | 通过，0 警告 0 错误 |
| 全解决方案测试 | **213 / 213 通过**（Domain 29 + Application 5 + App 179） |
| 应用真实启动（默认浅色） | 成功 |
| 应用真实启动（深色） | 成功，无崩溃 |
| 浅色截图 | `screenshots/stage02-mainwindow-light.png` |
| 深色截图 | `screenshots/stage02-mainwindow-dark.png`（经 RenderTargetBitmap，与设备无关） |
| MainWindow 加载路径守卫 | 新增用例 `MainWindow_should_survive_a_full_layout_pass`，已验证 Show() 后能捕获模板缺陷 |
| 日志脱敏 | UI 呈现的样例日志包含 RTSP URL + Bearer 头，脱敏后不可见明文 |

### 5. 测试基础设施改进

- `ThemedHost`：与生产 App.xaml 的合并结构逐字对齐（嵌套 ThemeResources），消除假阳性。
- `StaRunner`：ShutdownMode 改 OnExplicitShutdown，避免窗口 Close 关停整个 Application。
- 新增 `ResourceReferenceTests`：静态扫描模板内 `Lf*` 引用，豁免 `Converter=`（ADR-019）。
- 新增 `LogViewerContractTests`：反射断言模板绑定属性均有 DependencyProperty 支撑（ADR-020）。
- 新增 `MainWindowScreenshotTests`：RenderTargetBitmap 输出浅/深色 PNG 到 `screenshots/`，断言非空非全透明。
---

## 阶段 03 · 应用壳层（2026-10-16）

### 1. 交付物

| 类别 | 内容 |
| --- | --- |
| 应用壳层 | `Views/ShellWindow.xaml(.cs)`：标题栏 + 可折叠导航 + 内容区 + 可拖拽日志面板 + 状态栏 |
| 导航 | `Navigation/NavigationCatalog.cs`（一级结构取自 `REVIEW_V1.0.md` C-01 裁决）、`Navigation/LfGlyph.cs`（图标码位集中管理） |
| 视图模型 | `ViewModels/ShellViewModel.cs`（导航/主题/面板状态）、`ViewModels/DashboardViewModel.cs`（只呈现可验证事实） |
| 服务 | `Services/AppSettingsStore.cs`（原子写入 + 白名单）、`Services/AppSettings.cs`、`Services/AppEnvironment.cs`、`Services/ThemeManager.cs`（扩展主题同步） |
| 主题 | `Themes/Controls/Navigation.xaml`、`Dimensions.xaml` 新增壳层令牌（含 `LfBorderThicknessNone`） |
| 测试 | 新增 78 个用例：`ShellWindowTests` 11、`ShellWindowScreenshotTests` 3、`ShellViewModelTests` 25、`AppSettingsStoreTests` 17、`NavigationCatalogTests` 11、`GlyphTests` 6、`VisualHardcodingTests` 2，以及 `ThemeRoot` 测试基础设施 |

### 2. 本阶段发现并修复的真实缺陷

| # | 现象 | 根因 | 处理 |
| --- | --- | --- | --- |
| 1 | **关闭窗口必崩** | `SystemThemeWatcher.UnWatch` 在 `Closed` 调用，那时 HWND 已销毁，控件库抛 `InvalidOperationException("Could not get window handle.")`。单次关闭=非零退出码，反复开关=测试宿主进程崩溃 | 解绑提前到 `Closing`，加 `_isThemeWatcherAttached` 标志 |
| 2 | **工作台一片空白** | `DashboardPage` 的 `DataContext` 继承窗口的 `ShellViewModel`，页面所有绑定静默落空（无异常、无日志） | `ShellWindow.xaml` 加 `DataContext="{Binding Dashboard}"`；`ShellViewModel` 暴露 `Dashboard` 属性 |
| 3 | **"重新检测依赖"按钮点了没反应** | XAML 绑 `RefreshDependenciesCommand`，但 `DashboardViewModel.RefreshDependencies` 缺 `[RelayCommand]`，命令根本不存在 | 补特性 + `using CommunityToolkit.Mvvm.Input` |
| 4 | **半截主题**（控件库深、令牌浅） | `SystemThemeWatcher.Watch` 注册首个窗口时强制 `ApplySystemTheme`，只切 WPF-UI 字典，不动 `Palette.*.xaml` | 新增 `ThemeManager.SyncWithWpfUiTheme()` + 窗口订阅 `ApplicationThemeManager.Changed`（含 `_isSyncingWithWpfUi` 防递归）。沉淀为 **ADR-021** |
| 5 | **分组节点选中态分裂**（高亮在"接入调试"、内容是工作台） | TreeView 自己维护选中状态，ViewModel 拦不住 | `AddHandler(TreeViewItem.SelectedEvent, ...)` 撤销分组选中并回填当前节点 |
| 6 | **令牌/代码数字漂移** | 导航宽度在 code-behind 硬编码 `OpenPaneLength=240`/`CompactPaneLength=52`，令牌是 240/48 | 改为 XAML `Border.Style` + `DataTrigger` 在两个令牌间切换；删除 code-behind 宽度逻辑 |
| 7 | **日志面板折叠留白 / 拖动高度不持久** | 折叠只改可见性不改行高；`Border.Height` 绑定导致 GridSplitter 拖动与持久化脱节 | `LogPanelRow` 行定义 + `ApplyLogPanelHeight()`（折叠=0，展开=像素值）+ `DragCompleted` 回写 `SetLogPanelHeight` |
| 8 | **视图硬编码视觉值** | `Width="1366" Height="800"`、`ColumnDefinition Width="120"` | 新增令牌 `LfLabelColumnWidth`；窗口尺寸改用 `LfReferenceWidth`/`LfReferenceHeight` |
| 9 | **阶段 02 的"浅色截图"其实是深色** | `MainWindowScreenshotTests` 只在 `Application.Current is App` 时注入主题，而测试宿主是**基类** `Application`，分支永不成立 —— 主题从未被设定，截图反映残留状态；阶段 02 提交的 light/dark 两张 PNG **SHA-256 完全相同** | `MainWindow` 增加 `internal ThemeManager` 注入点；测试显式注入并新增"两主题必须不同"断言。沉淀为 **ADR-023** |
| 10 | **截图证据依赖执行顺序** | 截图类通过进程级 `ApplicationThemeManager` 全局状态互相影响；并行执行不可复现 | 关闭 xunit 测试集合并行；固定窗口 `Left/Top`（ClearType 子像素相位）。沉淀为 **ADR-023** |
| 11 | **静态守卫报 3 处硬编码** | `BorderThickness="0"` 写在视图与模板里 | 新增 `LfBorderThicknessNone` 令牌替换（未走白名单豁免） |

### 3. Mutation 验证记录

每一项都是"把缺陷改回去、确认守卫真的失败"，而不是只跑一遍绿灯。

| 验证项 | 操作 | 结果 |
| --- | --- | --- |
| 关闭路径守卫 | 把 `UnWatch` 从 `Closing` 搬回 `Closed`（复现缺陷 1） | `Closing_the_window_should_not_throw` 与 `Opening_and_closing_repeatedly_should_not_crash` **均失败** |
| 工作台绑定守卫 | 删掉 `DashboardPage` 的 `DataContext="{Binding Dashboard}"` | `Dashboard_page_should_receive_its_own_view_model` **失败** |
| 命令守卫 | 去掉 `RefreshDependencies` 的 `[RelayCommand]` | 相关用例**失败** |
| 分组选中守卫 | 移除 `TreeViewItem.SelectedEvent` 的 `AddHandler` | `Selecting_a_group_node_should_not_leave_it_highlighted` **失败** |
| 令牌宽度守卫 | 把折叠宽度写死为 `52` | `Navigation_pane_width_should_come_from_design_tokens` **失败** |
| 主题同步守卫（ADR-021） | 把 `SyncWithWpfUiTheme` 改为空实现 | `SyncThemeWithWpfUi` 相关用例**失败** |
| 视觉值守卫 | 把 `ErrorPanel` 的令牌改回字面量 `0` | `VisualHardcodingTests` **失败** 并列出文件与属性 |
| 截图主题守卫（ADR-023） | 移除显式主题注入（回到阶段 02 写法） | 两张截图变为字节相同，`Light_and_dark_screenshots_should_differ` **失败** |

### 4. 阶段验收

| 验证项 | 结果 |
| --- | --- |
| 全解决方案构建（`-warnaserror`） | 通过，0 警告 0 错误 |
| 全解决方案测试 | **291 / 291 通过**（Domain 29 + Application 5 + App 257） |
| 应用真实启动 + 关闭 | 成功；窗口标题 `LinkForge`，`MainWindowHandle` 有效，**退出码 0** |
| 浅色截图 | `screenshots/stage03-shellwindow-light.png`（人工目视确认：导航/令牌/状态栏"主题：浅色"正确） |
| 深色截图 | `screenshots/stage03-shellwindow-dark.png`（人工目视确认：状态栏"主题：深色"正确） |
| 阶段 02 截图修正 | 修正前 light 与 dark 字节完全相同（`D1AB10E7...`，两张其实都是浅色渲染）；修正后 light 字节不变，dark 变为真正的深色（`7384586C...`），两图差异 5% 以上 |
| 1366×768 布局 | `ShellWindowTests` 断言非全透明渲染；1024×640（最小尺寸）同样通过 |
| 主题持久化 | `ShellViewModelTests` 断言往返、只改主题字段、显式主题不被系统变化覆盖 |
| 脱敏 | `AppSettingsStoreTests` 白名单断言：设置文件中不含任何秘密字段 |
| 令牌漂移 | `VisualHardcodingTests` 扫描 `Views/` 与 `Themes/Controls/`，无颜色/字号/间距/描边字面量 |

### 5. 测试基础设施改进

- 新增 `DesignTokens/ThemeRoot.cs`：构造与生产**嵌套结构一致**的主题根，供不依赖 `Application.Current` 的用例使用。
- 新增 `xunit.runner.json`（`parallelizeTestCollections: false`）：被测对象共享进程级 WPF 全局状态，并行会让截图证据不可复现。
- `StaRunner` 的"基类 Application"设计在阶段 03 暴露了阶段 02 的假阳性（缺陷 9）；已在 `MainWindow` 侧补注入点，而不是放宽测试宿主。

### 6. 尚未验证（如实声明）

- 未在 **Windows 10** 上实测（本机为 Windows 11 build 26300）。图标字体回退（Fluent → MDL2）由 `GlyphTests` 在**字体文件层面**验证码位存在，未在 Win10 真机渲染验证。
- 未做高 DPI 缩放（125% / 150% / 200%）实测。
- 未做键盘焦点环与 Tab 顺序人工走查（阶段 06）。
- 截图存在跨进程的微小像素差异（约 0.87%，集中在按钮边框与文字边缘，源于字形缓存预热与 ClearType），因此截图仅用于人工目视比对，**未**作为字节级回归基线。
- x86 运行未通过，原因已定位为缺少 x86 .NET 10 运行时，非代码缺陷（见 `REVIEW_V1.0.md` D-08）。

## 阶段 04 · 工作台（2026-10-17）

### 1. 交付物

| 类别 | 内容 |
| --- | --- |
| 领域模型 | `Domain/Diagnostics`（步骤/任务结论、状态映射、证据元数据）、`Domain/Environment/DependencyProbe`（含稳定标识 Id）、`Domain/Projects`、`Domain/Assets`、`Domain/Secrets/SecretReference` |
| 插件契约 | `Plugin.Abstractions/IDiagnosticAdapter.cs`（步骤计划 / 执行上下文 / 进度事件 / 取消令牌） |
| 应用层 | `Diagnostics/DiagnosticTaskRunner`（编排器：依赖跳过、前置步骤跳过、单步超时、总超时、用户取消、异常映射，结论先脱敏再上报）；`Diagnostics/EnvironmentSelfCheckAdapter`（真实自检：写探测文件 / 读磁盘 / 探测 Npcap / 导出证据 + SHA-256）；`Diagnostics/JsonRunHistoryStore`（原子写入、损坏隔离、上限 200 条）；`Diagnostics/ITextRedactor` + `DiagnosticRunOptions`；`Environment/DependencyCatalog`（异步可取消依赖探测）；`Diagnostics/StepProgressRelay`（同步进度转发，避免乱序） |
| UI 工作台 | `Views/Pages/DashboardPage.xaml` 重写：快捷动作（仅列已实现项）、自检任务（进度/取消/步骤明细/日志/结果摘要）、系统与依赖状态（缺失用 DependencyMissingPanel 区别于失败）、最近任务（真实历史）、最近项目（明确空状态） |
| 视图模型 | `ViewModels/DashboardViewModel.cs` 重写：`LoadAsync` / `RefreshDependenciesCommand` / `RunSelfCheckCommand` / `CancelSelfCheckCommand` / `ClearHistoryCommand`；进度经 `Progress<T>` 回 UI 线程；UI 投递可等待（`PostAsync`），消除"读旧集合"竞态 |
| 服务 | `Services/AppEnvironment.cs` 精简（删除与 Domain 冲突的 `DependencyProbe` 定义与同步探测，保留进程自身事实 + `SettingsDirectory`）；`Presentation/SecretTextRedactor`（桥接 Application 层脱敏契约与 `SecretRedactor` 唯一实现） |
| DI | `App.xaml.cs` 注册 `ITextRedactor` / `DependencyCatalog` / `IRunHistoryStore` / `DiagnosticTaskRunner` / `IDiagnosticAdapter`（自检适配器） |
| 令牌 | `Dimensions.xaml` 新增 `LfRowSpacing` / `LfStepListMaxHeight` / `LfHistoryListMaxHeight` / `LfActionButtonMinWidth` / `LfDividerBlockTop` |
| 测试 | Domain +17（步骤/任务结论）；Application +20（编排器/超时/取消/跳过）；App +17（`DashboardViewModelTests` 12 个状态用例、`DashboardPageTests` 4 个渲染/截图用例、`DashboardHarness` 夹具） |

### 2. 本阶段发现并修复的真实缺陷

| # | 现象 | 根因 | 处理 |
| --- | --- | --- | --- |
| 1 | **应用启动即崩**（`Unable to resolve IRunHistoryStore`） | DI 注册了具体类型 `JsonRunHistoryStore`，而 `DashboardViewModel` 依赖接口 `IRunHistoryStore`；编译期无感知，运行期解析才炸 | 改为 `AddSingleton<IRunHistoryStore>(...)`。真实启动 + 退出码 0 验证通过 |
| 2 | **"清空历史后列表仍有数据"**（测试捕获） | `Post` 是 fire-and-forget：历史加载/依赖刷新投递集合变更后立即返回，调用方继续读集合时可能拿到旧内容；`RunSelfCheck` 里编排器也可能读到上一次的依赖快照 | `Post` 改为可等待的 `PostAsync`（`TaskCompletionSource`）；所有"投递后还要读集合"的调用点全部 `await`。沉淀为 ADR-024 配套实现约束 |
| 3 | **EmptyState 渲染为空白**（截图验证捕获） | 本地 `Style` 整体替换隐式样式，只写 `Visibility` 时模板丢失 | 本地样式一律 `BasedOn="{StaticResource {x:Type controls:EmptyState}}"`（StatusBadge 同理） |
| 4 | **进度先于脱敏**（上半场发现，沿用修复） | 编排器先 `Report` 后 `Redact`，明文经进度事件进入 UI/日志 | 改为先 `Redact` 再 `Report`；依赖跳过原因同样过脱敏 |
| 5 | **AppEnvironment 与 Domain 的 DependencyProbe 冲突** | App 层 3 参数（无 Id）与 Domain 层 4 参数（含 Id）并存，工作台与诊断各用一套 | 删除 App 层定义，统一用 Domain 版；App 层不再做依赖探测，委托 `DependencyCatalog` |
| 6 | **依赖探测阻塞 UI 线程** | 原 `AppEnvironment.ProbeDependencies()` 同步启动外部进程（ffmpeg --version 等），卡住会冻住界面 | 全部走 `DependencyCatalog.ProbeAllAsync`（3 秒超时、可取消） |

### 3. Mutation / 竞态验证记录

| 验证项 | 操作 | 结果 |
| --- | --- | --- |
| DI 接口注册守卫 | （缺陷 1）以具体类型注册、接口消费 | 真实启动即崩，退出码 -532462766；修复后启动成功、退出码 0 |
| UI 投递竞态守卫 | （缺陷 2）`PostAsync` 退回 fire-and-forget 的 `Post` | `Clearing_history_should_empty_the_list_and_the_file` **失败**（`HasRecentRuns` 为 True）；恢复后通过 |
| 基于隐式样式守卫 | （缺陷 3）移除 `BasedOn` | `Dashboard_page_should_render_a_non_blank_bitmap` 仍通过但人工目视发现 EmptyState 空白；修复后截图正常。**已知限制**：非空断言无法拦截"局部空白"，此缺陷依赖人工目视发现，自动化守卫待补 |
| Skipped ≠ Failed | 断言自检步骤中跳过项不计失败、必须写原因 | `Skipped_steps_must_never_be_counted_as_failures` 通过（真实自检在无 Npcap 环境下 capture 步骤被跳过） |
| 证据落盘 | 断言证据文件存在、SHA-256 长度 64、`IsRedacted` 为 true | `Self_check_should_export_real_evidence` 通过 |

### 4. 阶段验收

| 验证项 | 结果 |
| --- | --- |
| 全解决方案构建（`pwsh scripts/build.ps1`，`-warnaserror`） | 通过，0 警告 0 错误 |
| 全解决方案测试 | **353 / 353 通过**（Domain 54 + Application 25 + App 274） |
| 应用真实启动 + 关闭 | 成功；依赖注入解析正常，**退出码 0** |
| 浅色截图 | `screenshots/stage04-dashboard-light.png`（人工目视确认：快捷动作/自检空态/环境事实/依赖未检测提示/最近任务空态/最近项目空态齐全） |
| 深色截图 | `screenshots/stage04-dashboard-dark.png`（人工目视确认：令牌正确、可读性良好） |
| 自检任务 | 测试内真实执行：4 步全走通、历史落盘、证据文件存在、取消/竞态守卫通过 |
| 七类状态覆盖 | 空（EmptyState）· 加载（LoadingState × 2）· 运行（TaskProgressPanel + 进度）· 成功/部分成功/失败（ResultSummary 按 TaskRunState）· 取消（Cancelled）· 依赖缺失（DependencyMissingPanel）· 读取失败（ErrorPanel + 重试） |
| 脱敏 | 自检结论经 `SecretTextRedactor`（即 `SecretRedactor` 正则）脱敏后才落历史与日志；`Logs_should_never_contain_plaintext_secrets` 锁定 |
| 令牌漂移 | `VisualHardcodingTests` 通过；新令牌全部进 `Dimensions.xaml` |

### 5. 尚未验证（如实声明）

- **自检总超时（90 秒）与单步超时（30 秒）的触发路径**：单元测试用假时钟覆盖了编排器的超时逻辑，但工作台的真实 UI 上没有人为制造过"自检超过 90 秒"的场景（真实自检在测试机上秒级完成）。
- **`ErrorPanel` 的读取失败路径**：`Load_failure_should_be_surfaced_not_swallowed` 用注入的 `FailingRunHistoryStore` 验证了 ViewModel 行为，但真实 UI 上未人为制造"历史文件不可读"的场景（需要手动锁文件或设只读后人工目视）。
- **键盘 Tab 顺序 / 焦点环**：未做人工走查（阶段 06）。
- **高 DPI（125% / 150% / 200%）**：未实测。
- **Windows 10 真机**：本机 Windows 11 build 26300，未在 Win10 上验证。
- **x86 运行**：仍受 D-08 约束（本机无 x86 .NET 10 运行时），构建可通过（`-IncludeX86`），运行未验证。
- **阶段 05 的业务页面**：本阶段只交付工作台与任务框架；视频 / IoT / 网络 / API 等页面按计划属阶段 05，未实现。

## 阶段 05 · 网络基础诊断（LF-006，2026-10-20）

### 1. 交付物

| 类别 | 内容 |
| --- | --- |
| 网络适配器 | `src/LinkForge.Adapters.Network/`：`WindowsNetworkProbe`（Ping/TCP/DNS/本机网络/tracert）与 `NetworkDiagnosticAdapter` |
| 领域结果 | `DiagnosticFact`、`DiagnosticStepResult.Facts/RawOutput`，保留结构化事实并区分原始输出 |
| 视图模型 | `NetworkDiagnosticsViewModel`：字段校验、授权门禁、异步运行/取消、状态与摘要、脱敏复制/导出 |
| 页面 | `NetworkDiagnosticsPage.xaml`：目标与工具、高级参数、授权、运行、结果、原始日志、空状态 |
| 导航/DI | 网络诊断改为 `Available`；注册真实 `INetworkProbe`、适配器、导出器与页面视图模型 |
| 令牌 | `Dimensions.xaml` 新增表单列宽、事实列宽、结果区高度、授权间距与提示间距 |
| 测试 | 网络适配器 10 项；App 新增网络视图模型、页面渲染/截图、Shell 导航互斥与日志目标脱敏测试 |

### 2. 本阶段发现并修复的真实缺陷

| # | 现象 | 根因 | 处理 |
| --- | --- | --- | --- |
| 1 | 网络页可能执行成“环境自检” | DI 同时注册了 `IDiagnosticAdapter` 自检适配器和具体网络适配器，页面按接口注入会命中自检 | 视图模型改为注入具体 `NetworkDiagnosticAdapter`；保留具体类型注册 |
| 2 | 读取本机网卡/路由可能阻塞 UI | `WindowsNetworkProbe.LocalAsync` 在调用线程同步执行 P/Invoke 与网卡枚举 | 改为 `Task.Run` 读取快照，并保留取消检查 |
| 3 | 未实现模块占位可能空白 | Shell 的局部 `EmptyState` 样式未继承隐式模板 | 补 `BasedOn="{StaticResource {x:Type controls:EmptyState}}"` |
| 4 | 网络结论的事实/原始输出可能残留秘密 | 编排器只脱敏摘要、建议与跳过原因 | 统一脱敏步骤名称、事实标签/值和原始输出；`LogEntry.TargetId` 也在构造时脱敏 |
| 5 | 页面截图出现高级参数大块留白 | 当前工具不适用字段按设计 `Collapsed`，旧断言把折叠字段误判为 0 高度 | 量测只检查可见字段；同时保留 1366×768 与主题截图断言 |

### 3. 验证记录

| 验证项 | 结果 |
| --- | --- |
| `pwsh -NoProfile -File scripts/build.ps1` | 通过，0 警告 0 错误 |
| Domain 测试 | 54 / 54 |
| Application 测试 | 26 / 26 |
| Network Adapter 测试 | 10 / 10 |
| App 测试 | 304 / 304 |
| 全解决方案测试 | **393 / 393 通过** |
| 真实启动 + 关闭 | 成功；窗口标题 `LinkForge`，退出码 **0** |
| 浅色截图 | `screenshots/stage05-network-light.png` |
| 深色截图 | `screenshots/stage05-network-dark.png` |
| 页面布局 | 1366×768 渲染通过；可见字段高度 > 0；空/运行/结果组件真实可见 |
| 主题差异 | 浅色与深色截图差异 > 5% |
| 脱敏 | 复制、JSON 导出、日志目标与结构化事实均断言不含测试秘密 |

### 4. 尚未验证（如实声明）

- Windows 10 真机、125%/150%/200% 高 DPI 与键盘 Tab/焦点环仍属阶段 06。
- x86 运行仍受 D-08 缺少 x86 .NET 10 运行时约束；本阶段仅验证 x64。
- 海康/大华 SDK、RTSP/媒体解码、抓包、安全扫描/渗透攻防未在本阶段接入；
  D-09 / D-10 边界未关闭前不以占位或假数据冒充已实现。
- Traceroute 的真实公网路径结果依赖系统 `tracert.exe` 与网络环境；
  自动化测试覆盖了平台不可用、无最终回应与取消等语义，未固定公网跳点。

## 阶段 05 · HTTP/API 工作台（LF-007，2026-10-21）

### 1. 交付物

| 类别 | 内容 |
| --- | --- |
| HTTP 适配器 | `src/LinkForge.Adapters.Http/`：真实 GET/POST/PUT/PATCH/DELETE、Query/Headers/Body、Basic/Bearer/API Key、超时、重定向、响应截断与失败分类 |
| API 工作台 | `ApiWorkbenchViewModel` + `ApiWorkbenchPage.xaml`：请求编辑、环境变量、脱敏历史、响应状态/头/体/耗时、复制与 JSON 导出 |
| 历史与导出 | `JsonApiRequestHistoryStore`（最多 100 条，读写均再次脱敏）、`ApiResultExporter`（Windows 保存对话框） |
| 导航/DI | `connect.api` 改为 `Available`；注册 HTTP 传输、历史存储、导出器与 API 视图模型 |
| 令牌 | `LfMethodColumnWidth`、`LfBodyEditorHeight`、`LfResponseBodyHeight`；表单列间距改用 `GridLength` 令牌 |
| 测试 | HTTP 适配器 15 项（本地 `HttpListener`，不依赖公网）；App 新增 API 校验/状态/取消/脱敏/导出、页面渲染/截图与 Shell 导航互斥测试 |

### 2. 本阶段发现并修复的真实缺陷

| # | 现象 | 根因 | 处理 |
| --- | --- | --- | --- |
| 1 | 空历史时“尚无请求历史”可能永不显示 | 直接 `Visibility` 绑定覆盖了本地样式中的 `HasHistory=False` 触发器 | 删除直接绑定，仅保留 `BasedOn` 样式触发器 |
| 2 | `Set-Cookie` 等响应头可能把会话值显示/导出为明文 | 仅按值执行启发式脱敏，无法从 `session=...` 推断 Header 名称敏感 | 新增 `SecretRedactor.IsSensitiveName/RedactNamedValue`；`Authorization`、`Cookie`、`Set-Cookie`、`X-API-Key` 等按名称整体遮蔽 |
| 3 | 视图模型预置的凭据可能在模板应用后不回显 | `OnSecretChanged` 早于 `PasswordBox` 模板创建，`HookPart` 未补同步 | `HookPart` 在摘除回调后补一次 `Password` 同步，避免覆盖用户已有值或递归 |
| 4 | API 方法列曾触发 XAML 运行时异常 | 将 `double` 令牌用于 `Width`，而 `Width` 需要 `GridLength` | 方法列改用 `MinWidth` + `LfMethodColumnWidth`；表单列间距使用 `LfFormColumnGap` |

### 3. 验证记录

| 验证项 | 结果 |
| --- | --- |
| `pwsh -NoProfile -File scripts/build.ps1` | 通过，0 警告 0 错误 |
| Domain 测试 | 54 / 54 |
| Application 测试 | 25 / 25 |
| Network Adapter 测试 | 10 / 10 |
| HTTP Adapter 测试 | 15 / 15 |
| App 测试 | 337 / 337 |
| 全解决方案测试 | **442 / 442 通过** |
| 真实启动 + 关闭 | 成功；主窗口保持运行，正常关闭退出码 **0** |
| 浅色截图 | `screenshots/stage05-api-light.png` |
| 深色截图 | `screenshots/stage05-api-dark.png` |
| 页面布局 | 1366×768 渲染通过；空态与结果组件真实可见 |
| 主题差异 | 浅色与深色截图差异 > 5% |
| 状态语义 | HTTP 4xx/5xx = 已收到响应；超时/传输失败 = Failed；用户取消 = Cancelled |
| 脱敏 | 认证字段不进入历史/导出；响应头按名称遮蔽；复制/导出/日志断言不含测试秘密 |
| TLS | 传输层不提供关闭证书验证的开关，源码守卫测试锁定 |

### 4. 尚未验证（如实声明）

- OpenAPI 导入、断言、请求串联、OAuth2、HMAC/自定义签名仍未实现（ADR-005 的 P1 范围）。
- 代理、客户端证书、HTTP/2 强制与流式上传未提供专用 UI；当前使用系统默认网络栈。
- Windows 10 真机、125%/150%/200% 高 DPI 与键盘 Tab/焦点环仍属阶段 06。
- x86 运行仍受 D-08 缺少 x86 .NET 10 运行时约束；本阶段仅验证 x64。
- 海康/大华 SDK、MQTT、RTSP 与安全扫描/抓包/攻防仍未在本阶段接入；
  D-09 / D-10 边界未关闭前不以占位或假数据冒充已实现。

## 阶段 05 · MQTT 基础调试器（LF-008，2026-10-21）

### 1. 交付物

| 类别 | 内容 |
| --- | --- |
| MQTT 适配器 | `src/LinkForge.Adapters.Mqtt/`：MQTTnet 5.2.0.1603；真实连接/断开/订阅/发布/消息接收/连接状态事件；失败分类覆盖连接、超时、认证、订阅与发布 |
| MQTT 工作台 | `MqttWorkbenchViewModel` + `MqttWorkbenchPage.xaml`：Broker/端口/Client ID/认证/TLS/超时校验、连接、断开、取消、订阅、发布、消息筛选、暂停显示、清空、复制与 JSON/CSV 导出 |
| 导出 | `IMqttResultExporter` + `MqttResultExporter`；复制与导出内容按 Topic、载荷、凭据边界脱敏 |
| 导航/DI | `connect.iot` 改为 `Available`；注册 `IMqttTransport`、`IMqttResultExporter` 与 `MqttWorkbenchViewModel` |
| 令牌 | `LfMqttMessageListHeight`、`LfMqttMessageEmptyHeight`、`LfMqttSubscriptionListHeight`、`LfMqttPayloadEditorHeight`、`LfMqttMessagePayloadHeight`、`LfMqttWorkspaceGap`、`LfMqttControlColumnWidth`、`LfMqttStreamColumnWidth` |
| 测试 | MQTT 适配器 8 项（本地进程内 Broker）；App 新增 MQTT 校验/状态/取消/脱敏/导出、页面渲染/截图与 Shell 导航互斥测试 |

### 2. 本阶段发现并修复的真实缺陷

| # | 现象 | 根因 | 处理 |
| --- | --- | --- | --- |
| 1 | 应用真实启动后关闭时崩溃，退出码 `-532462766` | 应用退出调用同步 `ServiceProvider.Dispose()`，但 `MqttTransport` 仅实现 `IAsyncDisposable` | `MqttTransport` 同时实现 `IDisposable`，同步释放委托给 `DisposeAsync()`；新增同步释放幂等测试 |
| 2 | `LogViewer` 自动滚动会抢占页面级滚动，用户进入 MQTT 页看不到连接状态 | 日志组件主动滚动到底部 | 日志 `Expander` 默认折叠；用户需展开时再看连接与消息日志 |
| 3 | 页面同时出现状态块与 `ResultSummary` 状态块，信息重复 | 原步骤结果与连接概览重复表达 | 合并为单一连接概览卡片；事实行显示 Broker/TLS/状态/订阅 |

### 3. 验证记录

| 验证项 | 结果 |
| --- | --- |
| `pwsh -NoProfile -File scripts/build.ps1` | 通过，0 警告 0 错误 |
| Domain 测试 | 54 / 54 |
| Application 测试 | 27 / 27 |
| Network Adapter 测试 | 10 / 10 |
| HTTP Adapter 测试 | 15 / 15 |
| MQTT Adapter 测试 | 8 / 8 |
| App 测试 | 375 / 375 |
| 全解决方案测试 | **489 / 489 通过** |
| 真实启动 + 关闭 | 成功；主窗口正常关闭，退出码 **0** |
| 浅色截图 | `screenshots/stage05-mqtt-light.png` |
| 深色截图 | `screenshots/stage05-mqtt-dark.png` |
| 页面布局 | 1366×768 渲染通过；连接/订阅左栏与状态/消息流右栏可视；日志默认折叠 |
| 主题差异 | 浅色与深色截图差异 > 5% |
| 状态语义 | 认证失败、超时、连接失败、用户取消、Broker 断线与主动断开分别表达；`Unknown ≠ Completed`、`Cancelled ≠ Failed` |
| 脱敏 | 用户名/口令不进入日志、订阅列表、复制与导出；Topic/载荷构造时即脱敏 |
| TLS | 始终校验证书；无关闭 TLS 校验开关，源码守卫测试锁定 |

### 4. 尚未验证（如实声明）

- 目前实现 MQTT 5.0 客户端；MQTT 3.1.1 版本选择器尚未实现，不得宣称为双版本切换。
- 未引入 MQTT 3.1.1 属性、遗嘱、保留消息管理、Topic 树和断线自动重连策略。
- Windows 10 真机、125%/150%/200% 高 DPI 与键盘 Tab/焦点环仍属阶段 06。
- x86 运行仍受 D-08 缺少 x86 .NET 10 运行时约束；本阶段仅验证 x64。
- RTSP、海康/大华 SDK、Zigbee、抓包、安全扫描/渗透攻防仍未接入；
  D-09 / D-10 边界未关闭前不以占位或假数据冒充已实现。

## 阶段 05 · RTSP 分阶段信令诊断(LF-009,2026-10-21)

### 1. 交付物

| 类别 | 内容 |
| --- | --- |
| RTSP 适配器 | `src/LinkForge.Adapters.Video/`:真实 DNS/TCP/RTSP OPTIONS/DESCRIBE/SDP 解析;URL 支持 `rtsp://`/`rtsps://`/IPv4/IPv6/userinfo;userinfo 仅用于 Basic 认证;请求行不携带 userinfo;401 后按 `WWW-Authenticate` 判断;当前仅支持 Basic 重试,Digest 明确失败;RTP/SETUP/PLAY/解码/首帧未执行,明确标记 `Skipped` |
| RTSP UI | `RtspDiagnosticsViewModel` + `RtspDiagnosticsPage.xaml`:URL/超时校验、外部目标授权确认、运行/取消、结构化阶段结果、复制脱敏摘要、JSON 导出、原始日志;空状态明确说明媒体接收/解码/首帧未执行 |
| 导出 | `IRtspResultExporter`:复制与导出内容按 URL/凭据边界脱敏 |
| 导航/DI | `connect.video` 改为 `Available`;注册 `IRtspProbe`、`RtspDiagnosticAdapter`、`IRtspResultExporter`、`RtspDiagnosticsViewModel` |
| 测试 | Video 适配器 17 项(本地 `LocalRtspServer`,真实 TCP/RTSP 报文);App 新增 RTSP 校验/状态/取消/脱敏/导出、页面渲染/截图与 Shell 导航互斥测试 |

### 2. 本阶段发现并修复的真实缺陷

| # | 现象 | 根因 | 处理 |
| --- | --- | --- | --- |
| 1 | IPv6 URL 解析后方括号重复 | `RtspUrlParser` 未剥离 Host 方括号 | 修复解析器;新增 IPv6 解析测试 |
| 2 | `WWW-Authenticate: Digest realm="..."` 被误识别为 Basic | `WWW-Authenticate` 参数被误识别为认证方案 | 修复正则表达式;新增 Digest 明确拒绝测试 |
| 3 | RTSP 截图测试并发写同一 PNG 失败 | 固定截图路径并发冲突 | 截图在静态锁内写;测试读取唯一临时副本;临时 Bitmap 使用 `BitmapCacheOption.OnLoad`;临时文件测试结束后删除 |

### 3. 验证记录

| 验证项 | 结果 |
| --- | --- |
| `pwsh -NoProfile -File scripts/build.ps1` | 通过,0 警告 0 错误 |
| Domain 测试 | 54 / 54 |
| Application 测试 | 28 / 28 |
| Network Adapter 测试 | 10 / 10 |
| HTTP Adapter 测试 | 15 / 15 |
| MQTT Adapter 测试 | 8 / 8 |
| Video Adapter 测试 | 17 / 17 |
| App 测试 | 392 / 392 |
| 全解决方案测试 | **524 / 524 通过** |
| 真实启动 + 关闭 | 成功;主窗口正常关闭,退出码 **0** |
| 浅色截图 | `screenshots/stage05-rtsp-light.png` |
| 深色截图 | `screenshots/stage05-rtsp-dark.png` |
| 页面布局 | 1366×768 渲染通过;参数输入/授权确认/运行控制/结果区/复制导出/日志可视 |
| 主题差异 | 浅色与深色截图差异 > 5% |
| 状态语义 | DNS 失败、TCP 超时、OPTIONS 超时、401/认证失败、无效 SDP、用户取消分别表达;`Skipped ≠ Failed`、`Unknown ≠ Completed`、`Cancelled ≠ Failed` |
| 脱敏 | RTSP URL 密码不进入日志、事实、复制与导出;显示/日志/事实/导出统一脱敏 |

### 4. 尚未验证(如实声明)

- 已实现:DNS、TCP、OPTIONS、DESCRIBE、SDP、Basic 认证重试、脱敏、复制/JSON 导出。
- 未实现:Digest 认证、SETUP/PLAY、RTP 接收、媒体解码、首帧、持续播放、ONVIF、海康/大华 SDK。
- 当前结论只能证明"RTSP 信令和 SDP 可用",不能证明"视频已经播放"。
- Windows 10 真机、125%/150%/200% 高 DPI 与键盘 Tab/焦点环仍属阶段 06。
- x86 运行仍受 D-08 缺少 x86 .NET 10 运行时约束;本阶段仅验证 x64。
- Zigbee、抓包、安全扫描/渗透攻防仍未接入;D-09 / D-10 边界未关闭前不以占位或假数据冒充已实现。
