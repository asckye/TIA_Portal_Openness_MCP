# 给 Codex 的执行规则

通用开发规范见[《工程开发手册》](docs/standards/engineering-handbook.html)（最小必做清单、代码质量、注释、测试和完成定义）及本项目的[采用说明](docs/standards/README.md)；仓库通用规则见 [CLAUDE.md](CLAUDE.md)，范围、任务状态和放行门槛见[发布计划](docs/status/release-plan.md)。每次执行完成任务说明中的全部内容；一份说明可以包含多个计划编号。

## 范围

- 修改任务说明列出的路径，以及为完成任务必须同步修改的测试、生成产物、注册清单和文档，并在报告中逐项列出。其他范围外的改动先停下，在报告中说明原因。
- MCP 对外接口（工具名称、参数名与类型、返回结构和错误语义）只在任务说明要求时改变；改变后用正式工具重新生成契约与响应基线，在报告中逐项列出差异。
- 不改变西门子 Openness 调用的顺序、参数和线程归属，除非任务明确要求；有改动时在报告中列出前后调用序列。
- 不编辑 `manifest/*.json` 中的哈希，不手改生成文件（`manifest/tools-list.json`、
  `src/Shared/ToolUsageData.json`、`docs/reference/tool-matrix.md` 等），除非任务要求运行对应生成器。
- 不连接 TIA Portal、PLC、VM 或任何外部网络服务；不运行 live 测试分支。测试和检查自己在本机回环地址（127.0.0.1）上启动的合成服务（HTTP 夹具、命名管道夹具等）是允许的，注册套件、preflight 和传输检查都要完整运行，不得因此跳过。
- 只能停止自己在本 worktree 中启动的进程：先核对可执行文件路径或命令行位于本 worktree 内，再按进程号停止；绝不按进程名或模糊匹配停止进程，绝不停止其他 worktree、审查者或用户的进程。
- 不删除、跳过、过滤、放宽或重设任何功能检查、测试、断言、阈值和门禁（含套件最低通过数、最大跳过数、计时与增长上限）；认为某项检查有误或不稳定时保持原样，在报告中附证据说明，由维护者决定。功能按设计有意改变时，用仓库内正式工具重新生成受影响的生成基线和 golden 文件（契约/响应捕获、`TIA_MCP_UPDATE_GOLDEN=1`、`check-tia-features -Capture`、各生成器）是允许的，在报告中逐项列出差异；改阈值、改断言、删除或跳过检查仍须维护者批准。
- 任务说明给出时间预算；到时完成当前一批，干净收尾并列出剩余工作。

## 验证

- 交付前运行一次 `dotnet run --project build-tools/release -- check-all -Affected -Quick`（约 15 分钟上限）和任务说明点名的套件，报告通过、失败和阻塞数。沙箱中无法运行的作业（例如需要 HTTP 或并行 MSBuild 的作业）列出名称和原因，由审查者在主机运行。
- 替换代码路径时补对等测试；新命令至少真实运行一次；夹具使用真实格式的数据。
- 已迁移套件以 `tests/test-suites.json` 注册表为准，用 `dotnet run --project build-tools/release -- test-suites -Suite <name>` 执行并检查注册的 trx 最低通过数和最大跳过数；开发时可直接用 `dotnet test <csproj> -c Release`。未注册的控制台工程仍用 `dotnet run --project <csproj> -c Release`，`dotnet test` 不执行其断言。
- 删除、移动或新增文件的任务都要运行 `dotnet run --project build-tools/release -- validate-bundle -Strict -NoBinaries -SkipSourceHashes` 与
  `Check-Repository.py --no-binaries`；两者含必需文件清单，`Package-Release.py` 另有一份同类清单。

## 代码

- C# 源码使用无 BOM UTF-8 和 LF；保持周围代码的命名、注释密度和写法。
- 构建与发布入口使用 `dotnet run --project build-tools/release -- <command>`；不要新增受跟踪的 `.ps1`、`.psm1`、`.bat` 或 `.cmd` 文件。
- 移动或重命名源文件时同步更新所有 `<Compile Include>` 链接、测试工程和文档中的路径。
- 需要 Siemens 程序集的编译使用任务说明给出的 `-p:SiemensEngineeringDirectory=<绝对路径>`。
- 嵌入目录按 `dotnet run --project build-tools/release -- generate-catalogs`、`dotnet run --project build-tools/release -- generate-tool-usage`、`generate-tool-counts` 的顺序生成，再构建与捕获；迁移表用 `generate-release-migration`。这些命令的 `-Check` 都逐字节检查。

## 提交

- 不执行 `git add`、`git commit`、`git stash`、推送、合并或改写历史；改动留在 worktree 中，由审查者核对后提交。
  worktree 的 Git 元数据位于沙箱可写范围之外。
- 任务说明要求分多个提交时，在报告中按提交分组列出文件，并给出英文提交信息（不加 AI 署名）。

## 报告

结束时按以下顺序输出：

1. 改动的文件列表。
2. 运行的命令及结果（通过数、失败数、阻塞数）。
3. 与任务说明不一致的地方及原因。
4. 未解决的问题或需要决定的事项。
