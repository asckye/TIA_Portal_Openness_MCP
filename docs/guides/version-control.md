# 工程与 Git 工作区同步（VCI）

VCI 将受支持的工程对象与本地文本文件对应，Git 再管理这些文件。VCI 的工程→工作区、工作区→工程同步不是 Git 的 push/pull，也不会自动把工程上传到 GitHub。

## 版本和入口

| 版本 | MCP | Studio |
|---|---|---|
| V14 SP1、V15.1 | 基础目录不提供 VCI | 无 VCI 实现 |
| V16–V19 | 基础目录不提供 VCI | 使用旧版 WorkspaceMapping 接口 |
| V20、V21 | 完整引擎提供 VCI 工具 | 使用 MappedObject 接口 |

工具目录中的存在与实际项目支持是两项检查；连接项目后确认其 VCI 服务和可导出对象。Studio 直接调用 Openness，不经过 MCP。详细支持范围见[版本说明](../reference/version-tools.md)。

## 用 MCP 建立一次基线

1. 在 TIA/MCP 所在电脑上创建一个用于文本导出的空目录，或使用已有 Git 工作目录。
2. 读取 `GetToolUsage(toolName="CreateVersionControlWorkspace")`，使用明确工作区名和目录创建工作区。
3. 读取 `ConnectProjectToWorkspace` 的示例，先预览映射范围，核对设备及不支持对象，再执行映射。
4. 检查 `ListVersionControlWorkspaces` 和 `GetVersionControlStatus` 的实际结果。
5. 按 `SynchronizeVersionControlWorkspace` 的示例，以 `ProjectToWorkspace` 同步；检查成功、失败、跳过数量及输出文件。
6. 在文件目录执行 `git status`、`git diff`，选择应纳入版本管理的文件后提交。

这些工具在默认 lite 目录中不一定全部直接显示，可用 `FindTools` 和 `CallTool`。每个工具的参数、预览方式和结果字段以当前 `GetToolUsage` 为准。

## 日常修改和恢复

工程修改后，先编译需要导出的块，再读取状态并同步到工作区。检查文本实际差异后提交 Git。`Unequal` 可能受文件时间等状态影响，不必然意味着 Git 中有内容差异；已为 `Equal` 的对象会被跳过。

需要把文本修改导回工程时，先查看 Git 差异和所选工作区，再按示例使用 `WorkspaceToProject`。导入后检查实际对象、编译结果并保存工程。恢复文本版本与恢复整个二进制工程不是一回事。

## 支持范围和输出解释

VCI 对可映射对象逐项报告结果。块、变量表、数据类型等程序对象通常是主要用途；硬件、受保护块和选件对象是否支持要看实际返回，不能把“整个工程扫描完成”解释成所有内容已导出。保留 TIA 工程备份以覆盖文本同步以外的内容。

若提示块不一致，先编译该块；若提示访问保护，按工程的保护设置处理。工程中某个对象未导出时，应保留失败记录并说明缺口。

自动导出和提交的可选工具见 [vci-watch](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/scripts/operations/vci-watch/README.md)。先完成一次手动基线，再配置自动化和提交范围。
