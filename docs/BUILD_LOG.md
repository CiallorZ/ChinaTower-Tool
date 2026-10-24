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

---

## 阶段 05 · 报告服务（LF-010，2026-10-21）

### 1. 交付物

| 类别 | 内容 |
| --- | --- |
| 报告库 | `src/LinkForge.Reporting/`（`net10.0`，不引用 WPF）：`ReportDocument` / `ReportMetadata`、`ReportHtmlRenderer`、`ReportGenerator`、`SensitiveDataScanner` |
| 渲染 | 自包含离线 HTML，模板版本 `1.0`；不含 `script`、外链样式、远程字体或图片；所有外部数据只经 `WebUtility.HtmlEncode` 一处编码 |
| 扫描 | 生成前对全部文本执行敏感信息扫描（URL 凭据、Authorization、键值型口令、令牌、私钥）；只记录位置与类别，不保存命中原文 |
| 写入 | 扫描通过后才写文件；临时文件 + `File.Move(overwrite)` 原子替换；UTF-8 无 BOM；扫描命中时不创建 `.tmp` |
| 报告页 | `ReportPageViewModel` + `ReportPage.xaml`：真实历史列表、项目名称、预览、扫描结论、保存、资源管理器定位、空/加载/运行/失败/取消状态 |
| 保存边界 | `IReportFileExporter` 抽象保存对话框；生成后只做 `/select` 定位，不直接执行 HTML |
| 导航/DI | `reports` 改为 `Available`；注册 `IReportGenerator`、`IReportFileExporter`、`ReportPageViewModel` |
| 测试 | Reporting 42 项（渲染、扫描、生成、取消、写入失败、并发、BOM、注入转义）；App 新增报告页视图模型与页面渲染/截图测试 |

### 2. 本阶段发现并修复的真实缺陷

| # | 现象 | 根因 | 处理 |
| --- | --- | --- | --- |
| 1 | 取消生成时抛 `OperationCanceledException` 到调用方 | 扫描前直接 `ThrowIfCancellationRequested` | 改为返回 `ReportFailureKind.Cancelled` 结果 |
| 2 | 非法路径（含 `\0`）异常冒到 UI | 未捕获 `ArgumentException` | 写入 catch 增加 `ArgumentException`，转为可读反馈 |
| 3 | `Authorization: Bearer ***` 被误判为泄漏 | 扫描未剥离认证方案前缀 | 增加认证方案剥离；已脱敏值不再命中 |
| 4 | 历史加载完成后空状态占位不显示 | 后台线程修改属性，WPF 绑定不刷新 | 属性更新统一 `PostAsync` 回 UI 线程；新增显式 `IsEmpty` |
| 5 | 列表选中项深蓝底 + 深灰字不可读 | 默认 `ListBoxItem` 选中态使用系统强调色 | 新增令牌化的 `LfReportRunListBoxItemStyle` |
| 6 | 跳过原因在页面预览中重复显示并被截断 | 摘要已含“已跳过：原因”，预览又渲染一次 | 移除页面重复渲染；本次同时修复 HTML 报告中的同源重复 |

### 3. 验证记录

| 验证项 | 结果 |
| --- | --- |
| `pwsh -NoProfile -File scripts/build.ps1` | 通过，0 警告 0 错误 |
| Domain 测试 | 54 / 54 |
| Application 测试 | 29 / 29 |
| Network Adapter 测试 | 10 / 10 |
| HTTP Adapter 测试 | 15 / 15 |
| MQTT Adapter 测试 | 8 / 8 |
| Video Adapter 测试 | 17 / 17 |
| Reporting 测试 | 42 / 42 |
| App 测试 | 417 / 417 |
| 全解决方案测试 | **592 / 592 通过** |
| 真实启动 + 关闭 | 成功；主窗口正常关闭，退出码 **0** |
| 浅色截图 | `screenshots/stage05-report-light.png` |
| 深色截图 | `screenshots/stage05-report-dark.png` |
| 报告页布局 | 1366×768 渲染通过；历史列表、项目名称、扫描结论、步骤预览与操作按钮可视 |
| 主题差异 | 浅色与深色截图差异 > 5% |
| 报告内容核验 | 生成的样例 HTML 经浏览器可访问性树确认：中文、状态、计数、跳过原因、建议、脱敏说明与页脚均正确渲染 |
| 离线自包含 | 输出无 `script` / `link` / `img` / `@import`，无 `http(s)` 远程引用 |
| 注入转义 | `<script>`、事件属性、`iframe`、`</style>` 载荷均被转义 |
| BOM / 原子写入 | UTF-8 无 BOM；写入失败与取消均不留半成品或临时文件 |

### 4. 尚未验证（如实声明）

- 已实现：离线自包含 HTML 报告；产品版本、项目、目标、时间、状态、步骤、
  结构化事实、证据元数据与建议；生成前敏感信息扫描；XSS 转义；
  UTF-8 无 BOM；原子写入；取消与写入失败反馈；真实历史为唯一数据源。
- 未实现：PDF / Markdown / CSV 导出、企业模板、数字签名与校验和、
  项目级对比、自动巡检报告（PRD §3.9 P1/P2）。
- 未验证：Windows 10 真机、125% / 150% / 200% 高 DPI、
  打印版式实机验证；x86 仍受 D-08 约束。
- 报告结论只能证明“本次任务产生了这些观测结果”，不等于验收通过。
- 厂商 SDK（D-09）、抓包与安全扫描/渗透攻防（D-10）仍未接入。

---

## 阶段 05 · 通用开发者工具（LF-011，2026-10-21）

### 1. 交付物

| 类别 | 内容 |
| --- | --- |
| 转换库 | `src/LinkForge.DeveloperTools/`（`net10.0`，零第三方依赖）：`DeveloperTransforms` 提供 JSON 格式化/压缩/校验、URL 编码/解码、Base64 编码/解码、Unix 时间戳转换、UUID 生成与 JWT 载荷解码 |
| 边界处理 | 输入按 UTF-8 字节预检，上限 1 MiB；未配对代理字符、非法 `%` 序列、非法 Base64、无效 JSON/JWT 均返回可读错误；JWT 只解码，不验证签名、有效期或签发者 |
| 服务 | `IDeveloperTransformExecutor` + `DeveloperTransformExecutor`（`Task.Run` 异步执行）；`IDeveloperResultClipboard` 隔离剪贴板；不写日志、历史或报告 |
| 开发者工具页 | `DeveloperToolsViewModel` + `DeveloperToolsPage.xaml`：10 个工具目录、输入/输出、运行/取消、状态与错误、复制结果、空/运行/成功/失败/取消状态 |
| 导航/壳层 | `developer` 改为 `Available`；`ShellViewModel.DeveloperTools`、`DeveloperToolsContent` 与 `DataContext` 接线；新增 `LfDeveloperToolListBoxItemStyle` 与 LF-011 尺寸令牌 |
| 测试 | DeveloperTools 纯逻辑 41 项；App 新增视图模型与页面测试；导航守卫核对每个 `Available` 模块均有 DI 注册、内容宿主和精确的 `DataContext` 绑定 |

### 2. 本阶段发现并修复的真实缺陷

| # | 现象 | 根因 | 处理 |
| --- | --- | --- | --- |
| 1 | URL 编码遇到未配对代理字符时崩溃 | `Uri.EscapeDataString` 对非法 UTF-16 直接抛异常 | 入口检测代理对完整性，返回可读失败结果 |
| 2 | URL 解码遇到非法 `%` 序列时异常冒到 UI | 未校验百分号后两位是否为十六进制 | 增加 `HasValidSurrogatePairs` / `IsHex` 预检，明确失败 |
| 3 | 工具列表在 1366×768 下被固定高度裁切 | 列表使用固定 `Height` | 改为 `MaxHeight` 令牌，保留滚动并按内容收缩 |
| 4 | “结果只读”说明压到输出框边框 | 行间距沿用了过小的纵向令牌 | 改用 `LfRowSpacing`，重新验证页面布局 |
| 5 | 导航接线守卫因字符串匹配写成半截引号而误报 | 守卫期望 `DataContext="{Binding X}"`，实际只匹配到 `X` | 修正守卫为完整结束引号和花括号；发现并确认接线本身正确 |

### 3. 验证记录

| 验证项 | 结果 |
| --- | --- |
| `pwsh -NoProfile -File scripts/build.ps1` | 通过，0 警告 0 错误 |
| Domain 测试 | 54 / 54 |
| DeveloperTools 测试 | 41 / 41 |
| Reporting 测试 | 42 / 42 |
| Application 测试 | 30 / 30 |
| Network Adapter 测试 | 10 / 10 |
| HTTP Adapter 测试 | 15 / 15 |
| MQTT Adapter 测试 | 8 / 8 |
| Video Adapter 测试 | 17 / 17 |
| App 测试 | 445 / 445 |
| 全解决方案测试 | **662 / 662 通过** |
| 真实启动 + 关闭 | 成功；主窗口正常关闭，退出码 **0** |
| 浅色截图 | `screenshots/stage05-developer-light.png` |
| 深色截图 | `screenshots/stage05-developer-dark.png` |
| 页面布局 | 1366×768 渲染通过；10 个工具全部可达，输入、结果与操作区可视 |
| 主题差异 | 浅色与深色截图差异 > 5% |
| 状态语义 | 空、运行、成功、失败、取消分别表达；取消不记为失败 |
| 脱敏边界 | 工具不写日志/历史/报告；JWT 仅解码展示，页面醒目标注签名未验证 |

### 4. 尚未验证（如实声明）

- 已实现：JSON 格式化/压缩/校验、URL 编码/解码、Base64 编码/解码、
  Unix 时间戳转换、UUID 生成、JWT 载荷解码；1 MiB 输入上限；
  异步执行与取消；复制结果；空/运行/成功/失败/取消状态。
- 未实现：XML / YAML 格式化、Hex 视图、文件哈希、正则测试、文本差异
  （PRD 后续 P1/P2 项）；JWT 签名验证、有效期校验和 JWK 解析。
- 未验证：Windows 10 真机、125% / 150% / 200% 高 DPI、
  键盘 Tab 顺序与焦点环；这些属阶段 06。
- x86 运行仍受 D-08 缺少 x86 .NET 10 运行时约束；本阶段仅验证 x64。
- 厂商 SDK（D-09）、抓包与安全扫描/渗透攻防（D-10）仍未接入。

## V1.0 正式发布验证（2026-10-24）

### 1. 构建与测试

| 验证项 | 结果 |
| --- | --- |
| 正式门禁提交 | `879de49f4640c1f0c6455636e45f01d578d2319b` |
| `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/build-release.ps1 -Formal` | 通过，0 警告 0 错误 |
| 发布渠道 / 工作区 | `formal` / `dirty=false` |
| Domain / Application / Persistence | 54 / 142 / 41 |
| Network / HTTP / MQTT / RTSP / ONVIF | 10 / 15 / 8 / 17 / 14 |
| DeveloperTools / Reporting / Plugin.Hosting | 41 / 42 / 30 |
| App | 589 / 589 |
| **全解决方案测试** | **1003 / 1003 通过** |
| 干净检出 Release 验证 | `879de49f4640...` 克隆、还原、构建和 **1003 / 1003** 测试通过 |

新增 `ReleasePackagingTests` 8 项，覆盖：

- Inno 文件版本 `1.0.0` → `1.0.0.0`、`0.5.0-preview.1` → `0.5.0.1`
  及非法版本拒绝；
- 安装器同时声明 x64/x86 自包含载荷、用户级安装目录和简体中文语言包；
- 正式发布门禁、未签名元数据、安装器第三方声明和卸载保留用户数据；
- 应用清单的 `asInvoker`、PerMonitorV2 和 long path 声明；
- 随仓库固定的 Inno 中文语言包及其 MIT 许可证文本。

### 2. 安装器与架构实测

| 验证项 | 结果 |
| --- | --- |
| Inno Setup 安装器编译 | 成功；简体中文语言包从仓库读取 |
| 静默安装 | 退出码 0 |
| 重复安装升级 | 退出码 0 |
| 安装版启动/关闭 | 两次退出码均为 0 |
| 卸载 | 退出码 0；程序与开始菜单入口删除 |
| `%LOCALAPPDATA%\LinkForge` 用户数据 | 保留 |
| PE 架构检查 | `win-x64` 载荷为 x64；`win-x86` 载荷为 x86 |
| x86 自包含程序启动/关闭 | 退出码 0 |
| x64 独立载荷启动/关闭 | 退出码 0 |

验证中发现并修复：

1. Inno 中文语言包原先引用本机 `compiler:Languages`，本机缺失导致编译失败；
   现固定 MIT 许可语言包到 `installer/Languages`。
2. 验证脚本使用 `/GROUP` 生成唯一开始菜单目录，但 Inno 在
   `DisableProgramGroupPage=yes` 时明确规定忽略该参数；现改为使用真实
   `LinkForge` 目录，并在目录已存在时拒绝测试，避免误删用户快捷方式。
3. 带空格参数原先可能被 `Start-Process` 拆分；现对 `/DIR` 与 `/LOG`
   显式加引号。
4. 正式发布门禁首次执行时，截图用例会重写受版本控制的 `screenshots/`，
   使测试成功后工作区仍为脏状态。截图测试现默认写入进程级临时目录；
   仅设置 `LINKFORGE_UPDATE_SCREENSHOTS=1` 时才更新仓库证据图。
5. 发布脚本在 Windows PowerShell 5.1 下存在 JSON 反序列化、哈希 API
   和相对路径 API 兼容问题；现改用 5.1 可用实现。
6. `ReleasePackagingTests` 原先位于被 `.gitignore` 排除的目录，导致干净
   检出少 8 项测试；现迁入可跟踪的 `tests/LinkForge.App.Tests/Packaging/`。

### 3. 最终发布产物

| 产物 | 字节数 | SHA-256 |
| --- | ---: | --- |
| `LinkForge-Setup-1.0.0-879de49f4640.exe` | 96,228,385 | `9ac224a6ad4454f507ad030ffc5606bdf88dca0513e6a972e50a5cc13fd70ff1` |
| `LinkForge-1.0.0-879de49f4640-win-x64-portable.zip` | 69,884,361 | `0b16fa1d6e985b3bc63f7c805458e6d98208ec43b7ac04d95036f3e7cec853f6` |
| `LinkForge-1.0.0-879de49f4640-win-x86-portable.zip` | 65,179,221 | `17dec419103c03f529287812ac1a5bc55648617eb33f3cc3fac8d457c9a20ef7` |
| `LinkForge-Setup-THIRD-PARTY-NOTICES.txt` | 1,072 | `02f0dd3b98833040147d01daf91243c24392a33ddced674152220b5216782c6a` |

发布 JSON 为 `LinkForge-Release-1.0.0-879de49f4640.json`，三个主要产物均另带
`.sha256`。发布 JSON、独立文件哈希和实际文件逐项复核一致。

### 4. 正式产物边界

- 版本为 `1.0.0`，无预发布后缀；Git 标签为 `v1.0.0`。
- 安装器和便携包未签名；未完成 Windows 10 真机、32 位 Windows 安装流程、
  高 DPI 与完整可访问性最终验收。
- 安装器是 Inno Setup 用户级安装包，不是 MSIX/WiX；没有自动更新或自动回滚。
- 不得宣称 bit-for-bit 可复现；已验证的是干净 checkout 可重复构建和通过测试。

## 阶段 05 · 插件清单校验与进程外隔离（LF-012，2026-10-22）

### 1. 交付物

| 模块 | 说明 |
| --- | --- |
| `LinkForge.Plugin.Hosting` | 严格清单解析（64 KiB 上限、JSON 深度 16）、API 版本精确匹配、架构校验、入口相对路径与重解析点拒绝、SHA-256 校验、进程外单行 JSON 协议（`hello` / `executeStep`）、参数白名单、崩溃隔离、超时与取消语义、宿主侧秘密过滤 |
| `LinkForge.App/Services` | `IPluginCatalogService`（扫描 `%LOCALAPPDATA%\LinkForge\plugins`）、`IPluginRunExecutor`（三道运行边界）、`IPluginResultExporter`（剪贴板/对话框） |
| `LinkForge.App/Presentation` | `PluginDisplayItem` 展示层：id、名称、发布者、版本、API、能力、哈希前 12 位、签名状态、阻断原因 |
| `LinkForge.App/ViewModels` | `PluginManagerViewModel`：真实扫描、状态语义、来源确认、进程外运行、取消、耗时、结论、脱敏复制/导出 |
| `LinkForge.App/Views/Pages` | `PluginManagerPage`：目录与扫描、插件列表（含阻断项与原因）、详情、信任确认、运行/停止、进度、结论摘要、步骤事实、日志、空/错误状态 |
| 导航 | `settings` 改为分组节点，新增二级项 `settings.plugins`（Available）与 `settings.appearance` / `settings.logs` / `settings.about`（NotImplemented） |
| 主题 | 新增 `Themes/Controls/PluginList.xaml` 与 `LfPlugin*` 尺寸令牌 |

### 2. 本阶段发现并修复的真实缺陷

| # | 现象 | 根因 | 处理 |
| --- | --- | --- | --- |
| 1 | `PluginCatalogService` 编译失败 | `Task.Run(Func<Task<T>>)` 已自动解包，不能再 `.Unwrap()` | 去掉 `.Unwrap()` |
| 2 | App 内新增插件文件无法编译 | `LinkForge.App.csproj` 未引用 `LinkForge.Plugin.Hosting` | 补 `ProjectReference` |
| 3 | `Path` / `IOException` 报 CS0103 | WPF 项目（`UseWPF`）的隐式 using 不含 `System.IO` | 显式 `using System.IO;` |
| 4 | `OnPluginsCollectionChanged` 编译失败 | 它不是 CommunityToolkit 源生成器会声明的钩子（集合无 `[ObservableProperty]`） | 改为订阅 `Plugins.CollectionChanged` |
| 5 | 空目录时右侧仍显示上一个插件的详情 | `SelectedPlugin` 由 null 赋值为 null 不触发变更通知 | 扫描后显式 `NotifySelectionState()` |
| 6 | **目录读不到时界面显示"插件目录为空"** | `IsEmptyAfterScan` 未排除读取失败，权限问题被误导成安装问题 | 空状态排除 `HasScanFailure`，错误改由 `ErrorPanel` 呈现 |
| 7 | **复制摘要泄漏凭据明文** | 编排器只脱敏 `DiagnosticStepResult` 字段，页面自拼的纯文本摘要未过脱敏 | 页面层对摘要逐字段再脱敏（幂等） |
| 8 | **`SecretRedactor` 漏脱裸 `user:pass@host`** | 只有带 scheme 的 `UrlUserInfoPattern`，目标标签与 SDK 日志常是裸形态 | 新增 `RedactBareUserInfo`，并锁定邮箱与 `IP:port` 不被误伤 |
| 9 | **插件按 JSON 返回的秘密完全漏网** | `SkipSeparator` 未吃掉键名右侧引号，`"password":"x"` 整条规则失配 | 分隔符识别覆盖引号形态 |
| 10 | 脱敏后产出非法 JSON | 替换带引号值时把引号一起吃掉（`"password":***`） | 保留引号，只替换内容；测试断言可被 `JsonDocument.Parse` |
| 11 | **假脱敏：`Authorization: Bearer <token>` 只遮住了方案名** | 把 `Bearer` 当值遮蔽，真令牌原样留下 | 识别认证方案名并跳过，遮蔽其后的令牌 |
| 12 | 泄漏测试漏掉"建议文本"这条出口 | 夹具按 `passed` 返回，而 Passed 结论不携带建议字段 | 夹具改为 `warning`，覆盖摘要/建议/事实/原始输出四条出口 |
| 13 | 取消扫描的测试抛无关异常 | `Task.FromCanceled(token)` 要求令牌已取消，否则抛 `ArgumentException` | 改为真实"扫描进行中 → 用户点停止"路径 |
| 14 | `settings` 变分组后壳层测试失效 | 分组节点不参与导航，状态栏不再出现"设置" | 改用二级项断言，并新增"分组不得切到空白页"守卫 |
| 15 | 信任说明文案出现多余空格 | 两个 `<Run>` 之间的 XAML 换行被当作内容 | 合并为单个 `<Run>` |

### 3. 验证记录

| 验证项 | 结果 |
| --- | --- |
| `pwsh -NoProfile -File scripts/build.ps1` | 通过，0 警告 0 错误 |
| Domain 测试 | 54 / 54 |
| Application 测试（含 Plugin.Hosting 架构守卫） | 31 / 31 |
| **Plugin.Hosting 测试（真实子进程）** | **30 / 30** |
| Network / HTTP / MQTT / Video Adapter 测试 | 10 / 15 / 8 / 17 |
| DeveloperTools 测试 | 41 / 41 |
| Reporting 测试 | 42 / 42 |
| App 测试 | 480 / 480 |
| 全解决方案测试 | **728 / 728 通过** |
| 真实启动 + 关闭 | 成功；DI 可解析插件页，退出码 **0** |
| 浅色截图 | `screenshots/stage05-plugins-light.png` |
| 深色截图 | `screenshots/stage05-plugins-dark.png` |
| 页面布局 | 1366×768 渲染通过；可加载与阻断插件同时可见、原因直陈、未确认来源时运行按钮不可用 |
| 主题差异 | 浅色与深色截图差异 > 5% |
| 状态语义 | 未扫描 / 扫描中 / 空目录 / 读取失败 / 部分阻断 / 成功 / 失败 / 取消 / 未判定 分别表达 |
| 运行态可见性 | 页面测试在真实运行过程中断言进度面板已渲染，而非仅断言视图模型布尔值 |
| 边界语义 | 取消 ≠ 失败；跳过 ≠ 失败；未判定 ≠ 完成；拒绝运行（未确认来源）不记为执行失败 |
| 脱敏边界 | 参数白名单 + 秘密键名拒绝下发；插件返回文本在宿主出口再过滤；复制与导出逐字段脱敏 |
| 崩溃隔离 | 夹具 `FailFast` 崩溃映射为 Failed，宿主进程存活断言通过 |
| 架构守卫 | `Plugin.Hosting` 不引用 App / Application / WPF，零 NuGet 依赖 |

### 4. 尚未验证（如实声明）

- 已实现：清单严格校验（API、架构、入口哈希、重解析点拒绝）、进程外 JSON 协议、
  握手身份核对、超时与取消、崩溃隔离、参数白名单、宿主侧秘密过滤、
  插件管理页与 `settings.plugins` 导航、来源显式确认、脱敏复制与 JSON 导出。
- **未实现**：海康 / 大华开放平台 SDK 适配（D-09 许可、位数、再分发均未确认）；
  ONVIF；RTP 接收与媒体解码；插件签名**证书链验证**；插件市场/在线安装与更新；
  插件权限沙箱（当前仅"进程外 + 参数白名单 + 无秘密下发"）。
- **签名口径**：枚举只有 `Unsigned` 与 `DeclaredSignedUnverified`，**不存在 Verified**。
  界面只能显示"声明已签名（未验证）"或"未签名"，不得表述为已验证。
- **剩余风险**：宿主不向插件提供秘密库 API、不下发秘密明文，但插件以当前用户身份运行，
  仍可自行读取该用户可访问的磁盘文件与网络；同用户的恶意本地进程不受本边界保护。
- 测试夹具 `LinkForge.TestPluginHost` 不访问网络与文件，仅验证宿主边界；
  真实厂商 SDK 的进程外行为尚未验证。
- 未验证：Windows 10 真机、125% / 150% / 200% 高 DPI、键盘 Tab 顺序与焦点环（阶段 06）。
- x86 运行仍受 D-08 缺少 x86 .NET 10 运行时约束；本阶段仅验证 x64。
- 抓包与安全扫描 / 渗透攻防（D-10）仍未接入。

## 阶段 05 · 项目持久化与资产管理（LF-004，2026-10-22）

### 1. 交付物

- `src/LinkForge.Persistence/`（`net10.0`，零 WPF 依赖）
  - `LinkForgeDbContext` / `LinkForgeDatabase` / `SqliteContextFactory`
  - `SqliteProjectStore` / `SqliteAssetStore` / `SqliteEnvironmentStore`
  - EF Core 迁移 `20261021193558_InitialCreate`（结构升级走迁移，不手工改表）
  - 数据库默认路径 `%LOCALAPPDATA%\LinkForge\linkforge.db`
- `src/LinkForge.Application/` 存储契约与纯逻辑
  - `Projects/IProjectStore` / `IEnvironmentStore`、`Assets/IAssetStore`（全异步 + 取消令牌）
  - `Storage/StorageException` / `IStorageInitializer` / `DatabaseInitializationResult`
  - `Assets/AssetCsvCodec`（RFC 4180 手写实现、UTF-8 BOM + CRLF）
  - `Assets/AssetCredentialReference`（引用键白名单）、`Assets/AssetTagText`
  - `Projects/EnvironmentVariableText`（`KEY=VALUE` 解析 + 秘密语义拒绝）
- `src/LinkForge.App/`
  - `Services/AssetCsvImportService` / `AssetCsvExportService` / `AssetCsvFileService`
  - `ViewModels/AssetCatalogViewModel`（项目/环境/资产 CRUD、筛选、分组、导入导出）
  - `Views/Pages/AssetCatalogPage.xaml`（三栏：项目与环境 | 资产列表 | 资产表单）
  - 导航 `assets` 由 `NotImplemented` 改为 `Available`

### 2. 本阶段发现并修复的真实缺陷

| # | 现象 | 根因 | 处理 |
| --- | --- | --- | --- |
| 1 | **筛选下拉框显示为空白** | `SelectedKindOption`（筛选）从未初始化，而 `SelectedItem` 为 null 的 ComboBox 只渲染空框 | 构造函数显式选中"全部类型"，并加回归测试 |
| 2 | **左栏"创建环境"按钮被裁到视口外** | 页面是三栏里唯一没有页面级 `ScrollViewer` 的业务页，内容超出后直接裁掉 | 加页面级 `ScrollViewer` + 显式 `Auto` 行 + `VerticalAlignment="Top"` |
| 3 | ScrollViewer 加了仍不滚动 | 内层 Grid 只有 `ColumnDefinitions`，隐式行是 `*`，在 ScrollViewer 里被解析成视口高度 | 显式声明 `<RowDefinition Height="Auto" />` |
| 4 | **"读不到数据库"被显示成"当前项目还没有资产"** | `IsEmptyAfterLoad` 未排除加载失败 | 空状态排除 `HasLoadFailure`，错误改由 `ErrorPanel` 呈现 |
| 5 | **环境变量 `TOKENIZER` / `COOKIEJAR` 被误判为凭据** | 秘密检测用整串子串匹配 | 改为按分隔符与驼峰切词段后逐段比对，并补"相邻段拼接"检查 |
| 6 | **环境变量行号整体错位、往返不稳定** | `Parse` 把 `\r` 与 `\n` 当两个独立分隔符切，CRLF 会多切出空行 | 改为按 `\r\n` / `\r` / `\n` 整体切 |
| 7 | `Environment.NewLine` 编译不过 | `LinkForge.Application.Environment` 命名空间遮蔽了 `Environment` 类型 | 全限定为 `System.Environment.NewLine` |
| 8 | `AssetCsvCodec` 里的标识符校验被误删 | 拆分 `IsValidIdentifier` 时连带删掉了资产 id 校验 | 拆成 `IsValidAssetId`（白名单 `.` `_` `-`，≤64，不含 `:`）+ 复用 `AssetCredentialReference.IsValidKey` |
| 9 | 页面按钮可访问性断言误报 | `EmptyState` 无主操作时按钮是 `Collapsed`，折叠按钮不在无障碍树里 | 断言跳过非 `Visible` 按钮 |
| 10 | 首轮全量构建报 `Plugin.Hosting.Tests` 编译错误 | 新增 `ProjectReference` 后 `--no-restore` 用了过期还原产物 | 先跑还原再构建；改动 `csproj` 后必须还原 |
| 11 | **工作台谎称"项目管理功能将在后续阶段交付"** | `HasProjects` 在阶段 04 被硬编码为 `false`，文案写死"后续阶段交付" | 改为读取真实项目数量，文案改为引导到"设备与资产"页；读不到时写错误面板而非显示"没有项目" |
| 12 | **工作台谎称"视频接入 / IoT / 网络 / API 将在后续阶段交付"** | 快捷动作区的说明文字写于阶段 04，一直没随交付更新 | 改为列出已交付模块并指向左侧导航 |

### 3. 验证记录

| 验证项 | 结果 |
| --- | --- |
| `pwsh -NoProfile -File scripts/build.ps1` | 通过，0 警告 0 错误 |
| Domain 测试 | 54 / 54 |
| Application 测试（含 Persistence 架构守卫） | 133 / 133 |
| **Persistence 测试（真实 SQLite 文件）** | **41 / 41** |
| Network / HTTP / MQTT / Video Adapter 测试 | 10 / 15 / 8 / 17 |
| DeveloperTools 测试 | 41 / 41 |
| Reporting 测试 | 42 / 42 |
| Plugin.Hosting 测试 | 30 / 30 |
| App 测试 | 519 / 519 |
| 全解决方案测试 | **910 / 910 通过** |
| 真实启动 + 关闭 | 成功；退出码 **0** |
| 依赖注入装配 | 30 个已注册服务逐一强制解析，全部成功 |
| 浅色截图 | `screenshots/stage05-assets-light.png` |
| 深色截图 | `screenshots/stage05-assets-dark.png` |
| 页面布局 | 1366×768 渲染通过；三栏可达，超高内容可滚动而非裁切 |
| 主题差异 | 浅色与深色截图差异 > 5% |
| 状态语义 | 未加载 / 加载中 / 读取失败 / 空库 / 有资产 / 导入部分成功 / 导入失败 / 导出失败 / 取消 分别表达 |
| 存储语义 | 唯一冲突映射为可读 `StorageException`；删除项目级联清除资产；批量写入事务化 |
| 凭据边界 | 资产只存引用键；环境变量名含密码/令牌语义时拒绝该行；明文不进入模型、数据库、CSV、日志 |
| 架构守卫 | `Persistence` 不引用 App / WPF / 任何适配器；`App` 必须引用 `Persistence` |

### 4. 尚未验证（如实声明）

- 已实现：SQLite + EF Core 迁移、数据库损坏隔离（改名保留并重建）、项目/环境/资产 CRUD、
  资产目录页、CSV 导入导出（部分成功逐行报告）、凭据引用键校验、环境变量秘密语义拒绝。
- **未实现**：数据库加密（当前数据库文件本身不加密，只是不存明文凭据）；
  项目导入导出（当前只有资产 CSV）；资产凭据存储本体（只有引用键，没有对应的凭据库）；
  多用户 / 多机同步；数据库备份与恢复界面。
- **凭据边界**：`CredentialReference` 只是一个指针，实际凭据库尚未实现 ——
  填入引用键不会让应用自动取到密码，这是当前的设计事实，不得对外表述为"已接入凭据管理"。
- 未验证：Windows 10 真机、125% / 150% / 200% 高 DPI、键盘 Tab 顺序与焦点环（阶段 06）。
- x86 运行仍受 D-08 缺少 x86 .NET 10 运行时约束；本阶段仅验证 x64。
- 海康 / 大华开放平台 SDK（D-09）、抓包与安全扫描 / 渗透攻防（D-10）仍未接入。

## 阶段 05 · 结构化日志与脱敏（LF-005，2026-10-22）

### 1. 交付物

- `src/LinkForge.Application/Logging/`
  - `StructuredLogRecord`：`Timestamp / Level / Module / RunId / TargetId / EventId / Message`
  - `IStructuredLogStore` + `JsonLineStructuredLogStore`
  - 按日文件 `%LOCALAPPDATA%\LinkForge\logs\linkforge-yyyyMMdd.jsonl`
  - 写入前与读取时均经过统一 `ITextRedactor`；损坏行跳过
- `src/LinkForge.App/`
  - `StructuredLogService`：页面日志聚合、5000 条内存上限、后台 Channel 写盘、关闭 flush
  - `LogPanelExporter`：复制与导出系统边界
  - `ShellViewModel`：真实日志筛选、最低级别、复制可见、导出、取消与错误状态
  - `LogViewer`：搜索覆盖消息/模块/RunId/TargetId/EventId，区分空日志与“筛选无匹配”
  - `ShellWindow.xaml`：日志面板绑定真实集合、筛选条件和命令
- 报告路径继续在 `ReportGenerator` 生成前扫描敏感信息；命中时拒绝写文件。

### 2. 本阶段发现并修复的真实缺陷

| # | 现象 | 根因 | 处理 |
| --- | --- | --- | --- |
| 1 | 日志只存在内存，重启后全部消失 | 阶段 03 只有局部 `ObservableCollection`，没有持久化契约 | 新增 Application 层 JSONL 存储与 App 级聚合服务 |
| 2 | 日志面板筛选像“假的”：输入后仍显示全部 | 搜索框与最低级别没有进入真实过滤集合 | `ShellViewModel` 维护可见集合，消息/模块/RunId/TargetId/EventId 全部参与搜索 |
| 3 | 日志面板把读不到历史文件显示成“没有日志” | 空集合与存储错误共用同一空态 | 增加独立 `StorageError` 与状态文本，区分空日志、无匹配、读写失败 |
| 4 | 关闭应用时可能丢最后几条日志 | UI 发布后没有等待写盘队列 | 关闭前有限等待 `FlushAsync`，队列关闭后给出存储错误而不是静默丢弃 |
| 5 | 嵌套 JSON 中的秘密容易漏脱敏 | 仅覆盖顶层键值与常见 Header | 扩展统一脱敏规则并增加嵌套 JSON、Bearer 令牌端到端测试 |
| 6 | 既有 nullable 代码在 `warnaserror` 下失败 | 新测试编译开启警告即错误，暴露旧测试夹具可空性 | 修复 `PluginSecretFilterTests` 与 `AssetCsvCodecTests` 的可空声明 |
| 7 | 完整测试顺序下日志条目与写入错误偶发不可见 | 服务使用全局 `Application.Current.Dispatcher`，可能投到其他测试创建的无关消息泵 | 构造时捕获当前线程 Dispatcher；无 Dispatcher 的宿主直接执行，并定向/全量回归 |

### 3. 验证记录

| 验证项 | 结果 |
| --- | --- |
| `pwsh -NoProfile -File scripts/build.ps1` | 通过，0 警告 0 错误 |
| Domain 测试 | 54 / 54 |
| Application 测试（含 JSONL 存储） | 140 / 140 |
| Persistence 测试 | 41 / 41 |
| Network / HTTP / MQTT / Video Adapter 测试 | 10 / 15 / 8 / 17 |
| DeveloperTools / Reporting / Plugin.Hosting 测试 | 41 / 42 / 30 |
| App 测试 | 537 / 537 |
| 全解决方案测试 | **935 / 935 通过** |
| 真实启动 + 关闭 | 成功；退出码 **0** |
| 日志字段 | 七字段 JSONL 往返稳定，稳定 `EventId` |
| 筛选/复制/导出 | 仅作用于当前可见日志；取消 ≠ 失败；写入失败有独立反馈 |
| 脱敏 | UI、磁盘、读取、复制、导出与报告路径均经过统一脱敏测试 |
| 存储韧性 | 损坏 JSONL 行跳过，不影响其他记录与应用启动 |

### 4. 尚未验证（如实声明）

- 已实现：按日 JSONL 持久化、应用级日志聚合、5000 条内存上限、异步写入、
  关闭 flush、搜索/级别筛选、复制可见、导出、存储错误反馈、统一脱敏。
- **未实现**：日志文件加密、轮转保留与自动清理、压缩归档、全文索引、
  日志签名/完整性校验、原始异常堆栈的独立安全详情查看面。
- 日志文件本身不加密；当前保证是“写入前不落明文凭据”，不是“文件已受保护”。
- 强制终止进程时，队列中尚未写入的日志仍可能丢失；正常关闭会有限等待 flush。
- Windows 10 真机、高 DPI、键盘 Tab 顺序与焦点环仍属阶段 06。
- x86 运行仍受 D-08 缺少 x86 .NET 10 运行时约束；本阶段仅验证 x64。
- 海康 / 大华开放平台 SDK（D-09）、抓包与安全扫描 / 渗透攻防（D-10）仍未接入。

## 阶段 06 · 发布工程增量（LF-013，2026-10-22）

### 1. 交付物

- `Directory.Build.props`：统一 `0.5.0-preview.1`、产品/公司/仓库与确定性构建元数据。
- `CHANGELOG.md`：当前预览版本的能力、安全边界与已知限制。
- `scripts/publish.ps1`：默认执行 Release 全量测试门禁，发布 `win-x64` 自包含
  便携 ZIP，生成 `release.json`、`VERSION.txt`、依赖/许可证清单、
  CycloneDX 1.5 SBOM、包内 `SHA256SUMS.txt` 与 ZIP SHA-256。
- `scripts/verify-clean-checkout.ps1`：克隆已提交 `HEAD` 到临时目录并重新执行
  还原、构建和测试。
- `tests/LinkForge.App.Tests/Services/OperationsWorkflowEndToEndTests.cs`：
  SQLite 项目/资产 → TCP 诊断 → JSONL 日志 → HTML 报告的真实组件链路。
- `docs/RELEASE.md`：便携包、版本、依赖、签名、升级/卸载和未完成项说明。

### 2. 本阶段发现并修复的真实缺陷

| # | 现象 | 根因 | 处理 |
| --- | --- | --- | --- |
| 1 | 端到端诊断被误判为 `Failed`，摘要为 `NullReferenceException` | 编排器在调用方未提供进度回调时，把 `null` 传给适配器的非空 `IProgress<T>` 契约 | 编排器统一注入无操作进度器；新增 `DiagnosticTaskRunner` 回归测试 |
| 2 | 发布脚本启动即报 `The term 'if' is not recognized` | `Get-ConfiguredVersion` 使用 `return if (...) { ... }`，PowerShell 将 `if` 当作命令 | 改为显式 `if` 块 + `return` |
| 3 | `VERSION.txt` 的 `-join` 可能被解析成函数参数 | `Write-Utf8NoBom` 调用中直接混写数组与 `-join` | 先构造 `$versionLines`，再显式 `-join` |
| 4 | SBOM 每次生成随机 UUID | 使用 `[Guid]::NewGuid()` | 由版本与提交派生稳定 UUID，不宣称 bit-for-bit 可复现 |

### 3. 验证记录

| 验证项 | 结果 |
| --- | --- |
| Debug `pwsh -NoProfile -File scripts/build.ps1` | 通过，0 警告 0 错误 |
| Release `pwsh -NoProfile -File scripts/build.ps1 -Configuration Release` | 通过，0 警告 0 错误 |
| Domain / Application / Persistence 测试 | 54 / 141 / 41 |
| Network / HTTP / MQTT / Video Adapter 测试 | 10 / 15 / 8 / 17 |
| DeveloperTools / Reporting / Plugin.Hosting 测试 | 41 / 42 / 30 |
| App 测试 | 538 / 538 |
| **全解决方案测试（Debug 与 Release 分别执行）** | **937 / 937 通过** |
| 发布脚本本地诊断包 | `publish.ps1 -AllowDirty -SkipTests` 成功生成 `.dirty` 便携 ZIP |
| 依赖清单 | 25 个 NuGet 组件，许可证声明可解析 |
| SBOM | CycloneDX 1.5 可解析；25 个组件；序列号由版本/提交确定 |
| 包内 SHA-256 | 逐文件复核 0 失败；ZIP 另行生成 `.sha256` |
| 自包含程序启动 | `LinkForge.App.exe` 真实启动，关闭退出码 **0** |

### 4. 尚未验证（如实声明）

- 当前是 `0.5.0-preview.1`，不是 V1.0 正式版。
- 便携 ZIP **未签名、不是安装器**；未完成代码签名、时间戳、SmartScreen、
  MSIX/WiX/Inno Setup、升级失败回滚与卸载残留测试。
- 未完成 Windows 10 真机、高 DPI、键盘焦点与可访问性最终验收。
- x86 运行仍受 D-08 约束；本阶段只发布并验证 `win-x64`。
- 厂商 SDK（D-09）、抓包、安全扫描与渗透攻防（D-10）未接入。
- 依赖许可证清单只反映 NuGet nuspec 声明，不替代法律审查。
- 干净 checkout 验证已对提交 `3bd8af769ad1` 执行通过：克隆、还原、构建与
  937 / 937 测试全部成功。最终干净版 ZIP SHA-256 为
  `9c542574a73f899b957e5694e6c2a15aaeb8a4f4ba0d4df9c1d6376007272c8e`。

## 阶段 05 · ONVIF 标准接入（LF-014，2026-10-24）

### 1. 交付物

- `src/LinkForge.Adapters.Onvif/`
  - WS-Discovery UDP `239.255.255.250:3702`
  - `GetDeviceInformation`、`GetCapabilities`、`GetProfiles`、`GetStreamUri`
  - SOAP over HTTP(S)，TLS 证书校验始终启用
  - WS-Security UsernameToken、2 MiB 响应上限、禁止 DTD 与 XML 解析限制
- `src/LinkForge.App/ViewModels/OnvifDiagnosticsViewModel.cs`
- `src/LinkForge.App/Views/Pages/OnvifDiagnosticsPage.xaml`
- `tests/LinkForge.Adapters.Onvif.Tests/`
- App 测试：ViewModel、页面、Shell 导航/互斥、依赖注入与架构守卫
- 截图：`screenshots/stage05-onvif-light.png` / `screenshots/stage05-onvif-dark.png`

### 2. 本阶段发现并修复的真实缺陷

| # | 现象 | 根因 | 处理 |
| --- | --- | --- | --- |
| 1 | 三级导航节点 `connect.video.rtsp` / `connect.video.onvif` 无法被 `Find` 命中 | 导航目录只遍历两级，视频分组拆分后子节点不可达 | `NavigationCatalog.Enumerate` 改为递归深度优先遍历 |
| 2 | 发现窗口内单条畸形组播报文可能打断整轮发现 | 同一接收循环直接处理所有报文 | 隔离坏包、继续等待窗口结束，坏包不进入原始输出 |
| 3 | “发现到设备”与“取得 Stream URI”容易被误读为媒体已播放 | ONVIF 信令能力与媒体接收/解码边界不同 | 页面、结果、文档统一声明未执行 RTP/解码/首帧，相关步骤保持 `Skipped` |
| 4 | 凭据可能通过 SOAP 调试文本或 Stream URI 进入复制/导出 | 认证材料与 URL 会出现在请求/响应表面 | 凭据只进入认证 Header；显示、日志、事实、复制与 JSON 导出统一脱敏 |
| 5 | 本地未发现设备时若映射为成功会给出错误结论 | `Unknown` 与 `Completed` 语义不同 | 保持 `Unknown`，文案明确“不代表网络中没有 ONVIF 设备” |

### 3. 验证记录

| 验证项 | 结果 |
| --- | --- |
| 解决方案构建 | 通过，0 警告 0 错误 |
| ONVIF 适配器测试 | 14 / 14 |
| App ONVIF / 导航 / Shell / DI 定向测试 | 91 / 91 |
| App 测试 | 561 / 561 |
| **全解决方案测试** | **975 / 975 通过** |
| 真实启动 + 关闭 | 成功；退出码 **0** |
| 干净检出验证 | `d97a079` 克隆、还原、构建与 975 / 975 测试全部通过 |
| 截图 | 1366×768；浅色均值 `(236.6,236.8,237.2)`，深色均值 `(100.8,102.8,106.7)` |
| 脱敏 | WS-Security 用户名/口令、服务 URI userinfo 与 Stream URI 凭据在 UI/日志/事实/复制/导出中统一处理 |
| 发现韧性 | 单条坏组播包隔离；未发现设备保持 `Unknown` |

### 4. 尚未验证（如实声明）

- 已实现：WS-Discovery、设备信息、能力、媒体 Profile、Stream URI、
  WS-Security UsernameToken、结构化阶段结果、取消、复制与 JSON 导出。
- **未实现**：RTP 接收、媒体解码、首帧、持续播放、PTZ、事件、录像、
  固件升级、海康/大华开放平台 SDK 与 GB/T 28181。
- **Stream URI 可用不等于视频已经播放。**
- 未覆盖真实设备矩阵、厂商兼容性差异与复杂多网卡组播环境。
- 不执行自动网段扫描、端口扫描或厂商私有探测。
- Windows 10 真机、高 DPI、键盘 Tab 顺序与焦点环仍属阶段 06。
- x86 运行仍受 D-08 缺少 x86 .NET 10 运行时约束；本阶段仅验证 x64。
- 厂商 SDK（D-09）、抓包与安全扫描/渗透攻防（D-10）仍未接入。

## 阶段 05 增量 · MQTT 3.1.1 / 5.0 协议选择（LF-008，2026-10-24）

### 1. 交付物

- `MqttProtocolMode.V311` / `MqttProtocolMode.V500`：协议版本由连接选项显式传入。
- `MqttTransport`：真实映射 MQTTnet `MqttProtocolVersion.V311 / V500`；
  非法枚举值立即返回可读错误，不发起连接。
- MQTT 工作台新增“协议版本”选择器，默认 MQTT 3.1.1；未选择协议时拒绝连接。
- 复制摘要新增协议事实，便于区分实际使用的协议版本。
- 本地进程内 Broker 集成测试同时验证 3.1.1 与 5.0 连接路径。

### 2. 验证记录

| 验证项 | 结果 |
| --- | --- |
| 全解决方案构建 | 通过，0 警告 0 错误 |
| MQTT 适配器测试 | 10 / 10 |
| App MQTT 定向测试 | 36 / 36 |
| **全解决方案测试** | **1006 / 1006 通过** |
| 真实启动 + 关闭 | 成功；退出码 **0** |
| 截图 | `stage05-mqtt-light.png` / `stage05-mqtt-dark.png` 由页面渲染测试生成；1366×768 布局与非空/主题差异断言通过 |

### 3. 仍未交付

- 遗嘱、保留消息管理、Topic 树、自动重连与指标面板。
- Zigbee、海康/大华开放平台 SDK、抓包、安全扫描与渗透攻防。
- Windows 10 真机、高 DPI 与完整可访问性最终验收。

## 阶段 05 增量 · 项目包安全导入导出（LF-015，2026-10-24）

### 1. 交付物

- `src/LinkForge.Application/Projects/ProjectPackage.cs`、
  `ProjectPackageCodec.cs`
- `src/LinkForge.Persistence/SqliteProjectPackageImporter.cs`
- `src/LinkForge.App/Services/ProjectPackageFileService.cs`
- 资产页项目包导入/导出、运行/取消、成功/失败反馈与 CSV 互斥
- Application、Persistence、App 三层项目包测试

### 2. 关键边界

- schema 为 `linkforge.project-package` v1，UTF-8 JSON，文件上限 2 MiB。
- 项目包只含项目、环境、资产和凭据引用键；不包含密码、令牌、Cookie、
  授权头或其他秘密值。
- 读取严格拒绝未知/重复字段、未知版本/枚举、重复 ID、跨项目引用、
  多个激活环境与秘密形状字段。
- 导入默认拒绝覆盖已有项目 ID、项目名称、环境 ID 或资产 ID。
- 导入使用单个 SQLite 事务；唯一约束冲突、触发器异常和取消均整体回滚。

### 3. 验证记录

| 验证项 | 结果 |
| --- | --- |
| 全解决方案构建 | 通过，0 警告 0 错误 |
| Application 项目包定向测试 | 12 / 12 |
| Persistence 项目包定向测试 | 5 / 5 |
| App 项目包定向测试 | 12 / 12 |
| **全解决方案测试** | **1034 / 1034 通过** |
| 真实启动 + 关闭 | 成功；退出码 **0** |
| 截图 | `stage05-assets-light.png` / `stage05-assets-dark.png` 已更新并人工检查 |

### 4. 尚未验证（如实声明）

- 项目包文件未签名；格式有效不等于来源可信。
- 项目包本身不包含凭据库本体、数据库加密、项目合并/覆盖策略或跨机同步；
  LF-016 已在应用内提供独立的 CurrentUser DPAPI 凭据库。
- 尚未在 Windows 10 真机和高 DPI 环境执行最终交互验收。
- x86 运行仍受 D-08；厂商 SDK、抓包、安全扫描与渗透攻防仍受 D-09/D-10。

## 阶段 05 / V1.1 增量 · CurrentUser DPAPI 凭据库与 Windows CI（LF-016 / LF-017，2026-10-24）

### 1. 交付物

- `src/LinkForge.Application/Secrets/ISecretStore.cs`
- `src/LinkForge.App/Services/WindowsDpapiSecretStore.cs`
- `tests/LinkForge.App.Tests/Services/WindowsDpapiSecretStoreTests.cs`
- `tests/LinkForge.App.Tests/ViewModels/AssetCatalogCredentialTests.cs`
- 资产页凭据保存、状态检查和删除入口
- `.github/workflows/windows-ci.yml`

### 2. 关键边界

- 只支持 `SecretProvider.DpapiCurrentUser`；其他提供方明确失败。
- 每条秘密独立保存为 `%LOCALAPPDATA%\LinkForge\secrets\*.dpapi`；
  文件名是引用键 SHA-256，DPAPI 附加熵绑定应用标识、schema、提供方与引用键。
- `Missing / Available / Unreadable` 三态；损坏或无法解密不得显示为“未保存”。
- 保存后立即清空视图模型明文；引用键变化时也清空旧输入。
- 项目包、日志、历史、复制、导出和报告不读取秘密值。
- 凭据文件不提供跨机同步；CurrentUser DPAPI 不能防御同用户恶意软件。

### 3. 验证记录

| 验证项 | 结果 |
| --- | --- |
| Debug 构建 | 通过，0 警告 0 错误 |
| 凭据存储与资产页定向测试 | 27 / 27 |
| **全解决方案测试** | **1049 / 1049 通过** |
| 真实启动 + 关闭 | 成功；退出码 **0** |
| CI 定义 | Release 构建/测试 + 未签名 win-x64 诊断包；实际远端结果见文末 V1.1 正式发布门禁 |

### 4. 尚未验证（如实声明）

- GitHub Actions 远端首次执行结果尚未在本地日志中伪造，推送后以实际运行状态为准。
- V1.1.0 正式标签、安装器验证、干净检出与产物 SHA-256 已由文末正式门禁章节完成回填。
- 未实现 Windows Credential Manager、跨机同步、数据库加密、秘密备份恢复或密钥轮换。
- Windows 10 真机、高 DPI、完整可访问性与代码签名仍未完成。
- 厂商 SDK、抓包、安全扫描和渗透攻防仍受 D-09/D-10 约束。

## V1.1 正式发布门禁与 Windows CI（LF-017，2026-10-24）

### 1. 首次 CI 失败与真实修复

Windows CI 首次执行（[Run 40026923494](https://github.com/CiallorZ/ChinaTower-Tool/actions/runs/40026923494)）
在 `LinkForge.Plugin.Hosting.Tests` 失败：静默插件超时后，测试清理临时目录时
遇到 `UnauthorizedAccessException: Access to the path 'LinkForge.TestPluginHost.exe' is denied`。

这是 Windows 子进程终止后的真实文件句柄释放竞态，不应通过忽略异常或重跑掩盖。
修复内容：

- `PluginProcessSession.DisposeAsync` 在 `Kill()` 后等待子进程真正退出，
  再释放工作目录，避免宿主立即清理时 apphost 文件仍被占用。
- 测试夹具目录清理增加有限重试，最终仍允许系统回收临时目录，不把测试清理
  失败伪装成产品功能失败。
- 静默插件超时用例连续执行 11 次，全部通过；修订后全解决方案 Release
  构建 0 警告 0 错误，`1049 / 1049` 测试通过。
- 修复提交为 `64c5993`；后续 Windows CI 从该提交重新执行并通过。

### 2. Windows CI

| 项目 | 实际结果 |
| --- | --- |
| Run | [40027976323](https://github.com/CiallorZ/ChinaTower-Tool/actions/runs/40027976323) |
| 提交 | `64c5993e572dae7879baf0819fed0c14898e7f85` |
| 结果 | 通过，3m43s |
| 门禁步骤 | Release 构建、全量测试、未签名 `win-x64` 诊断包、上传产物 |
| 边界 | CI 产物是未签名诊断包，不替代本地正式门禁 |

### 3. 正式发布门禁

执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\build-release.ps1 -Formal
```

| 验证项 | 结果 |
| --- | --- |
| Release 构建 | 0 警告 0 错误 |
| Release 测试 | `1049 / 1049` 通过 |
| 渠道 / 工作区 | `formal` / `dirty=false` |
| 提交 | `64c5993e572d` |
| 代码签名 | `unsigned`（如实声明，不宣称已签名） |
| 安装器 | `LinkForge-Setup-1.1.0-64c5993e572d.exe` |
| x64 便携包 | `LinkForge-1.1.0-64c5993e572d-win-x64-portable.zip` |
| x86 便携包 | `LinkForge-1.1.0-64c5993e572d-win-x86-portable.zip` |

### 4. 产物核对

| 产物 | 字节 | SHA-256 |
| --- | ---: | --- |
| 安装器 | 96,281,443 | `37af3f6a2255012ab684eb98645591f662a001ec94c90a89491fd4f717078755` |
| x64 便携包 | 69,926,634 | `26679775d540514bb34f74a26eada740335c7a9377071f0ae94472b6cb8c16f8` |
| x86 便携包 | 65,221,476 | `52545a95ee98b6bfe9d6098a7533c2e804ff1e4f8b7132002edb926bad350621` |
| 第三方声明 | 1,072 | `02f0dd3b98833040147d01daf91243c24392a33ddced674152220b5216782c6a` |
| 发布 JSON | 1,742 | `06e7265da44ec28ce35e1bf6aaed96a26297d18b97fe7f6b0832e68fb1f6e91c` |

PE 头复核：`release-payloads\win-x64\LinkForge.App.exe` 为 `0x8664` (x64)，
`release-payloads\win-x86\LinkForge.App.exe` 为 `0x014C` (x86)。独立启动
x86 载荷并关闭，退出码 `0`。

### 5. 安装器与干净检出

| 验证项 | 结果 |
| --- | --- |
| 静默安装 | 退出码 `0` |
| 重复安装覆盖升级 | 退出码 `0` |
| 覆盖前后应用启动/关闭 | 两次退出码均为 `0` |
| 卸载 | 退出码 `0` |
| `%LOCALAPPDATA%\LinkForge` 用户数据 | 保留 |
| 干净检出 `64c5993` Release 门禁 | 通过，构建 0 警告 0 错误，`1049 / 1049` 测试通过 |

### 6. GitHub Release 发布核验

- GitHub Release `v1.1.0` 已公开发布：
  https://github.com/CiallorZ/ChinaTower-Tool/releases/tag/v1.1.0
- `isDraft=false`、`isPrerelease=false`，8 个附件全部为 `uploaded`；
- 远端安装器、双架构便携包、校验文件、第三方声明和发布 JSON 的字节数
  与本地正式产物一致；
- `v1.1.0` 标签解引用后指向 `64c5993e572dae7879baf0819fed0c14898e7f85`，
  `v1.0.0` 标签仍指向原发布提交；
- 记录正式门禁的文档提交 `ffc2a4a` 已推送，Windows CI
  [Run 40029279139](https://github.com/CiallorZ/ChinaTower-Tool/actions/runs/40029279139)
  通过。

### 7. 仍未完成

- Windows 10 x64 真机安装、升级和卸载；
- 32 位 Windows 上的安装器架构选择流程；
- 高 DPI 多显示器、完整键盘焦点与可访问性验收；
- 代码签名、时间戳、SmartScreen 信誉、自动更新和升级失败回滚；
- 厂商 SDK、抓包、安全扫描和渗透攻防（D-09 / D-10）。

## V1.2 安装器失败升级恢复（LF-018，2026-10-24）

### 1. 真实缺陷

审计发现 V1.0/V1.1 安装器只验证了“重复安装升级成功”，没有验证升级失败。
在临时安装目录锁定 `LinkForge.App.dll` 后执行覆盖安装，Inno 回滚会删除旧
程序目录，只留下测试标记；应用随后无法启动。这不是证书、SmartScreen 或
Windows 10 环境问题，而是发布工程缺口。

### 2. 修复

- `PrepareToInstall` 在已有安装时把整个 `{app}` 复制到
  `%LOCALAPPDATA%\LinkForge\upgrade-backup`；备份失败阻止安装。
- `DeinitializeSetup` 在安装未成功时恢复完整程序目录；恢复不完整保留备份
  并记录安装日志。
- `CurStepChanged(ssPostInstall)` 只在安装成功后标记完成；成功升级删除备份。
- `verify-installer.ps1` 新增真实失败注入，要求非零退出、旧 EXE/DLL 哈希、
  安装标记、卸载器和开始菜单入口保留，恢复后应用退出码 `0`。
- 新增 `verify-installer-upgrade-failure.ps1`，用于验证从旧正式安装器升级到
  候选安装器时的失败恢复。

### 3. 提交前候选验证

| 验证项 | 结果 |
| --- | --- |
| Debug 构建 | 通过，0 警告 0 错误 |
| 全解决方案测试 | **1049 / 1049** 通过 |
| 应用真实启动/关闭 | 退出码 `0` |
| 失败升级退出码 | `7`（非零，符合失败注入预期） |
| 失败后旧 EXE/DLL 哈希 | 保持不变 |
| 失败后安装标记/卸载器/开始菜单入口 | 保留 |
| 恢复后应用启动/关闭 | 退出码 `0` |
| 正常升级 | 退出码 `0` |
| 正常升级后临时备份 | 已删除 |
| 卸载 | 退出码 `0`，用户数据保留 |
| 从 V1.0.0 正式安装器升级失败注入 | 退出码 `7`，旧 EXE/DLL/标记/卸载器/快捷方式保留 |
| 跨版本恢复后应用启动 | 退出码 `0` |
| 跨版本失败验证后的清理 | 卸载退出码 `0`，开始菜单测试目录无残留 |

提交前候选安装器为
`artifacts/test-installer-1.2/LinkForge-Setup-1.2.0-16a64e0961fd.exe`，
它不是正式发布物。以下记录从提交 `5486fbe` 重新执行正式门禁后的结果。

### 4. 边界

- 失败恢复不是系统级事务；磁盘损坏、ACL 撤销或长期占用仍可能使恢复不完整。
- 当前安装器和便携包仍未签名；无时间戳、SmartScreen 信誉或自动更新。
- Windows 10 真机、32 位 Windows 安装流程、高 DPI 与完整可访问性未验收。
- 厂商 SDK、抓包、安全扫描与渗透/攻防仍受 D-09 / D-10 约束。

## V1.2.0 正式发布门禁与 GitHub Release（2026-10-24）

### 1. 提交、CI 与构建

| 项目 | 结果 |
| --- | --- |
| 功能提交 | `5486fbee8c52205f0a004e8a872ae0e17a886704` |
| 标签 | `v1.2.0`，不移动 `v1.0.0` / `v1.1.0` |
| Windows CI | [Run 40032659744](https://github.com/CiallorZ/ChinaTower-Tool/actions/runs/40032659744) 通过，4m30s |
| 正式 Release 构建 | 0 警告、0 错误 |
| 正式 Release 测试 | **1049 / 1049** 通过 |
| 发布渠道 / 工作区 | `formal` / `dirty=false` |
| 干净检出 `5486fbe` | Release 构建通过，`1049 / 1049` 测试通过 |

正式门禁命令：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\build-release.ps1 -Formal
pwsh -NoProfile -File scripts\verify-clean-checkout.ps1 -Configuration Release
```

### 2. 正式产物

| 产物 | 字节数 | SHA-256 |
| --- | ---: | --- |
| `LinkForge-Setup-1.2.0-5486fbee8c52.exe` | 96,270,699 | `61288ba44b55c54f2cbe2cd1a54acd413276b5fd7ea22261fbfd5ad313b271c8` |
| `LinkForge-1.2.0-5486fbee8c52-win-x64-portable.zip` | 69,926,494 | `a04d41589ccc71b203f1fb2af0c160cf2b1682dddd2876d042b08c572215e3eb` |
| `LinkForge-1.2.0-5486fbee8c52-win-x86-portable.zip` | 65,221,345 | `4d050a5dd1e42bc24156847e2a063e22977a4358ae19dd024e52e76d096a347f` |
| `LinkForge-Release-1.2.0-5486fbee8c52.json` | 1,742 | `208e17704117152f88a34aa373ebf15443279bde86641d3daf273006f59366a8` |
| `LinkForge-Setup-THIRD-PARTY-NOTICES.txt` | 1,072 | `02f0dd3b98833040147d01daf91243c24392a33ddced674152220b5216782c6a` |

安装器签名状态为 `NotSigned`。发布清单中的 `channel=formal`、
`dirty=false`、`commit=5486fbee8c52`、`codeSigning=unsigned`。x64/x86 便携包
均含 `release.json`、`VERSION.txt`、`DEPENDENCIES.txt`、
`THIRD-PARTY-NOTICES.md` 和 `sbom.cdx.json`。

### 3. 最终安装与运行验收

| 验证项 | 结果 |
| --- | --- |
| 正式安装器静默安装 | 退出码 `0` |
| 首次应用启动/关闭 | 退出码 `0` |
| 锁定 DLL 的失败升级 | 退出码 `7`，旧 EXE/DLL/标记保留 |
| 失败后卸载器/开始菜单入口 | 保留 |
| 恢复后应用启动/关闭 | 退出码 `0` |
| 正常覆盖升级 / 升级后启动 | `0` / `0` |
| 成功升级后的临时备份 | 删除 |
| 卸载 / 用户数据 | 退出码 `0` / 保留 |
| 从 `v1.0.0` 安装器跨版本失败注入 | 退出码 `7`，恢复应用退出码 `0` |
| x64 与 x86 便携载荷 | 均启动/关闭，退出码 `0` |
| PE 文件版本 | 两者均为 `1.2.0.0` |
| 测试后开始菜单目录残留 | 无 |

### 4. GitHub Release 公开核验

- 正式 Release：
  https://github.com/CiallorZ/ChinaTower-Tool/releases/tag/v1.2.0
- `isDraft=false`、`isPrerelease=false`，标签 `v1.2.0` 解析到提交
  `5486fbee8c52205f0a004e8a872ae0e17a886704`。
- 8 个附件全部为 `uploaded`：安装器、安装器 SHA-256、x64 便携包、x64
  SHA-256、x86 便携包、x86 SHA-256、第三方声明、发布 JSON。
- GitHub 远端附件大小与本地一致；正式 exe、两个便携 ZIP、发布 JSON 和第三方
  声明的远端 SHA-256 摘要与本地计算结果逐项一致。

## V1.2 后启动与数据库恢复修复（LF-019，2026-10-24）

### 1. 真实运行故障

- `src\LinkForge.App\bin\Debug\net10.0-windows\LinkForge.App.exe` 是框架依赖
  apphost，默认查找系统级 .NET。本机 SDK 位于 `%USERPROFILE%\.dotnet`，
  直接双击会提示缺少运行时。
- 本机历史 `linkforge.db` 为 0 字节；启动流程先由工作台查询项目表，后执行
  SQLite 迁移，导致 `no such table`。后台初始化任务未被观察，用户只看到
  页面不可用或缺表异常。
- 启动失败没有统一的 UI Dispatcher / 未观察任务异常兜底，错误无法稳定转成
  可读提示或非零退出码。

### 2. 修复内容

- 新增仓库根目录 `Start-LinkForge.cmd`；`scripts/run.ps1` 仅为当前进程设置
  `DOTNET_ROOT` 和 `PATH`，新增 `-BuildIfMissing`，并透传应用退出码。
- 启动顺序改为：日志读取 → SQLite 初始化/迁移 → 工作台加载；生命周期使用
  可取消的 `CancellationTokenSource` 管理。
- 增加 Dispatcher 与未观察任务异常兜底；启动关键错误显示脱敏、可读的提示，
  无法继续启动时返回非零退出码。数据库初始化失败时保留应用壳层并明确提示
  依赖数据库的功能暂不可用，不伪造加载成功。
- `README.md` 明确源码运行时不要直接双击 Debug apphost，安装版与便携包不受
  该问题影响。

### 3. 验证结果

| 验证项 | 结果 |
| --- | --- |
| 清理缓存后 Restore | 成功 |
| Debug 构建 | 0 警告、0 错误 |
| 全解决方案测试 | **1053 / 1053** 通过 |
| `Start-LinkForge.cmd` 启动/关闭 | 退出码 `0` |
| `scripts\run.ps1 -BuildIfMissing` 启动/关闭 | 退出码 `0` |
| Debug apphost + 显式 `DOTNET_ROOT` | 退出码 `0` |
| 启动前数据库 | 0 字节 |
| 启动后数据库 | 61,440 字节，SQLite 文件头有效 |

### 4. 缓存与临时文件清理

- `artifacts` 仅保留 V1.0/V1.1/V1.2 正式产物与校验文件共 22 个；删除
  解压后的便携目录、`release-payloads` 和候选安装器测试目录。
- 清理 `E:\AIWork\temp` 下 788 个 LinkForge 临时项、仓库内 52 个可重建
  `bin/obj` 目录、Codex `cache/.tmp`、崩溃转储与 NuGet 包缓存。
- Magpie 缓存除正在运行的 `bun` 进程文件外均已清理；未终止用户进程。
- 未删除用户数据库、项目、日志、凭据、Codex sessions/memories/plugins/
  attachments/visualizations，也未删除正式发布资产。

### 5. 边界

- 本次是 `v1.2.0` 发布后的源码启动与数据恢复修复，不替换、不重写既有
  `v1.0.0` / `v1.1.0` / `v1.2.0` 标签和已发布附件。
- 安装器与便携包仍未签名；Windows 10 真机、高 DPI、可访问性、厂商 SDK、
  抓包、安全扫描与渗透攻防边界仍未完成。

## 阶段 06 · 安全中心（LF-020，2026-10-24）

### 1. 实现范围

- 新增 `LinkForge.Adapters.Security`，只执行单目标、显式端口 TCP 暴露面与
  TLS/证书基线检查。
- 支持最多 32 个 TCP 端口、8 个 TLS 端口，TLS 必须是 TCP 子集；
  并发 1–4，单项超时 1–60 秒，检查间隔 0–2000 毫秒。
- TLS 使用 `SslStream` 与系统默认证书链/主机名校验，启用 TLS 1.2/1.3；
  没有关闭校验的配置或 UI 开关。
- 页面覆盖参数校验、外部目标授权确认、运行/取消、开放/关闭/过滤/未知、
  证书有效/拒绝/握手失败/超时、复制脱敏摘要、JSON 导出、原始日志与空状态。
- 导航“安全中心”由占位改为真实页面；DI 注册安全探测边界、适配器、
  导出器和视图模型。

### 2. 验证结果

| 验证项 | 结果 |
| --- | --- |
| Debug 构建 | 0 警告、0 错误 |
| 全解决方案测试 | **1110 / 1110** 通过 |
| 安全适配器测试 | 25 / 25 通过 |
| App 测试 | 651 / 651 通过 |
| 真实启动/关闭 | 退出码 `0` |
| 1366×768 页面布局 | 通过 |
| 浅/深色截图 | 两主题均生成，像素差异测试通过 |

截图：

- `screenshots/stage06-security-light.png`
- `screenshots/stage06-security-dark.png`

### 3. 状态与安全语义

- TCP 开放只表示连接成功；关闭、过滤/无响应与未知分别记录。
- 过滤或无响应不能证明主机离线；端口开放不等于漏洞。
- 证书告警不等于已确认可利用漏洞，必须保留为待核验事实。
- 用户取消保持 `Cancelled`，未执行项保持 `Skipped`，不会计为失败。
- 外部目标未勾选授权确认时不会启动探测；页面始终显示目标、TCP/TLS 端口
  和最大范围。

### 4. 边界

- 本次交付不是漏洞扫描器、资产测绘器或渗透测试平台。
- 不实现漏洞利用、口令爆破、隐蔽/规避扫描、网段扫描或破坏性测试。
- 不实现抓包、端口全量扫描、指纹识别、漏洞库匹配或自动化攻击链。
- D-10 安全扫描/攻防边界仍未正式关闭；Windows 10 真机、高 DPI、
  键盘焦点与可访问性最终验收仍待后续阶段。
