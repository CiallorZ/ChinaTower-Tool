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
