# LinkForge V1.0 发布与验收

日期：2026-10-24
版本：`1.0.0`
渠道：正式版
最终提交：`879de49f4640c1f0c6455636e45f01d578d2319b`
Git 标签：`v1.0.0`

## 1. 发布物

正式发布由 `scripts/build-release.ps1 -Formal` 从干净提交生成：

- `LinkForge-1.0.0-879de49f4640-win-x64-portable.zip`
- `LinkForge-1.0.0-879de49f4640-win-x86-portable.zip`
- `LinkForge-Setup-1.0.0-879de49f4640.exe`
- 安装器 SHA-256 文件
- `LinkForge-Setup-THIRD-PARTY-NOTICES.txt`
- `LinkForge-Release-1.0.0-879de49f4640.json`

| 产物 | 字节数 | SHA-256 |
| --- | ---: | --- |
| 安装器 | 96,228,385 | `9ac224a6ad4454f507ad030ffc5606bdf88dca0513e6a972e50a5cc13fd70ff1` |
| `win-x64` 便携包 | 69,884,361 | `0b16fa1d6e985b3bc63f7c805458e6d98208ec43b7ac04d95036f3e7cec853f6` |
| `win-x86` 便携包 | 65,179,221 | `17dec419103c03f529287812ac1a5bc55648617eb33f3cc3fac8d457c9a20ef7` |

便携包内含 `release.json`、`VERSION.txt`、依赖/许可证清单、CycloneDX 1.5
SBOM、`SHA256SUMS.txt` 和 ZIP SHA-256。正式模式要求：

- `Directory.Build.props` 中 `VersionSuffix` 为空；
- 工作区干净；
- Release 全量构建和测试门禁成功；
- 安装器与应用程序版本、提交号一致。

```powershell
pwsh -NoProfile -File scripts/build-release.ps1 -Formal
```

## 2. 安装器

V1.0 使用 Inno Setup 6 构建单个用户级安装器，同时嵌入 `win-x64` 与
`win-x86` 自包含载荷，由安装时的 `IsWin64` 选择匹配架构。

- 默认目录：`%LOCALAPPDATA%\Programs\LinkForge`
- 权限：`PrivilegesRequired=lowest`、`asInvoker`，不需要管理员权限
- 快捷方式：开始菜单；桌面快捷方式为可选
- 卸载：删除程序文件、开始菜单/桌面快捷方式和卸载入口
- 用户数据：`%LOCALAPPDATA%\LinkForge` 保留，不随卸载删除
- 升级：同一 AppId 重复安装覆盖程序目录；不支持自动回滚
- 语言：简体中文 + 英文；中文语言包随仓库固定，来源和 MIT 许可见
  `installer/Languages/README.md`

安装器不是 MSIX/WiX，不包含自动更新器，也不支持失败自动回滚。

## 3. 验证命令

安装器完整验收：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File scripts/verify-installer.ps1 `
  -InstallerPath artifacts/LinkForge-Setup-1.0.0-879de49f4640.exe
```

脚本验证静默安装、开始菜单入口、应用启动/关闭、重复安装升级、卸载和
`%LOCALAPPDATA%\LinkForge` 用户数据保留。若开始菜单已存在 `LinkForge`
目录，脚本会拒绝继续，避免误删既有安装快捷方式。

两个架构载荷也可单独验证：

```powershell
artifacts\release-payloads\win-x86\LinkForge.App.exe
```

干净检出验证：

```powershell
pwsh -NoProfile -File scripts/verify-clean-checkout.ps1 -Configuration Release
```

截图验收测试默认写入系统临时目录，避免正式发布门禁污染工作区。需要有意
更新仓库中的 `screenshots/` 证据图时，显式设置环境变量后运行构建：

```powershell
$env:LINKFORGE_UPDATE_SCREENSHOTS = '1'
pwsh -NoProfile -File scripts/build.ps1
Remove-Item Env:LINKFORGE_UPDATE_SCREENSHOTS
```

## 4. V1.0 验收边界

已实测：

- Windows 11 10.0.26300 x64 安装、覆盖安装、启动/关闭、卸载与用户数据保留；
- 安装器内 x64 载荷真实启动并关闭，退出码 `0`；
- 独立启动 x86 自包含载荷成功并关闭，退出码 `0`；
- PE 头检查确认 `win-x64` 载荷为 x64、`win-x86` 载荷为 x86；
- 全量测试 `1003 / 1003` 通过；
- 提交 `879de49f4640...` 的干净检出 Release 构建与 `1003 / 1003` 测试通过；
- 完整记录见 `docs/BUILD_LOG.md`。

未完成最终实测：

- Windows 10 x64 真机安装与升级；
- 32 位 Windows 10 上的安装器架构选择和运行；
- 高 DPI 多显示器、完整键盘焦点顺序与可访问性验收；
- 恶意/损坏安装包、系统策略阻断和升级失败恢复。

应用清单声明支持 Windows 10/11、PerMonitorV2、`asInvoker` 和 long path，
但这不等于上述真机/辅助功能场景已经全部验收。

## 5. 签名状态

V1.0 安装器和便携包**未签名**：

- 没有代码签名证书、时间戳服务和发布者信任配置；
- Windows SmartScreen 可能显示未知发布者或阻止下载/运行；
- About 页只读取本机 Authenticode 信任链结果，不联网验证发布者；
- 不得把“本机信任链通过”表述为软件供应链审计完成。

代码签名、时间戳、SmartScreen 信誉和自动更新属于后续版本工作。

## 6. 依赖与许可证

发布脚本从 `project.assets.json` 和 NuGet `.nuspec` 生成 NuGet 依赖与声明
许可证清单，并生成 CycloneDX 1.5 SBOM。声明许可证不等于完成法律审查。

Inno Setup 简体中文语言包采用 MIT 许可，许可证文本随安装器发布物提供。
仓库当前没有为 LinkForge 自身增加 `LICENSE` 或 EULA；安装器不伪造许可协议。
正式对外再分发前，产品所有者仍需明确 LinkForge 自身授权策略并完成依赖
许可证复核。

## 7. 已知功能边界

- 海康/大华开放平台 SDK、GB/T 28181、RTP 接收、媒体解码、首帧和持续播放未交付；
- 抓包、端口/安全扫描和渗透攻防工具未交付，授权边界仍未确认；
- MQTT 遗嘱、保留消息管理、Topic 树和自动重连未交付；
- RTSP 当前只做 DNS/TCP/OPTIONS/DESCRIBE/SDP 与 Basic 认证；
- ONVIF Stream URI 可用不等于媒体已经播放；
- 日志文件不加密，未实现轮转保留、归档和签名；
- 便携包和安装器都不是 bit-for-bit 可复现；只能说干净 checkout 可重复构建和验证。
