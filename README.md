# LinkForge（视联网全链路调试工具箱）

面向视频监控、视联网、物联网与平台集成工程师的 Windows 桌面工程工作台：把设备资产、协议调试、网络诊断、API 联调、日志证据与报告输出整合为一套**可复现、可解释、可复用**的工程流程。

产品不以"工具数量"为目标，而以"可复现诊断流程 + 可解释证据 + 可复用项目配置"为核心价值。

## 当前状态

| 项目 | 状态 |
| --- | --- |
| 规划基线（PRD / 架构 / 功能树 / 线框 / 任务拆解） | 已归档到 `docs/` |
| 开工前评审（差异、风险、待决项） | 已完成，见 `docs/REVIEW_V1.0.md` |
| 技术选型决策（ADR） | 已起草，见 `docs/DECISIONS.md` |
| 开发步骤规范 | 已起草，见 `docs/DEV_STANDARDS.md` |
| UI 强约束规范 | 已归档，见 `docs/LinkForge_UI_Design_Guidelines_V1.1.md` |
| UI 分阶段实施计划 | 已起草，见 `docs/UI_IMPLEMENTATION_PLAN.md` |
| 解决方案骨架与业务模块 | 13 个源码项目、12 个测试项目，Debug/Release 构建均通过 |
| 设计令牌 / 基础组件 / 应用壳层 | 已完成 |
| 项目资产、网络、HTTP、MQTT、RTSP、ONVIF、日志、报告、开发者工具、插件页 | 已完成真实实现与本地验证 |
| 正式发布工程（LF-013） | **V1.0**：未签名双架构安装器、`win-x64`/`win-x86` 自包含便携包、依赖清单、SBOM、SHA-256 与干净构建脚本 |
| 版本与签名信息 | **已实现**：真实读取程序集、系统、架构、安装形态与本机 Authenticode 信任状态 |

> 当前版本为 `1.0.0`。安装器与便携包均**未签名**，会触发 SmartScreen；
> 安装器是 Inno Setup 用户级安装包，不是 MSIX/WiX，也没有自动更新或失败回滚。
> 本机已实测 Windows 11、x64 安装链路与 x86 自包含载荷；Windows 10 真机、
> 高 DPI/可访问性最终验收、厂商 SDK 与抓包/安全扫描边界仍未完成，详见
> `docs/RELEASE.md`。

## 文档索引

| 文档 | 用途 |
| --- | --- |
| `docs/PRD.md` | 产品需求规格说明书 V1.0 |
| `docs/FUNCTION_TREE.md` | 功能树与 MVP 边界 |
| `docs/ARCHITECTURE.md` | Windows 技术架构、分层、模型与安全设计 |
| `docs/UI_WIREFRAMES.md` | 核心页面线框与交互规划 |
| `docs/CODEX_TASKS.md` | 分阶段开发任务、提示词模板与 DoD |
| `docs/REVIEW_V1.0.md` | 开工前评审：环境事实、文档差异、风险、待决项 |
| `docs/DECISIONS.md` | 技术决策记录（ADR）与许可证登记 |
| `docs/DEV_STANDARDS.md` | 编码、测试、安全、提交流程规范 |
| `docs/ROADMAP.md` | 里程碑、任务映射与验收口径 |
| `docs/BUILD_LOG.md` | 实际执行的构建/测试命令与真实结果 |
| `docs/PRD_DELTA.md` | 建议纳入 PRD V1.1 的修订条目 |
| `docs/LinkForge_UI_Design_Guidelines_V1.1.md` | UI 开发强约束规范 |
| `docs/CODEX_START_PROMPT.md` | 启动提示词存档 |
| `docs/UI_IMPLEMENTATION_PLAN.md` | 六个阶段的分阶段实施计划 |
| `docs/RELEASE.md` | 正式发布物、安装/卸载、版本、依赖清单、SBOM、签名与未完成项 |
| `CHANGELOG.md` | V1.0 与历史预览版本变更摘要 |

## 环境要求（目标）

- Windows 10 19045 / Windows 11 x64（本机实测：Windows 11 10.0.26300）
- .NET SDK 10.0.402（已安装到用户级 `%USERPROFILE%\.dotnet`，未修改系统 PATH）
- 可选：Docker（本机已装 29.8.1，用于 MQTT/HTTP 测试容器）
- 可选：FFmpeg（本机已装 9.0.2，**仅作开发期诊断参考，不随产品分发**，原因见 ADR-007）

## 下一步

1. 在 Windows 10 x64 真机执行安装、启动、升级替换与卸载残留验收。
2. 评估代码签名证书、时间戳、自动更新与升级失败回滚。
3. 关闭 D-09（海康/大华 SDK 许可）与 D-10（抓包/安全扫描授权边界）。

发布命令：

```powershell
pwsh -NoProfile -File scripts/build-release.ps1 -Formal
pwsh -NoProfile -File scripts/verify-clean-checkout.ps1
```
