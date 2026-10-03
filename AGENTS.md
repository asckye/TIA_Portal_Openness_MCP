# 给 Codex 的执行规则

仓库通用规则见 [CLAUDE.md](CLAUDE.md)，重构目标、约束与任务清单见
[重构计划](docs/development/refactor-plan.md)。每次执行只完成任务说明中的一个任务。

## 范围

- 只修改任务说明列出的路径。确需改动范围外的文件时先停下，在报告中说明原因。
- 阶段 0–5 不改变 MCP 对外接口：工具名称、参数名与类型、返回结构和错误语义保持不变。
- 不改变西门子 Openness 调用的顺序、参数和线程归属，除非任务明确要求；有改动时在报告中列出前后调用序列。
- 不编辑 `manifest/*.json` 中的哈希，不手改生成文件（`manifest/tools-list.json`、
  `tools/openness-shared/ToolUsageData.json`、`docs/reference/tool-matrix.md` 等），除非任务要求运行对应生成器。
- 不连接 TIA Portal、PLC、VM 或任何网络服务；不运行 live 测试分支。

## 代码

- C# 源码使用无 BOM UTF-8 和 LF；保持周围代码的命名、注释密度和写法。
- 移动或重命名源文件时同步更新所有 `<Compile Include>` 链接、测试工程和文档中的路径。
- 控制台测试工程用 `dotnet run --project <csproj> -c Release` 执行；`dotnet test` 不执行其断言。
- 需要 Siemens 程序集的编译使用任务说明给出的 `-p:SiemensEngineeringDirectory=<绝对路径>`。

## 提交

- 在当前分支提交，使用显式 `git add <路径>`，不用 `git add -A` 或 `git add .`。
- 英文提交信息，首行概括改动；不加 AI 署名或 Co-Authored-By。
- 不推送、不合并、不改写已有提交历史。

## 报告

结束时按以下顺序输出：

1. 改动的文件列表。
2. 运行的命令及结果（通过数、失败数）。
3. 与任务说明不一致的地方及原因。
4. 未解决的问题或需要决定的事项。
