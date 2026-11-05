# 安全标准与威胁模型

状态：现行 · 最后核对：2026-11-01，master `902a86fe`

[文档目录](../README.md) · [设计规范](design.md) · [开发规范](development.md) · [漏洞报告方式](../../.github/SECURITY.md)

本文记录自动化工程工作台的资产、信任边界、STRIDE 威胁和现有防护措施，供维护者、实现代理和审查者使用。漏洞的报告方式和受理范围只在 [`.github/SECURITY.md`](../../.github/SECURITY.md) 中维护，本文不重复。

本文只写代码中已有的行为，并为每条措施给出实现文件和测试。已决定但尚未实现的事项单独列在第 6 节。

## 1 范围与威胁模型的前提

- 产品在一台 Windows 电脑上运行：FoundationHost 为每个 MCP 会话启动一个 PLC worker，worker 经 Siemens Openness 操作 TIA Portal；工作台在同一台电脑上审批写调用。虚拟机部署时，AI 客户端可以在宿主机上，经 HTTP 连接虚拟机中的 FoundationHost。
- 信任边界是“同一 Windows 用户”。三条本机管道防止 MCP 客户端经工具参数、元数据或批次越权（例如自己批准写调用、关闭审批），不防同一用户下已经能执行代码的本机进程（[设计规范](design.md) 第 2.5 节）。
- 主要威胁是“AI 经 MCP 工具越权写入”：模型可能误解提示，也可能受到项目内容、文件或网页中注入文本的影响。
- 不在范围内：已经取得本机管理员权限的攻击者、TIA Portal、Openness 和 Windows 本身的漏洞、对生产设备执行下载这一运营决定（见 [`.github/SECURITY.md`](../../.github/SECURITY.md)）。

## 2 资产

| 资产 | 位置 | 受损后果 |
|---|---|---|
| 用户的 TIA 项目与 PLC 程序 | TIA Portal 中打开的项目、`.ap<主版本>` 文件 | 程序被改错或丢失；客户知识产权泄露 |
| 在线 PLC 状态 | 经在线通道连接的 CPU、PLCSIM | 设备行为改变，可能影响现场安全 |
| 凭据 | UMAC、PLC 保护、Web 服务器等工具参数中的密码；HTTP 访问密钥 | 设备或服务被他人访问 |
| 审批与审计记录 | `<数据根>/config/approval.settings`、`<数据根>/logs/audit/` | 写调用绕过人工审批，或事后无法追查 |
| 调用日志与诊断 | `<数据根>/diagnostics/`、`<数据根>/logs/` | 凭据或项目内容泄露 |
| Openness 用户组带来的本机权限 | Windows 组 “Siemens TIA Openness” | 组成员可经 Openness 操作本机全部 TIA 实例 |
| 交付包与更新 | GitHub Releases 的 ZIP 与 `.sha256`、`runtime/tools/TiaMcp.Updater.exe` | 被替换的二进制在用户电脑上执行 |

## 3 信任边界

| 边界 | 穿过边界的内容 | 主要控制 | 代码 |
|---|---|---|---|
| MCP 客户端与模型 → FoundationHost | 工具调用、参数、批次 | V4 准入与类型化参数；写调用审批；预览不执行 | `src/Logic/V4/`、`src/FoundationHost/FoundationTools.cs` |
| 项目内容、导入文件 → 模型 | 块注释、变量名、导出的 XML 与源 | 作为数据返回，不作为指令执行；写入仍须审批 | 同上 |
| 宿主机 → 虚拟机中的 HTTP 端点 | MCP over HTTP | Bearer 访问密钥（定长比较）；绑定的 IP 字面量；防火墙规则只开放本地子网 | `src/FoundationHost/EngineReleaseHost.cs`、`src/FoundationHost/HostOptions.cs`、`src/Shared/WindowsFirewallRule.cs` |
| FoundationHost ↔ 工作台审批管道 | 待批请求与决定 | 受保护 DACL、拒绝远程客户端、双方核对 SID；宿主生成 nonce；重放请求被拒绝 | `src/Shared/LocalPipeSecurity.cs`、`src/Shared/ApprovalPipe.cs` |
| AI → 工作台控制通道 | 显示、读取、预填 | 同上，另核对工作台映像路径；协议中没有审批操作 | `src/Shared/WorkbenchControlPipe.cs`、`src/Shared/WorkbenchControlProtocol.cs`、`src/Studio/Gui/WorkbenchControl.cs` |
| 工作台 ↔ MCP 会话（操作端管道） | 项目页发起的调用 | 同上，另核对映像路径；发起的写调用同样须审批 | `src/Shared/OperatorPipe.cs` |
| FoundationHost ↔ PLC worker | JSON-RPC 帧（STDIO） | 启动前计算 worker 与适配器的 SHA-256，`hello` 帧必须回显协议、发布键、两个哈希、PID 和 32 字节 nonce；作业对象在宿主退出时结束 worker | `src/FoundationHost/WorkerClient.cs`、`src/WorkerChannel/ChannelCodec.cs`、`src/FoundationHost/WorkerJob.cs` |
| 标准包 zip → 生成框架 | 包清单、分部文件 | 只在内存中读取；路径、数量与大小限制；逐个按 JSON Schema 校验；不执行包内代码 | `src/Logic/Generation/StandardPackageLoader.cs`、`reference/schemas/standards/v1/README.md` |
| 宿主机上的 AI → 导入暂存 | 文本源文件 | 文件数、批次数与大小上限；文件名与扩展名规则；拒绝重解析点；XML 禁用 DTD | `src/Logic/ModelContextProtocol/ImportStagingStore.cs` |
| FoundationHost → 生态工具子进程 | Python 桥接与参数 | 不经 shell 启动，参数经 stdin 以 JSON 传递；超时与输出上限；只允许固定的命令组；分类为 ONLINE-WRITE，默认 `dryRun` | `src/Shared/Host/EcosystemTools.cs`、`src/Shared/LocalProcess.cs`、`scripts/ecosystem/plc_tools_bridge.py` |
| GitHub Releases → 更新器 | 交付 ZIP | 与同一发布的 `.sha256` 比较；解压路径检查；包内发布号必须等于标签 | `src/Updater/Updater.cs` |
| Claude Code → WriteGuard 钩子 | 工具调用前的检查 | 按 `manifest/tools-list.json` 分类拒绝非预览的 ONLINE-WRITE 和未知工具 | `src/Tools/WriteGuard/Program.cs`、`hooks/` |

## 4 STRIDE 威胁

| 类别 | 威胁 | 对应措施 |
|---|---|---|
| 仿冒（Spoofing） | 其他进程冒充工作台批准写调用 | SEC-01、SEC-02 |
| 仿冒 | 被替换的 worker 或适配器接入宿主 | SEC-07 |
| 仿冒 | 局域网中的其他电脑调用 HTTP 端点 | SEC-05 |
| 篡改（Tampering） | 模型或注入文本让写调用绕过审批，或改写 `approval.settings` | SEC-01、SEC-03 |
| 篡改 | 审计记录被修改、删除或插入 | SEC-04 |
| 篡改 | 恶意标准包、导入文件或 MCP 输出写到预期目录之外 | SEC-01、SEC-08、SEC-09 |
| 信息泄露 | MCP 输出经 UNC 写到远程主机 | SEC-01；路径查询的剩余风险见第 6 节 |
| 篡改 | 被替换的交付包经更新器安装 | SEC-11 |
| 否认（Repudiation） | 无法证明某次写调用由谁批准、何时执行 | SEC-04 |
| 信息泄露（Information disclosure） | 密码、令牌出现在审批摘要、计划哈希、审计、调用日志或响应中 | SEC-06 |
| 信息泄露 | 堆栈、本机路径出现在 MCP 响应中 | SEC-06 |
| 拒绝服务（Denial of service） | 超大的包、批次或暂存文件耗尽内存或磁盘 | SEC-08、SEC-09 |
| 拒绝服务 | worker 挂起或崩溃后会话一直不可用 | SEC-12 |
| 权限提升（Elevation of privilege） | 控制通道或项目页绕过审批 | SEC-02 |
| 权限提升 | 生态工具的参数被解释为 shell 命令 | SEC-10 |
| 权限提升 | 结果未知的写调用被自动重放，造成重复写入 | SEC-12 |
| 权限提升 | 在线写操作在客户端侧未被拦截 | SEC-13 |

## 5 措施

每条措施列出实现与测试。修改这些代码时，必须保持措施成立，并运行对应测试。

### SEC-01 写调用默认须在工作台审批

- 分类为 `WRITE`、`ONLINE-WRITE` 的实际执行，以及 `SaveProject`、`SaveProjectCopy`、`CloseProject`、`StageImportFiles`、`CleanupStagedImportFiles`，在派发前等待工作台审批。预览（`dryRun: true`、`mode: preview`）不审批（[设计规范](design.md) 第 4.10 节）。
- MCP 向调用方指定的输出根之外写文件也须审批，包含 UNC 目标；根内路径若含重解析点仍按根外处理。输出根为 `<包根>/exports`（checkout 为 `bin-build/exports`）、显式数据目录下的 `exports`，或 `%LOCALAPPDATA%/TiaMcp/exports`；无法创建时所有目标均按根外处理。预览、产品自行选择的输出位置、工作台操作端和已批准的生成计划步骤沿用原行为；空参数由其他调用方输入派生位置时仍检查实际目录。MCP 的 Git stage/commit 无论目录是否在根内均须审批。通过 `GetSessionState data.outputRoot` 查询实际目录。审批卡片对每个文件系统路径分别展示并绑定 `Path.GetFullPath` 规范化后的完整路径，路径不做凭据片段脱敏；其他参数继续脱敏。所有这些路径（包括根内目标、WRITE 和 SESSION 工具路径）带首尾空白时，在发卡前返回 `INVALID_ARGUMENT`。长路径在审批卡片中换行展示。须审批的规范化目标超过 4096 个字符时，在发卡前返回 `INVALID_ARGUMENT`。`BuildReleaseHandoffArtifacts` 的空 `outputDirectory` 取输入报告所在目录，也须检查。
- 审批默认开启；`approval.settings` 缺失或格式无效时按开启处理。开关变化写入审计。工具不能写 `approval.settings`，包括经短文件名、链接或文件 ID 的写法。
- 宿主生成请求 nonce，MCP 参数和元数据都不能充当审批令牌；重复的请求号被拒绝；迟到、不匹配或格式错误的决定一律按拒绝处理。
- 实现：`src/Shared/ApprovalSettings.cs`、`src/Shared/ApprovalPipe.cs`、`src/Shared/ApprovalProtocol.cs`、`src/FoundationHost/FoundationTools.cs`。
- 测试：`tests/FoundationHost/TiaMcp.FoundationHost.Tests/OutputRootApprovalTests.cs`、`tests/KeptCode/TiaMcp.KeptCode.Tests/CallerInputFilesTests.cs`、`tests/FoundationHost/TiaMcp.FoundationHost.Tests/ApprovalHostTests.cs`、`tests/KeptCode/TiaMcp.KeptCode.Tests/ApprovalTests.cs`。

### SEC-02 控制通道和操作端不能审批

- 工作台控制协议只有显示、读取和预填操作，没有审批操作。审批或设置抽屉打开时，控制请求被拒绝；控制通道不能显示待批调用的详情。
- 项目页经操作端管道发起的写调用走同一审批路径。
- 实现：`src/Shared/WorkbenchControlProtocol.cs`、`src/Studio/Gui/WorkbenchControl.cs`、`src/Shared/OperatorPipe.cs`。
- 测试：`tests/Studio/TiaOpenness.Gui.Tests/WorkbenchControlPipeTests.cs`、`tests/Shared/TiaMcp.WorkbenchControl.Tests/PipeSecurityTests.cs`。

### SEC-03 本机管道只接受当前用户

- 三条管道的名称由 SHA-256 派生，绑定当前用户 SID 和对应路径。安全描述符是只授予当前用户的受保护 DACL，管道模式拒绝远程客户端。服务端核对对方 SID，客户端核对服务端 SID；控制通道和操作端管道还核对对方的映像路径。
- 实现：`src/Shared/LocalPipeSecurity.cs`。
- 测试：`tests/Shared/TiaMcp.WorkbenchControl.Tests/PipeSecurityTests.cs`；`tests/test-suites.json` 中 `operator-host` 套件在 Linux 与 macOS 上跳过的用例只覆盖 Windows 内核管道身份。

### SEC-04 审计哈希链

- 审批和写调用记录在 `<数据根>/logs/audit/audit-<序号>.jsonl`，按 10 MiB 轮转，不自动删除。每条记录带上一条规范化记录的 SHA-256；`tia audit` 校验序号连续和哈希一致。
- 限制：有效的链只能发现保留记录的篡改、缺失和插入，不能证明日志完整。
- 实现：`src/Shared/AuditLog.cs`、`src/Shared/AuditCli.cs`。
- 测试：`tests/FoundationHost/TiaMcp.Engine.Diagnostics.Tests/AuditLogTests.cs`、`tests/FoundationHost/TiaMcp.FoundationHost.Tests/SessionAuditHostTests.cs`。

### SEC-05 HTTP 传输

- 默认前缀是 `http://127.0.0.1:8735/`。HTTP 传输要求前缀是明确的 IP 字面量，并从工作台的当前用户 DPAPI 保护存储读取非空且不含控制字符的密钥。`--http-api-key` 仅兼容一个发布周期并输出不含密钥的弃用提示；工作台生成的命令不再包含它。
- 所有请求（包括健康检查）在鉴权前校验 Host 和可选 Origin。允许配置端点、同端口回环源与 `--http-allowed-origins` 明确列出的源；其他值返回 403 并记录不含原始头值的审计事件，防止 DNS rebinding。
- 除 `/mcp/health` 外，所有请求必须带 `Authorization: Bearer <密钥>`，用定长比较核对，不符返回 401。`/mcp/health` 不需要密钥，只返回版本、发布键和档位。
- 工作台生成 24 字节随机密钥，以 DPAPI（当前用户）保存。工作台只接受 IPv4 字面量，拒绝 `0.0.0.0` 和广播地址，允许局域网地址。配置网络时，以提升权限添加只授予当前用户的 URL ACL，并添加只允许本地子网的入站防火墙规则。
- 远程客户端使用随包 stdio 中继，命令和客户端配置不含密钥；中继从同一用户的数据目录读取按端点绑定的保护记录。私网、回环和链路本地地址绕过代理，其他地址遵守环境/系统代理及 NO_PROXY。不跟随重定向，不记录 HTTP 异常正文或凭据，不自动重放请求。
- 实现：`src/FoundationHost/HostOptions.cs`、`EngineReleaseHost.cs`、`HttpRequestBoundary.cs`、`StdioHttpRelay.cs`、`src/Shared/HttpAccessKeyStore.cs`、`RemoteProxyPolicy.cs`、`src/Studio/Gui/Configuration/ConfigCore.cs`、`src/Shared/WindowsFirewallRule.cs`。
- 测试：`tests/FoundationHost/TiaMcp.FoundationHost.Tests/EngineHttpSessionTests.cs`、`HttpHostOptionsTests.cs`、`HttpRequestBoundaryTests.cs`、`HttpRelayTests.cs`、`tests/Studio/TiaOpenness.Gui.Tests/RemoteConfigurationTests.cs`、`tests/Studio/TiaOpenness.Core.Tests/WindowsFirewallRuleTests.cs`。

### SEC-06 敏感值与诊断信息不外泄

- 键名含 password、passwd、secret、token、credential、authorization，或以 key、keys 结尾（`releaseKey` 除外），或等于 `pwd`、`sk` 的参数值被替换为占位符；同级 `name`/`key` 为敏感名时，`value` 也被替换。自由文本中的 Bearer/Basic 令牌、`name=value` 形式的密钥、URL 用户信息和 PEM 私钥被清除。
- 审批摘要、计划哈希和审计记录在计算哈希前先做同样的替换，因此敏感值不进入摘要；应用时可以使用与预览不同的密码。
- V4 信封的 `details` 和 `data` 不含堆栈与凭据：`Clean()` 删除键名含 `stack` 或等于 `password` 的字段，把多行堆栈文本替换为固定占位；`HostBehavior.SafeDiagnostic` 把盘符和 UNC 路径替换为 `<path>`。
- 限制：替换按参数名匹配。UMAC、PLC 保护和 Web 服务器凭据只有在参数名符合上述规则时才被替换，没有单独的名单。
- 实现：`src/Shared/CallJournalPayload.cs`、`src/Shared/ApprovalProtocol.cs`、`src/Shared/PlcToolContract.cs`、`src/Logic/V4/HostBehavior.cs`。
- 测试：`tests/FoundationHost/TiaMcp.FoundationHost.Tests/SessionAuditHostTests.cs`。

### SEC-07 worker 身份核对

- 宿主不经 shell 启动 PLC worker，经 STDIO 交换 JSON-RPC 帧。启动前计算 worker 可执行文件和适配器的 SHA-256 并生成 32 字节 nonce；worker 的第一帧必须是回显协议版本、发布键、两个哈希、PID 和 nonce 的 `hello`，否则宿主拒绝。作业对象保证宿主退出时 worker 随之结束。
- 实现：`src/FoundationHost/WorkerClient.cs`、`src/WorkerChannel/ChannelCodec.cs`、`src/WorkerChannel/ChannelMessage.cs`、`src/FoundationHost/WorkerJob.cs`。
- 测试：`tests/WorkerChannel/TiaMcp.WorkerChannel.Tests/ProtocolTests.cs`、`ProcessTests.cs`（注册套件 `worker-channel`）。

### SEC-08 标准包按数据处理

- 标准包 zip 只在内存中读取，不解压到磁盘。条目路径拒绝 `..`、`.`、空段、Windows 保留字符与保留设备名、控制字符、超长名称、过深路径和非 NFC 名称；拒绝链接和特殊条目；名称忽略大小写后必须唯一。文件数、条目数、单文件、总量和压缩包大小都有上限，解压大小按受限读取核对。每个分部文件按 JSON Schema 校验，schema 不能加载远程引用。加载过程不执行、不导入包内的 SCL、库或文档文件。
- 实现：`src/Logic/Generation/StandardPackageLoader.cs`、`src/Logic/Generation/StandardPackageStore.cs`。
- 测试：`tests/Generation/TiaMcp.Generation.Tests/StandardPackageStoreTests.cs`、`GenerationTests.cs`。

### SEC-09 导入暂存受限

- 暂存根目录是 `<包根>/staging/<会话>/`。文件数、批次数、单文件和每批次大小都有上限；文件名不能含分隔符、`..` 或保留名，扩展名必须与文件类别一致；路径中任何一级是重解析点时拒绝；XML 禁用 DTD 且不使用解析器；源文件必须是 ASCII。
- 实现：`src/Logic/ModelContextProtocol/ImportStagingStore.cs`。
- 测试：`tests/KeptCode/TiaMcp.KeptCode.Tests/ImportStagingTests.cs`、`tests/FoundationHost/TiaMcp.FoundationHost.Tests/ImportStagingHostTests.cs`。

### SEC-10 子进程不经 shell

- 生态工具以 `python -I -X utf8` 运行桥接脚本，参数经 stdin 以 JSON 传递；`LocalProcess` 不使用 shell，限制超时、输出大小和参数数量，并要求绝对工作目录。桥接脚本只接受固定的命令组。该工具分类为 ONLINE-WRITE，默认 `dryRun`。
- Openness 用户组修复以 `runas` 运行 `net.exe localgroup`，不经 shell，并拒绝含引号和控制字符的账户名（[ADR 0017](decisions/0017-own-openness-group-code.md)）。
- 实现：`src/Shared/Host/EcosystemTools.cs`、`src/Shared/LocalProcess.cs`、`scripts/ecosystem/plc_tools_bridge.py`、`src/Shared/WindowsOpennessGroup.cs`。
- 测试：`tests/FoundationHost/TiaMcp.Engine.Tests/McpLocalProcessTests.cs`、`tests/FoundationHost/TiaMcp.Engine.DiagnosticMembership.Tests/WindowsOpennessGroupTests.cs`。

### SEC-11 更新器核对完整性

- 更新器从本仓库的 GitHub Releases 获取交付 ZIP，与同一发布的 `.sha256` 比较；解压时核对每个条目都是安全的相对路径；包内交付清单的发布号必须等于标签。
- 限制：只核对完整性，没有签名或 Authenticode 校验；ZIP 与 `.sha256` 来自同一来源。
- 实现：`src/Updater/Updater.cs`。
- 测试：`tests/Updater/TiaMcp.Updater.Tests/UpdaterTests.cs`。

### SEC-12 结果未知时不重放

- 写入、传输或 worker 通道中断时返回 `OUTCOME_UNKNOWN`，会话按需要锁定，恢复前拒绝后续原生调用；宿主和客户端都不得自动重放写调用（[ADR 0008](decisions/0008-worker-timeouts-and-faults.md)，[设计规范](design.md) 第 4.11 节）。TIA 进程租约把一个 TIA 进程保留给一个 MCP 会话（[ADR 0011](decisions/0011-tia-process-lease-on-all-releases.md)）。
- 实现：`src/Logic/V4/HostBehavior.cs`、`src/Logic/ModelContextProtocol/RecoveryHints.cs`、`src/PlcWorker/FoundationProcessLease.cs`。
- 测试：`tests/FoundationHost/TiaMcp.FoundationHost.Tests/FoundationProcessLeaseTests.cs`；注册套件 `worker-robustness`。

### SEC-13 客户端侧的在线写入防护

- 随插件分发的 WriteGuard 是 Claude Code 的 PreToolUse 钩子：按 `manifest/tools-list.json` 分类，拒绝非预览的 ONLINE-WRITE 和未知工具，也检查 `CallTool`、预览和批次中的目标；设置 `TIA_MCP_ALLOW_ONLINE_WRITE=1` 才放行在线写入。非读取调用记录到本机日志，敏感键被替换。
- 限制：这是客户端侧的附加防线，`TIA_MCP_WRITE_GUARD=0` 可以关闭它；服务端审批（SEC-01）不依赖它。
- 实现：`src/Tools/WriteGuard/Program.cs`。
- 测试：`tests/Tools/TiaMcp.ShippedTools.Tests/WriteGuardPortTests.cs`（注册套件 `write-guard`）。

## 6 已知缺口与已决定未实现的事项

| 项目 | 现状 | 处理 |
|---|---|---|
| HTTP 请求的 Origin 与 Host 头 | RELAY-1 已实现每请求校验、403 与审计，含伪造头测试 | 已补齐；新增可信源须显式配置 |
| HTTP 访问密钥在命令行和客户端配置中是明文 | RELAY-1 的宿主与中继读取 DPAPI 存储；新配置仅含 stdio 中继命令 | 新路径已补齐；弃用的 `--http-api-key` 在下一发布周期移除，旧备份仍可能包含明文 |
| UNC 路径查询（S8 剩余部分） | 根外写入已由 SEC-01 审批；输入、预览和审批前的身份检查仍可能查询远程主机，暴露 Windows NTLM 凭据，尚未写出项目内容 | 是否在准入层拒绝 UNC 另行决定；须兼顾已文档化的 UNC 输入和共享项目 |
| `BuildDeviceAmlDocument` 分类 | 按文件目标审批，但仍为 OFFLINE / readOnlyHint=true | 单独重分类并重捕获八版基线，本任务不改契约 |
| 更新包没有签名 | 只有同源 `.sha256` | 未决定 |
| 凭据按参数名替换 | 没有 UMAC、PLC 保护、Web 服务器凭据的专用名单 | 新增带凭据参数的工具时按 SEC-06 的命名规则命名，并在测试中证明被替换 |
| 原生失败的诊断信息 | `NATIVE_OPERATION_FAILED` 的 `nativeMessage` 多数为空 | 维护者 2026-11-01 决定返回去敏后的原生消息首行、原生异常类型、HResult 和关联日志的诊断编号，堆栈只进日志（DES-4，尚未实现；见[设计规范](design.md)第 6.5 节） |
| EplanMCP 的安全借鉴项 | 虚拟机 HTTP 加固、“项目内容是数据”的服务器说明、审批卡清理等 | P8-95a–g，尚未实施 |

## 7 开发中的安全要求

- 新增写工具必须登记显式分类（`ToolMetadata` 或 `[ToolClassification]`）；会写入的动作必须经 SEC-01 的审批路径，不得新增绕过审批的入口。
- 带 `dryRun` 的工具，预览路径不得写项目、文件或在线设备。
- 新增带凭据的参数时，参数名必须能被 SEC-06 识别，并加测试证明值不进入审批摘要、审计、调用日志和响应。
- 新增读取文件或压缩包的入口时，按 SEC-08、SEC-09 的做法限制路径、数量和大小，并测试越界输入被拒绝。
- 启动子进程只用参数列表或 stdin，不拼接 shell 命令行。
- 新的本机管道使用 `LocalPipeSecurity` 创建并核对对方身份。
- 不在日志、文档、测试夹具和提交中写真实凭据、机器地址、客户项目或未脱敏的截图。
- 删除或放宽上述测试与检查须经维护者批准（[开发规范](development.md) 第 5.8 节）。
