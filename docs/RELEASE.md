# LinkForge 发布与验收

日期：2026-10-22
适用范围：LF-013 的发布工程增量。

## 当前发布方式

MVP 采用**免安装便携 ZIP**，不写成安装器，也不声称已经完成安装/卸载验收。

```powershell
pwsh -NoProfile -File scripts/publish.ps1
```

默认行为：

- 完整执行 Release 构建与全量测试；
- 发布 `win-x64` 自包含包；
- 输出到 `artifacts/LinkForge-<版本>-<commit>-win-x64-portable/`；
- 同时生成 ZIP 与 ZIP 的 SHA-256；
- 包内生成 `release.json`、`VERSION.txt`、`DEPENDENCIES.txt`、
  `THIRD-PARTY-NOTICES.md`、`sbom.cdx.json` 和 `SHA256SUMS.txt`。

工作区不干净时脚本默认拒绝打包。仅用于本地诊断时可显式使用 `-AllowDirty`，
产物会带 `.dirty` 标记。

`-SkipTests` 只用于快速检查发布流程，不得作为正式交付或阶段验收证据。

## 版本来源

版本号由根目录 `Directory.Build.props` 统一维护：

- `VersionPrefix`：产品版本；
- `VersionSuffix`：预发布通道；
- 程序集信息版本自动附带 Git 短提交号；
- 发布脚本在 `release.json` 与文件名中记录提交号。

当前版本为 `0.5.0-preview.1`，不是 V1.0 正式版。

## 依赖与许可证

发布脚本从发布后的 `project.assets.json` 读取全部 NuGet 包（含传递依赖），
从本机 NuGet 缓存的 `.nuspec` 提取声明的许可证表达式，生成：

- `DEPENDENCIES.txt`：机器可读的扁平清单；
- `THIRD-PARTY-NOTICES.md`：人工核对清单；
- `sbom.cdx.json`：CycloneDX 1.5 JSON SBOM；
- `SHA256SUMS.txt`：包内所有文件的 SHA-256。

“声明许可证”不等于已完成法律审查。正式对外发布前仍需逐项复核完整许可证文本、
再分发义务和 NOTICE 要求。

## 干净检出验证

```powershell
pwsh -NoProfile -File scripts/verify-clean-checkout.ps1
```

脚本将当前已提交的 `HEAD` 克隆到临时目录，确认 checkout 干净后重新执行
还原、构建和测试，避免本机 `bin/obj` 或未跟踪文件掩盖缺失引用。

## 2026-10-22 本地验证记录

- Debug 与 Release 构建均为 0 警告、0 错误；
- 全解决方案测试 937 / 937 通过；
- `-AllowDirty -SkipTests` 诊断包成功生成；
- NuGet 依赖清单包含 25 个组件，许可证声明可解析；
- CycloneDX 1.5 SBOM 可解析，序列号由版本与提交确定；
- 包内逐文件 SHA-256 复核 0 失败；
- 发布目录中的 `LinkForge.App.exe` 真实启动并关闭，退出码 0；
- 干净 checkout 验证已针对提交 `3bd8af769ad1` 执行并通过；
  克隆后的还原、构建与 937 / 937 测试均成功。

最终干净版便携包为
`artifacts/LinkForge-0.5.0-preview.1-3bd8af769ad1-win-x64-portable.zip`，
ZIP SHA-256 为
`9c542574a73f899b957e5694e6c2a15aaeb8a4f4ba0d4df9c1d6376007272c8e`。

## 升级与卸载（便携包）

- 升级：退出应用后，用新便携包替换程序目录；用户数据位于
  `%LOCALAPPDATA%\LinkForge`，不会因替换程序目录自动删除。
- 卸载：删除便携包目录。用户数据、日志、数据库与报告需要用户另行决定是否清理。
- 当前没有 MSIX/WiX/Inno Setup 安装器，也没有升级失败自动回滚。

## 签名状态

当前便携包**未签名**。没有证书、时间戳服务与发布者信任配置，
因此不得在界面、文档或交付说明中标注“已签名”或“已验证发布者”。

## 尚未完成

- Windows 10 真机安装/升级/卸载验收；
- x86 运行时验证（D-08）；
- 代码签名证书、时间戳与 SmartScreen 验证；
- MSIX/WiX/Inno Setup 安装器及卸载残留验证；
- 高 DPI、键盘焦点与可访问性最终验收；
- 厂商 SDK（D-09）与抓包/安全扫描/渗透攻防边界（D-10）。
