# 工程与 Git 工作区同步（VCI）

VCI 将受支持的工程对象与本地文本文件对应，Git 再管理这些文件。VCI 的工程→工作区、工作区→工程同步不是 Git 的 push/pull，也不会自动把工程上传到 GitHub。

## 版本和入口

| 版本 | MCP | 工作台 |
|---|---|---|
| V14 SP1、V15.1 | 基础目录不提供 VCI | 无 VCI 实现 |
| V16、V17 | 提供 VCI 工具。连接时先导出 XML，再建立映射；已有文件只有在内容匹配时才复用 | 使用 WorkspaceMapping 接口 |
| V18、V19 | 提供 VCI 工具，已记录成功的工程到工作区导出。若刷新被拒，`Equal` 是 TIA 保存的状态。同步时通过官方导出核对内容 | 使用 WorkspaceMapping 接口 |
| V20、V21 | V20/V21 提供 VCI 工具 | 使用 MappedObject 接口 |

工具目录中的存在与实际项目支持是两项检查；连接项目后确认其 VCI 服务和可导出对象。工作台的版本控制页经所附着的 MCP 会话调用同一套 VCI 工具。详细支持范围见[版本说明](../reference/version-tools.md)。

## 用 MCP 建立一次基线

1. 在 TIA/MCP 所在电脑上准备用于文本导出的目录，或使用已有 Git 工作目录。
2. 读取 `GetToolUsage(toolName="CreateVersionControlWorkspace")`，使用明确工作区名和目录创建工作区。
3. 读取 `ConnectProjectToWorkspace` 的示例，预览映射范围，核对设备及不支持对象。V16、V17 会自行创建缺少的 XML。预览用 `willCompare` 单列已有 XML，执行时与对象的官方导出比较，匹配后才会复用。V18–V21 的预览也会点名冲突文件；V20/V21 遇到无法用 XML 核验的已有目标或配套文件时会拒绝该对象。若不匹配，请先比较、备份，再移动或删除该文件，重新连接。覆盖导入同名对象后，旧 XML 属于该对象原先已断开的映射。请核对文件名，备份旧 XML，将其从工作区移除，再对同一对象执行连接。
4. 检查 `ListVersionControlWorkspaces` 和 `GetVersionControlStatus` 的实际结果。重新连接已断开的对象后，连接结果和状态中的 `duplicate mapping` 行会列出重复映射的名称、文件和数量。
5. 按 `SynchronizeVersionControlWorkspace` 的示例，以 `ProjectToWorkspace` 同步；检查成功、失败、跳过数量及输出文件。
6. 在文件目录执行 `git status`、`git diff`，选择应纳入版本管理的文件后提交。

这些工具在默认 lite 目录中不一定全部直接显示，可用 `FindTools` 和 `CallTool`。每个工具的参数、预览方式和结果字段以当前 `GetToolUsage` 为准。

## 日常修改和恢复

连接期间请确保只有 TIA 写入该工作区。工具在原生写入前检查目标文件；如果另一进程在原生写入期间创建同名文件，TIA 仍可能覆盖它。

工程修改后，先编译需要导出的块，再读取状态并同步到工作区。V16–V19 先请求刷新，再读取比较状态。V16 可刷新已为 `Equal` 的映射。V17–V19 可能拒绝刷新已知状态，此时工具读取 TIA 当前保存的比较状态。刷新被拒后，若读取到已知状态，状态行和同步行的 `refreshMessage` 会保留原生原因。`unverifiedEqual` 统计其中读到 `Equal` 的对象，即使启用 `changedOnly`，摘要也会报告这个数量。若仍为 `Unknown` 或后续读取遇到普通错误，该对象的 `error` 保留刷新失败的原因。连接中断、操作取消等严重读取错误则报告读取错误本身。

刷新被拒的 `Equal` 在状态查询中标记为 `verified:false`，即使 `changedOnly=true` 也会列出。查询和预览都不导出对象。同步预览以 `verify-before-export` 列出文件名、路径、状态和可读取文件的 SHA-256。预览中的 `planHash` 供核对使用；工作台审批绑定调用参数和工程身份，未绑定这些预览行。执行时会重新读取文件。

两种 `changedOnly` 设置均验证这些对象。工具先将对象正式导出到临时目录，依次比较 `None` 和必要时的 `WithDefaults` SimaticML。内容匹配计为 `verifiedEqual`，并计入 `skippedEqual`。若不匹配，且文件自比较以来仍可读、哈希未变，工程到工作区方向允许 TIA 同步一次，可能覆盖工作区的编辑。同步返回后，工具重新导出并比较内容。如果同步改变了文件，且比较期间文件可读、哈希稳定，就报告 `synchronized`。TIA 写出的格式可能与比较导出不同，`verification` 分别记录 `content-match`、`no-match` 或 `not-comparable`。文件未变时，只有内容匹配才计为 `verifiedEqual`。同步返回后文件不可读或在复核期间再次变化时，写入结果仍不确定。其余未能验证的对象报告 `not-verified`，计入 `failed`。请单独导出该块、数据类型或变量表并比较，备份后移走过时文件，再同步。更换文件后的恢复效果仍待实机验证。

同步结果中，`wouldVerify` 是待验证数，`unverifiedEqual` 是预览待定或执行后仍未证明的数目。此时 `dataComplete=false`。部分对象已写入而其他对象验证失败时返回 `PARTIAL_FAILURE`；若写入结果不确定，请先检查文件。断开的映射列入 `skippedDisconnected`，提示中列出文件和重新连接方法。仅有断开映射时同步成功，完整性仍为 false。批次中止时，已选中但尚未尝试的对象列入 `notAttemptedNames`，数量见 `notAttempted`。在 `ConnectProjectToWorkspace` 中，`notAttemptedKind=walk-nodes` 表示这两个字段记录中止时待遍历的节点名称和数量。节点可以是文件夹，其尚未展开的子对象不计入数量。

默认设置跳过状态未知且没有可确认文件差异的对象；`changedOnly=false` 会尝试同步未知对象。经 MCP 同步（包括工作台的 MCP 会话）只开放 `ProjectToWorkspace`。请求 `WorkspaceToProject`（包括预览）会返回 `UNSUPPORTED_CAPABILITY`。使用 Studio 原生接口恢复时，V16–V19 在其他对象导入后重新比较刷新被拒的 Equal，未验证的行不会导入工程。V20/V21 在同步 SIMATIC SD 块前检查准入条件，返回每个拒绝、失败或结果未知的对象及原因。

## 支持范围和输出解释

如果没有对象成功导出，但已保留映射或写入工作区 XML，工具仍会返回 `PARTIAL_FAILURE`。V4 校验器拒绝“0 项成功、N 项失败”（N 大于 0）的组合，因此 `error.details` 中的三项计数均为 0。实际失败数见 `data.evidence.failed`。

VCI 对可映射对象逐项报告结果。块、变量表、数据类型等程序对象通常是主要用途；硬件、受保护块和选件对象是否支持要看实际返回，不能把“整个工程扫描完成”解释成所有内容已导出。保留 TIA 工程备份以覆盖文本同步以外的内容。

若提示块不一致，先编译该块；若提示访问保护，按工程的保护设置处理。V16、V17 的实机记录中，已建立映射的对象也可能报 `The file is deleted`。请核对工作区内的实际文件，并保留每个未导出对象的错误记录。

自动导出和提交的可选工具见 [vci-watch](https://github.com/asckye/automation-engineering-workbench/blob/master/scripts/operations/vci-watch/README.md)。先完成一次手动基线，再配置自动化和提交范围。变更日志和提交说明只列出本轮实际同步的对象。本轮没有同步对象时，若 Git 工作区已有变更，工具以“工作区文件变更”提交。
