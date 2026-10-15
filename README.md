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
| 解决方案骨架（6 个项目） | 已建立，构建通过 |
| 设计令牌 / 基础组件 / 应用壳层 / 业务页面 | **未开始** |

> UI 未开始编码是刻意安排：按 UI 规范 §11 阶段 A 与 `docs/CODEX_TASKS.md` 约束，先完成勘察、差异识别与实施计划，再进入设计令牌。

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
| `docs/DEV_STANDARDS.md` | 编码、测试、安全、提交流程规范 |
| `docs/ROADMAP.md` | 里程碑、任务映射与验收口径 |

## 环境要求（目标）

- Windows 10 19045 / Windows 11 x64（本机实测：Windows 11 10.0.26300）
- .NET SDK 10.0.402（已安装到用户级 `%USERPROFILE%\.dotnet`，未修改系统 PATH）
- 可选：Docker（本机已装 29.8.1，用于 MQTT/HTTP 测试容器）
- 可选：FFmpeg（本机已装 9.0.2，**仅作开发期诊断参考，不随产品分发**，原因见 ADR-007）

## 下一步

1. 确认 `docs/REVIEW_V1.0.md` 第 6 节待决事项，优先 D-01（是否 `git init`）与 D-03（默认主题）。
2. 进入阶段 01：设计令牌（浅/深色资源字典 + 令牌完整性测试）。
3. 每阶段结束运行 `dotnet build` / `dotnet test` 并报告真实结果。
