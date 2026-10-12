# 运行布局与清理的历史记录

状态：历史记录 · 最后核对：2026-10-11，master `578a05ee`

本页保留 G7 与 P2-05 的任务表、起点统计和阶段性语言政策。现行布局见[运行时布局](../../development/runtime-layout.md)。

### 历史任务

| 任务 | 完成内容 | 状态 |
|---|---|---|
| G7-1 | P0-06 加入参考资料工具及 bin 布局下的更新检查基线 | done |
| G7-2 | 资源解析器、布局单元测试与 Python 交付资源检查器 | done |
| G7-3 | Logic 中的 R1 接入解析器，保留既有覆盖与兼容回退 | done |
| G7-4 | 引擎 R2、R3、R7 与 CLI 配置写入接入解析器 | done |
| G7-5 | Studio R11–R14 接入；修复 worktree `.git` 判断，清除死回退并校验 Launcher 路径 | done |
| G7-6 | V21 生态目录直接嵌入 V20/V21 引擎；指南仍从磁盘读取 | done |
| G7-7 | 更新现行布局文档与 CHANGELOG；关闭 G7，解除 P1-07 的 G7 阻塞 | done |

## 历史清理记录与现行文案语言

### 注释与 `*Leftovers`

- 引擎含产品版本号的注释 221 行（93 个文件），Logic 33 行；旧“Phase N”说法 32 处（与当前重构阶段同名，易混）；维护者原话 13 处；
  中文修复叙事 48 处；迁移墓碑注释 20 处；注释掉的代码约 9 行；与代码矛盾的工具数 4 处。
- 改写规则：删除版本号、维护者归属和旧 Phase 说法；原生行为证据（约 91 行）保留并写明 TIA/PLCSIM 版本、日期和
  [真机验收清单](../../reference/real-machine-ledger.md)链接（D-P5-1）；修复叙事改写为不变量；墓碑、注释掉的代码和过时计数直接删除；
  改写时保留原注释的语言（D-P5-6）。生成脚本内容里的“TODO”属于响应字节，不得改动。
- `*Leftovers`：产品文件 6 个、1,289 行，成员分属内核、基础设施、在线、硬件、PLC 数据、Motion/ProDiag、PLC 程序等领域，随阶段 3
  各步骤的纯迁移逐步消解，不单独先改名（D-P5-2）；步骤 15 结束时 `*Leftovers*` 文件数为 0。`engine-decomposition.md` 把
  Step7Leftovers 列为步骤 7 的可选包，与实际内容不符，由 P2-05d 修正。

P3-18 将最后三个产品余项文件按职责拆开，产品与测试均不再使用 `*Leftovers*` 文件名；注释与吞异常基线均为 0。
会话成员分别在 `Portal.HmiOperation.cs`、`Portal.Diagnostics.cs`、`Portal.SessionResolvers.cs`；对象标识和事务现位于
`Adapters/Native/Engineering/EngineeringServices.cs` 与 `Adapters/Native/Engineering/EngineeringTransaction.cs`。原 Base 校验按硬件实用程序、设备服务对象、对象标识、事务、凭据
归入引擎内的具名 `*Rules` 类；原 Step7 校验按外部源、PLC 表、报警文本列表与 ProDiag 导出归入 Logic 内的具名 `*Rules` 类。
P8-03f 的工程服务、库和反射原生实现由 adapter 织入覆盖，其余引擎原生调用仍由引擎织入覆盖。
工具共享实现直接由具名辅助类提供，目录与注册基础设施仍在 `McpServer`；
不再经过迁移期嵌套 `*ToolSupport` 或 HMI 编译转发。

### 用户可见文案语言

- 现状：V21 的 488 条工具描述中 482 条为纯英文，其余 6 条只在引用 TIA 原文或示例名称时含中文；参数描述 2,349 条中 11 条含中文示例值；
  30 个 prompts 全英文；引擎与 Logic 约 100 条消息含中文（另有中文报告、双语表和 CLI 输出）。
- 维护者已决定（2026-10-03）：
  - 阶段 0–5：所有对 MCP 可见的文本冻结（含中文消息与错别字）；**新增文本一律英文**，中文只作为数据出现（TIA 原文、示例名称）；
    中文字面量用检查器约束只减不增。
  - 阶段 6（4.0）：MCP 文本统一为英文，错误加稳定的机器码，使文本不再承担功能；提供旧→新对照表。
  - 不为 MCP 响应引入本地化层（调用方是 AI，本地化会破坏快照与确定性）；本地化只在 Studio（`Loc`）和 CLI doctor 输出。
- 功能性文本（`TryCanonicalizeEnumArgument` 解析的拒绝文本、快照脚本的标记、`Test-LocalStability` 检查的 stderr、FindTools 的关键词
  检索）在阶段 6 引入错误码之前不得改动。

### 任务

| 任务 | 内容 | 证明 |
|---|---|---|
| P2-05a | 注释清点与 MCP 可见中文字面量两个只减不增检查器（复用 `Check-SwallowedExceptions.py` 的词法器），接入仓库检查；语言政策写入 `tool-development.md` | 自检；不改 C# |
| P2-05b | 阶段 3 领域之外只改注释（Logic、Runtime、CLI、Studio、基础设施） | 引擎 EXE SHA-256 不变；其他程序集确定性构建前后一致；去注释比较 0 差异 |
| P2-05c… | 阶段 3 每一步的第一阶段提交（与吞异常原因一起） | 同上 |
| P2-05d | `*Leftovers` 随领域迁移消解，拆分并改名两个 Logic 类 | 阶段 3 标准证据 |
| P2-05e | Studio 剩余硬编码中文迁入 `Loc` | Configuration/Strings/Gui 测试 |
| P2-05z | 统一 MCP 文本并引入错误码 | 阶段 6 |

## 决策记录

| 编号 | 决定 |
|---|---|
| D-G7-1 | 布局用 `openness-shared` 中的代码表，`manifest/package-manifest.json` 为根标记，`Check-BundleLayout.py` 校验 |
| D-G7-2 | 历史阶段 0–5 使用旧根变量；P6-37 硬切为 `--bundle-root` / `TIA_MCP_BUNDLE_ROOT`，旧名不作别名 |
| D-G7-3 | P6-37 已删除引擎/Foundation/CLI 的兼容探测；Studio 收口由 P6-38 执行 |
| D-G7-4 | 开发锚点编入产品，只认枚举出的引擎、Foundation 和测试宿主输出；其他测试布局显式给根 |
| D-G7-5 | `v21-ecosystem.json` 已直接嵌入 V20/V21 引擎；指南仍读取随包文件，改为嵌入前需另做差分证明 |
| D-G7-6 | 软件自身数据优先留在包内，旧用户路径作为回退；P6-39 崩溃日志接 data/logs/studio，Python 默认位于 LocalAppData |
| D-G7-7 | G7-5 已修复 Studio 的 worktree `.git` 判断，文件和目录都禁止安装更新（仅 UI 行为） |
| D-G7-8 | 私有工作区默认值阶段 0–5 不动，阶段 6 删除或改为显式输入 |
| D-P5-1…6 | 见上文“注释与 `*Leftovers`”与“用户可见文案语言”；D-P5-3/5（新文本英文、4.0 统一英文）由维护者决定（2026-10-03） |

