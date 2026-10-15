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
