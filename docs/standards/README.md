# 本项目采用的开发规范

状态：现行 · 2026-11-02

本仓库遵循[《工程开发手册》v1.1](engineering-handbook.html)（通用手册，所有项目共用；手册页面在本目录随仓库保存，供开发者和 AI 代理离线阅读）。手册之外只记录本项目的选择和偏差；与手册冲突时以本页和 [ADR](decisions/README.md) 为准。

## 采用范围

| 项目 | 选择 |
|---|---|
| 领域规范 | A（MCP 服务）、C（工程软件自动化：TIA Portal Openness）、D（桌面应用：工作台） |
| 档位 | M（正式产品）；审批、写入安全和防止项目数据损坏的规则按 L 执行 |
| 必读 | 手册的最小必做清单、黄金路径、任务手册、快速交付；改动涉及的章节 |
| 任务说明 | 按手册模板 T.4（实现代理任务说明），并加上本仓库 [AGENTS.md](../../AGENTS.md) 的规则 |
| 完成定义 | 手册第 23 节，加上本仓库的生成器 `-Check`、契约与响应基线和实机清单要求 |

## 项目自己的取舍（偏差登记）

| 主题 | 本项目的做法 | 依据 |
|---|---|---|
| 支持的版本 | TIA Portal V14 SP1、V15.1、V16–V21 八个版本，功能按各版本官方 API 注册 | [ADR 0022](decisions/0022-engine-retirement-and-eight-release-unification.md) |
| 合并检查目标 | 本机 `check-all -Affected`，目标与手册一致（约 15–20 分钟）；夜间全量检查门槛 45 分钟 | [ADR 0024](decisions/0024-ci-does-not-block-merges.md)、[ADR 0025](decisions/0025-release-evidence-nightly-and-verified-build-units.md) |
| 工作台视觉 | EplanMcp 工作台设计系统；表格行高 36；审批按钮等权重、拒绝在左、回车不批准、拒绝后不撤销 | [ADR 0028](decisions/0028-workbench-visual-design-eplanmcp.md) |
| 用语 | 界面和文档用“服务”“项目”（TIA 的 project） | [ADR 0026](decisions/0026-documentation-structure-and-language.md) |

## 本目录的其他文档

- [安全标准与威胁模型](security.md)
- [测试策略](development.md)
- [MCP 工具设计规范](design.md)

这些文档按手册的对应章节和领域规范 A、C 细化到本项目；后续按手册结构合并或精简。
