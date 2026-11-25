# 工程与 Git 工作区同步（VCI）

VCI 将受支持的工程对象与本地文本文件对应，Git 再管理这些文件。VCI 的工程→工作区、工作区→工程同步不是 Git 的 push/pull，也不会自动把工程上传到 GitHub。

## 版本和入口

| 版本 | MCP | 工作台 |
|---|---|---|
| V14 SP1、V15.1 | 基础目录不提供 VCI | 无 VCI 实现 |
| V16、V17 | 提供 VCI 工具。映射路径已有该对象的有效 SimaticML XML 文件时，首次导出可成功。缺少目标文件时，首次导出可能失败并留下映射 | 使用 WorkspaceMapping 接口 |
| V18、V19 | 提供 VCI 工具，已记录成功的工程到工作区导出。若刷新被拒，`Equal` 是 TIA 保存的状态。如果编辑过对象，请核对工作区文件 | 使用 WorkspaceMapping 接口 |
| V20、V21 | V20/V21 提供 VCI 工具 | 使用 MappedObject 接口 |

工具目录中的存在与实际项目支持是两项检查；连接项目后确认其 VCI 服务和可导出对象。工作台的版本控制页经所附着的 MCP 会话调用同一套 VCI 工具。详细支持范围见[版本说明](../reference/version-tools.md)。

## 用 MCP 建立一次基线

1. 在 TIA/MCP 所在电脑上准备用于文本导出的目录，或使用已有 Git 工作目录。
2. 读取 `GetToolUsage(toolName="CreateVersionControlWorkspace")`，使用明确工作区名和目录创建工作区。
3. 读取 `ConnectProjectToWorkspace` 的示例，预览映射范围，核对设备及不支持对象。V16、V17 请先按目标文件名放入对应对象导出的有效 SimaticML XML，再执行映射。
4. 检查 `ListVersionControlWorkspaces` 和 `GetVersionControlStatus` 的实际结果。
5. 按 `SynchronizeVersionControlWorkspace` 的示例，以 `ProjectToWorkspace` 同步；检查成功、失败、跳过数量及输出文件。
6. 在文件目录执行 `git status`、`git diff`，选择应纳入版本管理的文件后提交。

这些工具在默认 lite 目录中不一定全部直接显示，可用 `FindTools` 和 `CallTool`。每个工具的参数、预览方式和结果字段以当前 `GetToolUsage` 为准。

## 日常修改和恢复

工程修改后，先编译需要导出的块，再读取状态并同步到工作区。V16–V19 先请求刷新，再读取比较状态。V16 可刷新已为 `Equal` 的映射。V17–V19 可能拒绝刷新已知状态，此时工具读取 TIA 当前保存的比较状态。刷新被拒后，若读取到已知状态，状态行和同步行的 `refreshMessage` 会保留原生原因。`unverifiedEqual` 统计其中读到 `Equal` 的对象，即使启用 `changedOnly`，摘要也会报告这个数量。若仍为 `Unknown` 或后续读取遇到普通错误，该对象的 `error` 保留刷新失败的原因。连接中断、操作取消等严重读取错误则报告读取错误本身。

V16–V19 编辑后 TIA 保存的状态是否及时更新，仍待实机验证。若已知工程对象有改动却读到 `Equal`，同步会跳过它。请打开工作区目标文件，核对改动确实写入后再将其视为已导出并提交 Git。`Unequal` 也可能受文件时间影响，请检查实际文本差异。

同步时，两种 `changedOnly` 设置都会跳过 `Equal`。默认设置也跳过 `Unknown`；设为 `false` 会尝试导出这些状态未知的对象。当前 MCP 和工作台只开放 `ProjectToWorkspace`，请求 `WorkspaceToProject`（包括预览）会返回 `UNSUPPORTED_CAPABILITY`。

## 支持范围和输出解释

VCI 对可映射对象逐项报告结果。块、变量表、数据类型等程序对象通常是主要用途；硬件、受保护块和选件对象是否支持要看实际返回，不能把“整个工程扫描完成”解释成所有内容已导出。保留 TIA 工程备份以覆盖文本同步以外的内容。

若提示块不一致，先编译该块；若提示访问保护，按工程的保护设置处理。V16、V17 的实机记录中，已建立映射的对象也可能报 `The file is deleted`。请核对工作区内的实际文件，并保留每个未导出对象的错误记录。

自动导出和提交的可选工具见 [vci-watch](https://github.com/asckye/automation-engineering-workbench/blob/master/scripts/operations/vci-watch/README.md)。先完成一次手动基线，再配置自动化和提交范围。
