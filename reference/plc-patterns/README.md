# 经典 PLC 模式候选库

本目录保存项目自行编写的 SCL 外部源及验证证据。S4a 的六个模式覆盖 S7-1200、S7-1500，
不覆盖 S7-1200 G2。所有原生验收和 PLC 运行行为均为 **NOT RUN**，当前不会通过 `GetToolUsage` 提供。
不含第三方模式库的代码、文字、标识符或导入选项。

每个模式一个目录，`pattern.json` 的 `schemaVersion` 为 1；未知字段会拒绝生成。
顶层字段为 `id`、`title`、`summary`、`notesZhCn`、`variants`。每个变体声明：

| 字段 | 含义 |
|---|---|
| `id`、`kind` | SCL 为 `scl` / `scl-s7-1200` / `scl-s7-1500`，类型为 `scl-external-source`；LAD 留待 S4c 实现 |
| `cpuFamilies` | 声明适用的 CPU 家族；SCL 每个家族均要求八版证据 |
| `files` | `path` 必须位于当前模式目录内，`encoding` 为 `ASCII`；无 BOM、LF、无制表符 |
| `contains` | 按源文件顺序列出 `kind`（UDT/FB/FC/DB）、`name`，实例 DB 另列 `instanceOf` |
| `instructions` | 每项有 `name`、`cpuFamilies`、`sourceUrl`、`verification`，可带 `version`、`verificationNote` |
| `notReleased` | 可选静态说明；每项有 `releaseKey`、`cpuFamily`、`reason`、`sourceUrl`，不能代替原生缺失证据 |
| `importSequence`、`tools` | 使用既有外部源导入序列，所列工具必须注册在全部适用版本 |

离线未取得指令手册页的名称和版本均标为 `unverified`，这些名称仅按任务说明编写，尚无官方核实结论。
`sourceUrl` 此时只记录 Siemens 文档入口，`verificationNote` 明确边界；禁止把入口当作具体指令出处。
提供模式前，每条指令的 `verification` 必须恰为 `verified`；缺失或空值拒绝加载，已核实指令的 `version` 不得为 `unverified`。
负责人须核对名称、填写确切官方页并更新 `verification`，指令实测版本记在证据行。
源文件还须通过现有 SCL 启发式 lint，声明与源中块的顺序和名字一致；lint 不等于 TIA 编译。
S7-1200 模式拒绝长时间类型，所有模式拒绝绝对 I/Q/M/DB 操作数和未声明的已知指令。

[`evidence.json`](evidence.json) 的 `schemaVersion` 为 1，`rows` 按模式、变体、发布键顺序、CPU 家族排序。
S4a 有 6 × 8 × 2 = 96 行。每行唯一键为 `pattern`、`variant`、`releaseKey`、`cpuFamily`，另有 `status`：

| 状态 | 证据要求与提供规则 |
|---|---|
| `NOT RUN` | 初始只写五个字段；阻止整个变体提供 |
| `verified` | 必须有日期、包或提交、TIA 更新、CPU 订货号、固件、零错误编译记录（含警告数）及 `runId` |
| `failed` | 保留 `nativeMessage`；阻止整个变体提供，源文件问题交回实现者 |
| `absent` | 必须保留指令不存在的 `nativeMessage` 和 Siemens 官方 `sourceUrl`；builder 未注册不算指令缺失 |

实测时另记 `instructionVersions`（名称到版本）、`exportSha256`（块名到导出哈希）。
负责人从 VM 记录维护证据；只有全部适用格为 verified/absent、至少一格 verified 且指令出处已核实时才提供。
提供记录包括每版每家族的 availability、实测版本、日期、包和验证边界；PLC 运行行为仍为 NOT RUN。
全部 NOT RUN 时不添加语言或示例，生成的 `ToolUsageData.json` 保持原字节。

VM 导入与证据记录步骤见[真机测试手册](../../docs/manuals/real-machine-testing.md#工具示例库)，
待验收范围见[真机台账](../../docs/reference/real-machine-ledger.md)。
