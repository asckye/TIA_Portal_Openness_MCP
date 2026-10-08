# 与官方能力的差距复查（2026-10-08）

本页记录两项只读复查的结论，作为 4.0 之后的规划依据；4.0 发布范围不变。工具数取自
`manifest/contracts/v4/baseline/<发布键>.json`（复查时 master：V14 SP1–V19 为 62/63/65/65/65/67，V20/V21 为 480/491；
阶段 7 合入后 V20/V21 为 487/498，lite 各 73）。API 存在于官方 XML 不等于许可证、可选组件或行为可用；
有工具也不等于已在真机验证。

## 一、对照西门子官方 Openness API

依据：八版官方 PublicAPI XML（本地 `sdk/`，不随仓库分发），按 `scripts/diagnostics/Audit-VersionTools.py` 的口径过滤，
与 [Openness 覆盖](../reference/openness-coverage.md)、[机器可读功能证据](../../reference/version-feature-matrix.json)一致。

| 发布键 | 官方领域成员（方法） | 工具数 | 编译后直接引用的方法 |
|---|---:|---:|---:|
| 14sp1 | 825（246） | 62 | 23 |
| 15.1 | 1161（351） | 63 | 23 |
| 16 | 1420（467） | 65 | 31 |
| 17 | 1719（579） | 65 | 36 |
| 18 | 2113（705） | 65 | 36 |
| 19 | 3586（922） | 67 | 37 |
| 20 | 3860（999） | 480 | 513 |
| 21 | 4323（1078） | 491 | 558 |

直接引用数不是覆盖率：V20/V21 的反射调用不计入，旧版又计入了未暴露为工具的 Studio 适配器代码。

结论：

- V14 SP1–V19 只覆盖“PLC 软件离线工程”子集（浏览、XML 交换、外部源、编译、变量与常量；16+ 监控表与工艺对象导出；
  V19 硬件目录与设备创建）。官方约 30 个能力族中约 25 个没有工具；已有工具的成功路径已在真机通过。
- V20/V21 几乎每个能力族都有工具入口，缺口在深度；4.0 的原生验收以阶段 7 的 V20/V21 验收为准。

按用户价值排序的旧版缺口（官方 API 已有，我们没有）：

1. 在线、下载、上传与在线比较（15.1+）。
2. 硬件与网络组态：设备、模块、地址、子网、IO 系统、拓扑（14sp1–18 已有设备创建 API）。
3. 库（全版本）与经典 HMI（全版本）。
4. 工程另存、归档、恢复、升级、语言与项目文本（15.1+）。
5. 监控表导入（15.1+）、工艺对象导入（16+）、PLC 对象删除、分组与保护。
6. 版本控制接口 VCI（16–19，Studio 适配器已有实现）。
7. 其余能力族：软件单元、PLC 报警/ProDiag、OPC UA、Safety、DCC/CFC、Startdrive、AML、Teamcenter、
   测试套件、用户管理、证书与保护、SiVArc。

V20/V21 的深度缺口：Multiuser 新建本地会话与对象标记；强制表导入导出（所有版本只读）；通信连接参数修改（V21 只能增删）；
在线设置/重置 PLC 主密钥；PLC 原生交叉引用的稳定性；Startdrive `p2051[0]` 读取崩溃。

## 二、对照西门子官方 AI / MCP 产品

- 西门子没有发布官方的 TIA Portal / Openness MCP 服务器（GitHub siemens 组织搜索 MCP 为 0）。
- 官方 TIA AI 产品是 **Eigen Engineering Agent**（原 Industrial Copilot for Engineering / Engineering Copilot TIA）：
  2026-04 汉诺威工博会正式商用，2026-06 增加 ECAD 集成与标准化项目生成；闭源订阅，经 Siemens Digital Exchange 购买，
  使用西门子云端模型，支持 V19–V21；官方资料未提及 MCP 或对外接口。
- 官方开源 `siemens/tia-portal-ai-extensions`（MIT，2026-09）：32 份给 AI 编程助手的 Openness 指南（Markdown），
  不连接 TIA、不执行操作，仅 V21。
- 西门子其他官方 MCP 与 TIA 无关：开发者门户文档问答、KPI 预测、iX 设计语言、Mendix 模块。

| 能力 | Eigen | 本项目 |
|---|---|---|
| 形态 | 闭源订阅，绑定西门子云端模型 | 开放 MCP，任意客户端与模型，本地运行 |
| 版本 | V19–V21 | V14 SP1–V21 |
| SCL/LAD 生成、编译修错、文档与解释、文本翻译 | 有 | 有（代码由客户端模型生成，本项目构建、导入、编译、渲染） |
| Unified HMI、驱动、PROFINET | 有，含 V19 | 仅 V20/V21 |
| 表格批量导入设备 | 有 | 缺 |
| ECAD（XML/AML）→ 设备、连接、变量与一致性检查 | 有 | 部分（AML 导入） |
| 风格指南检查 | 有 | 弱 |
| Classic VB 脚本 → Unified JS 迁移 | 有 | 缺 |
| 按西门子自动化框架（SAF）生成项目 | 有 | 部分（工程骨架与模板） |
| 在线下载、PLCSIM、实时值、库、版本控制、多用户、Teamcenter、F-安全、OPC UA、SiVArc | 资料未提及 | V20/V21 有 |
| 写入前预演、审批、事务、批处理 | 仅“生成计划” | 有 |

社区同类项目（非官方）：GitHub “tia portal mcp” 约 59 个仓库，例如 heilingbrunner/tiaportal-mcp、Czarnak/tia-portal-mcp；
商业产品 T-IA Connect（V17–V21）。

## 三、4.0 之后的计划

见[路线图](roadmap.md#40-之后)。顺序原则：先完成 V20/V21 真机验收；旧版补齐沿用阶段 7“八版共用同一实现”，
把 V20/V21 已有实现移入共享代码再按版本启用，不另写一套。

## 来源（访问日期 2026-10-08）

- Eigen 正式商用新闻稿：https://assets.new.siemens.com/siemens/assets/api/uuid:72c8b639-ecd8-44f3-b67c-09eb4d6e7528/HQCOPR202604167383EN.pdf
- ECAD 集成与项目生成新闻稿：https://assets.new.siemens.com/siemens/assets/api/uuid:b244e71e-20e6-4eec-9faf-cda34d0959a5/HQCOPR202606167406EN.pdf
- Eigen 产品页：https://www.siemens.com/eigen-engineering-agent
- Engineering Copilot TIA beta（SPS 2025）：https://assets.new.siemens.com/siemens/assets/api/uuid:7c447a50-fb36-40fc-b438-19eba5d3f839/HQDIPR202511217291EN.pdf
- tia-portal-ai-extensions：https://github.com/siemens/tia-portal-ai-extensions
- 开发者门户 MCP：https://developer.siemens.com/ai-registry/developer-portal/developer-portal-mcp.html
- TIA Portal V21 新闻稿：https://assets.new.siemens.com/siemens/assets/api/uuid:a9b038ad-86ce-4460-a411-ba5d812e0ac6/HQDIPR202511037269EN.pdf
