# LinkForge V1.2 发布与验收

日期：2026-10-24
版本：`1.2.0`
渠道：发布候选（正式门禁完成后回填提交、产物与 Release 链接）
历史版本：`v1.0.0`、`v1.1.0` 标签均保持不动

## 1. 本版目标

V1.2 不扩展协议或安全工具范围，只修复发布工程中的真实缺口：

- 覆盖安装失败时，原安装器在 Inno 回滚后可能只剩非程序文件，旧程序目录
  无法继续运行。
- V1.2 在升级前复制完整程序目录到
  `%LOCALAPPDATA%\LinkForge\upgrade-backup`。
- 安装未成功时恢复旧 EXE、DLL、卸载器、快捷方式及安装目录内其他文件；
  恢复不完整时保留备份并写入安装日志。
- 成功升级后删除临时备份；普通卸载仍保留
  `%LOCALAPPDATA%\LinkForge` 用户项目、日志、报告与凭据文件。

这不是系统级事务安装，也不能保证被其他进程长期占用、磁盘损坏或权限被撤销时
一定恢复成功。恢复失败必须保留备份和日志，不得声称成功。

## 2. 验证入口

候选安装器：

```powershell
pwsh -NoProfile -File scripts/verify-installer.ps1 `
  -InstallerPath artifacts\LinkForge-Setup-1.2.0-<commit>.exe
```

从旧正式安装器升级到候选安装器的独立失败注入：

```powershell
pwsh -NoProfile -File scripts/verify-installer-upgrade-failure.ps1 `
  -InstallerPath artifacts\LinkForge-Setup-1.2.0-<commit>.exe `
  -BaseInstallerPath artifacts\LinkForge-Setup-1.0.0-879de49f4640.exe
```

`verify-installer.ps1` 必须在隔离目录完成：

1. 静默安装并启动/关闭应用。
2. 记录 EXE/DLL 哈希并写入安装目录标记。
3. 以 `FileShare.None` 锁定 `LinkForge.App.dll`。
4. 使用 `/NOCLOSEAPPLICATIONS` 执行升级，要求安装器返回非零。
5. 要求旧 EXE/DLL、安装目录标记、卸载器与开始菜单快捷方式仍存在。
6. 恢复后应用启动/关闭退出码必须为 `0`。
7. 再执行正常升级，要求退出码 `0` 且备份目录已删除。
8. 卸载成功，程序与快捷方式删除，用户数据保留。

候选安装器已完成的失败注入结果：

```text
InstallExitCode               : 0
FailedUpgradeExitCode         : 7
UpgradeExitCode               : 0
FirstApplicationExitCode      : 0
RecoveredApplicationExitCode  : 0
SecondApplicationExitCode     : 0
UninstallExitCode             : 0
PreviousInstallationPreserved : True
UpgradeBackupRemoved          : True
UserDataPreserved             : True
```

同一候选安装器还完成了从 `v1.0.0` 正式安装器到 `v1.2.0` 候选安装器的
独立失败注入：升级退出码 `7`，旧 EXE/DLL、安装标记、卸载器和开始菜单
快捷方式全部保留，恢复后应用启动/关闭退出码 `0`，最终卸载退出码 `0`。

该结果是候选安装器的本地真实执行记录，不替代正式提交上的最终门禁。

## 3. 正式发布流程

正式产物必须从干净提交生成：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File scripts\build-release.ps1 -Formal
```

正式模式要求：

- `Directory.Build.props` 中 `VersionSuffix` 为空；
- Git 工作区干净；
- Release 全量构建与测试门禁成功；
- 安装器、应用程序、发布清单中的版本和提交一致；
- 生成 `win-x64` / `win-x86` 自包含便携包与单一用户级安装器；
- 生成每个产物的 SHA-256、第三方声明与发布 JSON。

正式产物形成后再执行安装器验收、干净检出验证、Windows CI、标签与 GitHub
Release 发布核验。产物大小、提交号、CI、标签和 Release 附件结果必须在正式
门禁完成后回填；在此之前状态只能写“候选”。

## 4. 发布产物

预期产物：

- `LinkForge-Setup-1.2.0-<commit>.exe`
- `LinkForge-Setup-1.2.0-<commit>.exe.sha256`
- `LinkForge-1.2.0-<commit>-win-x64-portable.zip`
- 对应 `.sha256`
- `LinkForge-1.2.0-<commit>-win-x86-portable.zip`
- 对应 `.sha256`
- `LinkForge-Setup-THIRD-PARTY-NOTICES.txt`
- `LinkForge-Release-1.2.0-<commit>.json`

便携包内含 `release.json`、`VERSION.txt`、依赖/许可证清单、CycloneDX 1.5
SBOM、`SHA256SUMS.txt` 和 ZIP SHA-256。依赖清单只反映 NuGet nuspec 声明，
不替代法律审查。

## 5. 安装与卸载

- 默认目录：`%LOCALAPPDATA%\Programs\LinkForge`
- 权限：`PrivilegesRequired=lowest`、`asInvoker`，不需要管理员权限
- 快捷方式：开始菜单；桌面快捷方式为可选
- 升级失败：恢复旧程序目录；恢复不完整时保留
  `%LOCALAPPDATA%\LinkForge\upgrade-backup`
- 成功升级：删除临时升级备份
- 卸载：删除程序文件、开始菜单/桌面快捷方式和卸载入口
- 用户数据：`%LOCALAPPDATA%\LinkForge` 保留，不随卸载删除
- 语言：简体中文 + 英文；中文语言包固定进仓库并附 MIT 声明

## 6. 安全与范围边界

- 安装器和便携包**未签名**，SmartScreen 可能显示未知发布者警告。
- 无 Authenticode 代码签名、可信时间戳、SmartScreen 信誉、自动更新、
  版本降级阻止或系统级事务安装。
- 未在 Windows 10 真机、32 位 Windows 安装流程、高 DPI 多显示器和完整
  键盘/辅助功能组合上完成最终验收。
- 厂商 SDK（D-09）、抓包、安全扫描与渗透/攻防（D-10）未接入。
- V1.2 只证明安装器失败升级的程序目录恢复链路，不证明任意磁盘、ACL、
  杀毒软件或系统策略故障都能恢复。

## 7. 历史版本

- V1.0.0：https://github.com/CiallorZ/ChinaTower-Tool/releases/tag/v1.0.0
- V1.1.0：https://github.com/CiallorZ/ChinaTower-Tool/releases/tag/v1.1.0

历史标签不得移动或重写；各版本原始产物核对记录保留在 Git 历史、
`docs/BUILD_LOG.md` 与对应 GitHub Release 中。
