# 命令行与工程生成

八个版本（V14 SP1、V15.1、V16–V21）都由 `TiaMcp.FoundationHost.exe` 提供同一套本机诊断与配置命令。首次使用见[新手指南](beginners.zh-CN.md)；图形配置入口见[工作台](configuration.md)。

## 选择发布版本

从交付包根目录运行，命令在前，选项在后。下例选择 V21；其他版本将 `v21` 与发布键一起替换为 `v14sp1` / `14sp1`、`v15.1` / `15.1` 或 `v16`–`v20` / `16`–`20`。

```powershell
.\runtime\v21\TiaMcp.FoundationHost.exe doctor --release-key 21
.\runtime\v21\TiaMcp.FoundationHost.exe schema --release-key 21
.\runtime\v21\TiaMcp.FoundationHost.exe config --print --release-key 21
.\runtime\v21\TiaMcp.FoundationHost.exe audit verify --release-key 21
.\runtime\v21\TiaMcp.FoundationHost.exe install-plc-tools --release-key 21
.\runtime\v21\TiaMcp.FoundationHost.exe help
```

文档中的 `tia` 是所选宿主的简写。可在当前 PowerShell 会话设置别名：

```powershell
Set-Alias tia (Resolve-Path .\runtime\v21\TiaMcp.FoundationHost.exe).Path
tia doctor
tia audit verify
```

随包 `release-key.txt` 提供默认发布键；开发构建默认选择 `21`。`--tia-version KEY` 与 `--tia-major-version N` 保留为发布选择的别名；V14 SP1 与 V15.1 必须使用精确键 `14sp1`、`15.1`，原始 V14/V15 不受支持。显式键须与宿主旁的发布标记一致。资源根使用 `--bundle-root <绝对目录>` 或 `TIA_MCP_BUNDLE_ROOT`。

## 保留命令

| 命令 | 用途与输出 |
|---|---|
| `doctor [--fix] [--json]` | 本机安装、Openness API 与版本匹配、.NET Framework 4.8、Windows 网络来源标记、worker 启动就绪、用户组和 AI 客户端配置；输出可读体检报告，`--json` 改为输出一个 JSON 对象 |
| `audit verify [--path <绝对目录>]` | 输出审计链 JSON 报告；完整返回 0，损坏返回 3 |
| `schema` | 原样输出旧版 JSON/YAML spec 字段参考，供维护已有 spec；新工程使用生成框架 |
| `version` | 输出 `tia <程序集版本>` |
| `config [--host <客户端>] [--print] [--full]` | 为所选版本生成或写入 MCP 客户端配置；`--print` 只打印 JSON/TOML，`--full` 显式选择完整工具清单 |
| `install-plc-tools [--python <路径>] [--environment-path <绝对目录>]` | 使用 Python 3.12+ 建立伴随环境并安装依赖 |
| `help` / `--help` / `-h` | 输出用法、八版发布键、替代工具和超时说明 |

`config` 支持 Claude Desktop、Claude Code、Cursor、VS Code、Codex、Gemini CLI、Windsurf 和 Cline。默认只写入检测到的客户端，显式 `--host` 可指定目标；合并保留其他配置，修改前备份为 `.bak`。配置写入后重启 AI 客户端。

`doctor` 默认只读。`--fix` 经现有 worker 用户组修复操作添加当前用户，可能弹出 UAC；完成后注销再登录。需要原生启动事实时使用 worker 观测，不连接 Portal 或打开工程。API 或 worker 无法启动时仍输出其余本机检查与修复提示。`--offline` 不启动 worker，不能执行用户组修复。非默认安装可传 `--tia-portal-location <安装目录>`；开发输出可传 `--public-api <准确版本的 API 目录>` 和 `--worker-exe <worker 路径>`。V20/V21 的过渡开关 `--plc-worker` 选择独立 PLC worker；不提供时仍用引擎 worker。

`doctor --json` 不输出可读报告，改为在 stdout 输出一行 JSON 对象，退出码与不带 `--json` 时相同。字段：`releaseKey`、`fix`（是否带 `--fix`）、`ready`、`logs`、`diagnostics`；`checks` 为各检查行（`id`、`ok`、`gating`、`name`、`detail`、`fix`，文字与可读报告相同）；`group` 为用户组结果（`ok`、`outcome`、`code`、`message`）；`clients` 为 AI 客户端配置（`name`、`registered`、`command`、`commandExists`）；`report` 为可读报告的逐行文本。

`group.outcome` 只在带 `--fix` 时给出，否则为 `null`：

| `outcome` | `code` | 含义 |
|---|---|---|
| `Ready` | `null` | PlcWorker 修复后当前登录令牌已含该组 |
| `PendingLogon` | `OPENNESS_GROUP_RELOGON_REQUIRED` | 用户已是组成员，令牌尚未生效；注销并重新登录 |
| `AddedPendingLogon` | `OPENNESS_GROUP_ADDED_RELOGON_REQUIRED` | 已加入组；注销并重新登录后生效（`ready` 仍为 `false`，退出码 2） |
| `ElevationDeclined` | `OPENNESS_GROUP_ELEVATION_DECLINED` | 拒绝了 UAC 管理员授权，未添加 |
| `MissingGroup` | `OPENNESS_GROUP_MISSING` | 本机没有 `Siemens TIA Openness` 组；安装带 Openness 的 TIA Portal |
| `AddFailed` | `OPENNESS_GROUP_ADD_FAILED` | 添加后仍不是组成员 |
| `Unknown` | `null` | 修复失败但没有上述结果码（`message` 为原因），或 V20/V21 引擎 worker 的修复只返回布尔值（`ok` 为其结果，不区分是否待重新登录） |

工作台的环境修复按钮运行所选版本随包宿主的 `doctor --fix --json --release-key <版本> --logging 0`，按 `group.outcome` 显示就绪、需注销重登或修复失败（附原因），并把报告行放入日志区；没有 JSON 对象时按退出码判断。

`doctor` 显示实际日志与诊断目录。宿主启动及运行消息共用 `data/logs/<releaseKey>/TiaMcpServer-<processKey>.log`，工作台崩溃日志位于 `data/logs/studio`；进程键含 PID、启动 UTC、GUID。只读安装的普通日志回退到 `%TEMP%\TiaMcp\logs`。每次启动按用途保留最新 32 份，跳过锁定、活动进程和非产品文件；写入失败每用途每进程报告一次 `IO_FAILED`。

审计写入 `data/logs/audit` 的共享单链，只读安装回退到 `%LOCALAPPDATA%\TiaMcp\logs\audit`，失败报 `DIAGNOSTIC_WRITE_FAILED`。`tia audit verify` 分别校验主目录和已存在的回退链，报告链目录、断点文件及序号；显式数据根不回退。校验不能证明整份日志没有被删除。`data/diagnostics` 保存原生调用诊断包。

Python 默认解释器为 `%LOCALAPPDATA%\TiaMcp\ecosystem-python\Scripts\python.exe`；自选解释器使用 `TIA_MCP_PLC_TOOLS_PYTHON`。安装命令的 pip 按当前索引设置联网安装；离线安装可设置 `PIP_NO_INDEX=1` 并提供本地 wheel 缓存。详见[生态工具](../reference/ecosystem-tools.md)。

## 退出码

`0` 成功；`2` 操作前被拒绝、`doctor` 未就绪或 `config` 所有尝试写入均失败；`3` 失败或读取失败；`4` 部分完成；`5` 结果未知；`64` 命令或参数写法错误；`70` 无法建立运行环境或缺少包资源。八个版本的宿主均采用此约定：旧引擎中 `doctor` 未就绪和 `config` 写入失败的退出码 `1`，在宿主中改为 `2`。`install-plc-tools` 仍原样返回外部 pip 进程的非零退出码，包括 `1`。没有可写客户端且没有写入错误时，`config` 提示未写入并返回 `0`。`4`、`5` 不能视为成功。

## 退役的工程命令

宿主不接受以下命令。V20/V21 引擎在删除前仍执行原有命令，并额外向 stderr 打印一行替代指引；stdout JSON 和退出码保持原样。

| 旧命令 | 替代入口 |
|---|---|
| `gen`、`patch` | [工程生成框架](../guides/project-generation.md)：`PlanProjectGeneration`、`ApplyGenerationPlan`，通过工作台预览与审批 |
| `describe` | MCP `GetProjectTree`，再按所选 PLC/HMI 查询 |
| `compile` | MCP `CompilePlcDiagnostics` 或对应设备/HMI 编译工具 |
| `export`、`import` | 对应 PLC/HMI 的导入导出 MCP 工具；先用 `FindTools` 和 `GetToolUsage` 核对所选版本、输入格式与路径 |
| `prewarm` | MCP `ConnectPortal`，由 MCP 会话持有 worker 与连接 |

旧引擎的 `gen`、`patch`、`describe`、`compile`、`export`、`import` 在 stdout 只打印一份 V4 结果 JSON，诊断写入 stderr。`schema` 仍是这些旧命令的字段参考，不能直接替代生成框架的标准包与机器输入。现有蓝图见 [templates/project-blueprints](../../templates/project-blueprints)；PLC 文档构建范围见 [Builder 说明](../guides/plc/builders.md)。

## MCP 宿主与 worker

八版都由 FoundationHost 提供 MCP，每个 MCP 会话使用一个 worker。使用工作台生成的宿主命令；隔离开关 `--isolate-openness` 和 `--no-isolate-openness` 已删除。

worker 超时预算为：默认 120 秒，每个代次首次附着 300 秒，编译、传输、导入导出、归档和库更新 30 分钟。`--worker-timeout-config` 或 `TIA_MCP_WORKER_TIMEOUTS` 设置分类预算，`--worker-timeout-seconds` 设置默认预算。派发前可取消；派发后等待真实结果，超时或通道故障不重放原生操作。

MCP 的 `WRITE` / `ONLINE-WRITE` 实际执行默认须经工作台审批。`dryRun=true` 和候选工具 `mode=preview` 不等待审批。`SaveProject`、`SaveProjectCopy` 和 `CloseProject` 实际执行同样须审批；连接、附着、打开和分离不审批。拒绝、120 秒默认超时或工作台不可用会在派发前返回 `CONFIRMATION_REQUIRED`。审批、执行与响应共享 requestId。

V20/V21 可用 `GetOpennessWorkerStatus` 查看代次与故障。`OUTCOME_UNKNOWN` 后先核对工程和调用日志，再用 `DisconnectPortal` 恢复，或显式调用 `RestartOpennessWorker`；重新连接后必须重新绑定工程。详见 [worker 与恢复](../guides/openness-worker-isolation.md)。
