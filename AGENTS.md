# 给 Codex 的执行规则

仓库通用规则见 [CLAUDE.md](CLAUDE.md)，重构目标、约束与任务清单见
[重构计划](docs/development/refactor-plan.md)。每次执行完成任务说明中的全部内容；一份说明可以包含多个计划编号。

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
- 删除、移动或新增文件的任务都要运行 `Validate-Bundle.ps1 -Strict -NoBinaries -SkipSourceHashes` 与
  `Check-Repository.py --no-binaries`；两者含必需文件清单，`Package-Release.py` 另有一份同类清单。

## 提交

- 不执行 `git add`、`git commit`、推送、合并或改写历史；改动留在 worktree 中，由审查者核对后提交。
  worktree 的 Git 元数据位于沙箱可写范围之外。
- 任务说明要求分多个提交时，在报告中按提交分组列出文件，并给出英文提交信息（不加 AI 署名）。

## 报告

结束时按以下顺序输出：

1. 改动的文件列表。
2. 运行的命令及结果（通过数、失败数）。
3. 与任务说明不一致的地方及原因。
4. 未解决的问题或需要决定的事项。
