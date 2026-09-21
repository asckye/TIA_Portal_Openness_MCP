# 换机器交接单（2026-09-21，2.7.53 本机构建、待推送发布与虚拟机部署；2.7.52 上在线族过了 TLS、卡在 F-CPU 安全设置）

[交接总页](handoff.md) · [文档目录](../README.md) · [真机台账](../reference/real-machine-ledger.md) · [v2.7.53 发布说明](../releases/v2.7.53.md)

这一页只回答"在另一台电脑上接着做，第一天要知道什么"。长期事实（每阶段固定动作、发布闸门、TIA 退出点、只在真机上学到的 API 事实）都在 [handoff.md](handoff.md)，不重复。

## 0. 一句话现状

- 仓库 `master` = `origin/master`，最后一次发布 **v2.7.51**（tag、`validate-bundle` / `offline-checks` / `Publish complete release` 全绿，ZIP `TIA_MCP_Delivery_v2.7.51_20260921.zip` 已上传）。448 个工具，离线 2197 项，形状 V20 2789 / V21 3077。
- 最后一次发布 **v2.7.52**；**2.7.53 已在本机 Build-Release 通过**（离线 2208，形状 V20 2805 / V21 3097，**450 个工具**），三段提交 + 推送 + tag 见 §5。虚拟机上跑的是 **2.7.52**；2.7.53 的 ZIP 待发布后部署。
- 真机台账：见 `docs/reference/real-machine-ledger.md` 头部计数；还剩 🔁 `DownloadToPlc` / `GoOnline` / `CompareSoftwareToOnline`——2.7.52 上 TLS 已过（`GetOnlineState` Incompatible），下载被 F-CPU V2.9 的安全设置挡住（访问级别 `NoAccess` 无完全访问密码、机密组态数据无密码），2.7.53 的 `ManagePlcProtection` 改它。
- 在线族：PG 侧（Softbus → "PLCSIM" 接口）与 TLS 信任（`trustDeviceCertificate`）都已解决；剩 CPU 保护设置（2.7.53 `ManagePlcProtection` + `CompileDevice` 核对）。

## 1. 新机器要准备的

| 项 | 怎么做 |
|---|---|
| 仓库 | `git clone` 后放两份 PublicAPI 副本（仓库根，`.gitignore` 已忽略；放别处也行——Build-Release 收绝对路径，desktop-ivrlcht 上是 `D:\Project\TIA\TIA_V20_PublicAPI` / `TIA_V21_PublicAPI`，与仓库同级）：`TIA_V20_PublicAPI\V20`（含 `Siemens.Engineering.dll`）与 `TIA_V21_PublicAPI\V21\net48`（含 `Siemens.Engineering.Base.dll` 等，**没有**单体 `Siemens.Engineering.dll`，handoff 里提到的 `V21\net48\Siemens.Engineering.xml` 就是 `Siemens.Engineering.Base.xml`）。没有这两份就不能编译、不能跑 Build-Release |
| 工具链 | .NET SDK 8 或 10、Python 3.10+（`json` / `gzip` 标准库即可）、Git（`Package-Release.py --git "<git.exe 完整路径>"`）。没有 `gh` 也行：CI 用 GitHub REST API 轮询，发布靠推注解 tag |
| 连虚拟机 | 把旧机器 `~/.claude.json` 里的 `tia-portal-vm` 条目（`url` + `headers.Authorization`）原样复制到新机器的 `~/.claude.json`。**地址随宿主机网络变**（handoff §5），以能通的为准；端口 8765，Bearer 鉴权。自检：`python scripts/diagnostics/Probe-McpServer.py tools` |
| 代理 | 宿主机若设了 `HTTP_PROXY`，Claude Code 的 MCP 客户端会把局域网请求送进代理，会话一开始就报 "tia-portal-vm CONNECT_TIMEOUT"——**这不是服务器坏了**，探针脚本与 `camp.py` 都绕过代理直连；把虚拟机地址加进 `NO_PROXY` 即可让 MCP 客户端也通 |
| Claude 记忆 | 记忆文件不随仓库走。旧机器记忆里、仓库里没有的几条已并入本页 §5；其余都在 handoff.md |

## 2. 虚拟机现在的样子（2026-09-21，2.7.52 在线族之后）

- TIA Portal V21 一个实例，打开着测试工程 **`项目1`**（`C:\Users\SIEMENS\Documents\Automation\项目1\项目1.ap21`，已按维护者同意保存）。**只开一个 TIA 实例**；每次写操作前看 `GetState.project`。绝不碰维护者的 `AutomaticDipCoatingMachine`。
- `项目1` 里引擎自建的东西：`MCP_PLC`（CPU 1515F-2 PN V2.9，X1 = 192.168.0.1 在子网 `MCP_PN`；块 MCP_G/MCP_FC/MCP_FB/MCP_FB_DB/MCP_DB，类型 MCP_T/MCP_UDT，变量表 MCP_Tags/MCP_Table，监控表在文件夹 **`MCP_W/MCP_WT`**（2 行：`"MCP_Start"` %I0.0 ModifyValue FALSE / Permanent，`%M0.0` TRUE / OnceOnlyAtStart——2.7.51 写入，**工程未保存**；桌面 `mcp50_wt.xml` 是 1 行时的导出），外部源 MCP_X，工艺对象文件夹 MCP_TO（当前为空），单元 MCP_Unit）、`MCP_TP700`（Comfort V17，800×480）、`MCP_UCP`（MTP700 Unified V21）、`MCP_S120`（V5.2 + 驱动轴_1）、项目库主副本、全局库 `MCP_GL`（桌面 `mcp46_gl`）。
- ~~根级五张空监控表 `MCP_WT_1` … `MCP_WT_5`~~ 已用 2.7.50 的 `ManagePlcTableEntries deleteTable` 删掉并保存（`GetPlcWatchTables` 只剩 `MCP_W/MCP_WT`）。
- `MCP_PLC` 的 CPU 保护：访问级别 **`NoAccess`**（TIA V21 新建 F-CPU V2.9 的默认）、无完全访问密码，机密 PLC 组态数据勾选但无密码——硬件编译 3 错，下载被拒；TIA 已在会话内信任 `MCP_SIM` 的证书（`GoOnline` 不再问）。
- PLCSIM Advanced 8.0：全局网络模式已改为 **Softbus**（`SimulationRuntimeManager.NetworkMode`，2.7.51 会话），实例 **`MCP_SIM`**（CPU1500_Unspecified）重新注册并 `powerOn`（Stop），`communicationInterface Softbus`、`controllerIP 192.168.0.1`（没下载过程序）；虚拟机再重启就要再注册（网络模式是否保留待观察）。TIA 路由树现在只有 PC 接口 "PLCSIM"；要回到虚拟网卡模式就 `register`/`powerOn` 时给 `communicationInterface:"TCPIPSingleAdapter"`（原值）。虚拟网卡本身仍无 IP。`ScanAccessibleDevices` 在该网卡上能按 MAC 看到（每次注册 MAC 会变：2.7.49 是 `02-C0-A8-00-F1-00`，2.7.50 重注册后 `02-C0-A8-00-C8-00`） "S7-1500 (PLCSIM)"。
- 接手先 `python scripts/diagnostics/Probe-McpServer.py tools` 确认能通、`Bootstrap` 看 `serverVersion`（部署 2.7.51 后应为 2.7.51.0）；引擎重启后要先 `Connect` 再 `AttachToOpenProject {projectName:"项目1"}`。
- 桌面上的产物：`mcp46_*` / `mcp47_*` / `mcp48_*` / `mcp49_*`（`mcp48_pid.xml` = PID_Compact 2.3 的 TO 导出，可再导入；`mcp47_projects\` 下有 SaveAs / Scaffold / Retrieve 出来的副本工程）。
- 已知的 TIA 退出点 ①–⑩（handoff §5）一个都别再碰；尤其 `TO_PositioningAxis 6.0`、Unified 面板 `/20.0.0.0`、与面板尺寸不符的经典画面、Safety Validation 条件级 `checkValidity`、无图表 PLC 上 `skipChartPreflight=true`。

## 3. 部署 2.7.53 后按顺序做（都用 `scripts/diagnostics/campaign`，见 §4）

先 `Connect`、`AttachToOpenProject {projectName:"项目1"}`，`GetState` 看 pid / project。（已做：监控表往返 ✅；PLCSIM Softbus ✅；TLS 信任 ✅（2.7.52，`GetOnlineState` Incompatible）；下载 ❌ 硬件编译 3 错 → 2.7.53 `ManagePlcProtection`。）

0. **CPU 保护**（2.7.53 新）：`ManagePlcProtection {devicePathJson:["MCP_PLC"], action:"read"}`（预期 `accessLevel NoAccess`、`masterSecret WithoutPassword`）→ `{…, action:"setAccessLevel", accessLevel:"FullAccessIncludingFailsafe", dryRun:false, confirmChange:true}` → `{…, action:"protectMasterSecret", password:"<测试密码>", dryRun:false, confirmChange:true}` → `CompileDevice {devicePathJson:["MCP_PLC"]}` 应 0 错（还剩 2 个警告无妨）。密码只在这个一次性测试工程里用，记进台账说明即可。
1. **监控表往返**（已通过，只在改了 XML 时重跑）：`SetWatchTableModifyValue {softwarePath:"MCP_PLC", tableName:"MCP_W/MCP_WT", address:"%M0.0", modifyValue:"TRUE", trigger:"OnceOnlyAtStart"}` 与 `address:"MCP_Start"`，看 `meta.readbackVerified` / `meta.after`（`ModifyIntention` 应由 TIA 置 true）；`ManagePlcTableEntries read` 核对行；再被拒就看 `meta.error` 原文——XML 在 `Siemens/WatchTableEntryXml.cs`（`ReadOnlyEntryAttributes` 列表可再加名字）。
2. **PLCSIM 网络模式**：`ReadPlcSimAdvancedInstances {includeState:true, memberFilter:"NetworkMode"}` 看 `api.networkMode` 与 `managerMembers`；`ManagePlcSimAdvancedInstance {instanceName:"MCP_SIM", action:"powerOff", dryRun:false, confirmInstanceChange:true}` → `unregister` → `register … cpuType:"CPU1500_Unspecified", communicationInterface:"Softbus"` → 看 `data.communicationInterfaceRoute`（`route` / `networkModeBefore` / `networkModeAfter`）与 `stateAfter.communicationInterface`；`powerOn`；`ReadTransferRoutes {softwarePath:"MCP_PLC"}` 看有没有 "PLCSIM" PC 接口。API 拒绝 `InstanceAlreadyRunning` 就先把所有实例 powerOff；`PCAPDriverNotRunning` 要维护者在虚拟机管理员命令行 `net start npcap`。
3. **在线族**（PG 侧已解除——第 2 步的 Softbus 让 TIA 出现了 "PLCSIM" 接口）：`python camp.py run plans/plan_online2.json`（先 `python plans/plan_online2.py` 生成 json；把里面 `ADP` 改成 `PLCSIM`）：`DownloadToPlc {softwarePath:"MCP_PLC", pgPcInterface:"PLCSIM", targetIpAddress:"192.168.0.1"}` → `GoOnline {ipAddress:"192.168.0.1", pgPcInterface:"PLCSIM"}` → `GetOnlineState` → `CompareSoftwareToOnline` → `ReadPlcSimAdvancedTags` / `WritePlcSimAdvancedTags` / `RunPlcSimAdvancedTestScenario`（有程序后才有标签）→ `UploadStationFromPlc {targetIpAddress:"192.168.0.1", dryRun:true}`（只收 IP，MAC 被 TIA 拒） → `GoOffline`。`ReadPlcBlockFingerprints` / `UploadDeviceParameters` 在 1515F-2 PN V2.9 上服务为 null，是 TIA 侧，不用再试。
4. 结果进台账：把每步的结论写进 `scripts/diagnostics/campaign/make_ledger.py` 末尾的 `o(...)` 覆盖行（工具名 状态 说明），`python make_ledger.py` 重生成 `docs/reference/real-machine-ledger.md`；handoff §1 加一条本版条目、§6 加"学到的事实"；有源码改动就走 §5 的发布流程出 2.7.52，没有就只提交文档。

## 4. 真机批跑工具（已入库：`scripts/diagnostics/campaign/`）

| 文件 | 用途 |
|---|---|
| `camp.py` | `call <Tool> '<json>' [metaKey,…|*]` 单调一次并打印 TIA 进程是否还活着；`raw` / `full` 看原文；`run plans/<plan>.json [起始步]` 批跑，逐步写 `ledger/<plan>.jsonl`，TIA 一死就停。lite 工具直接调，其余经 `CallTool` 桥接 |
| `rawmsg.py <Tool> '<json>'` | 同 `call` 但按 UTF-8 打印 message / meta——`.NET` 的中文异常文本经 `camp.py` 在 GBK 控制台是乱码，用这个看 |
| `plans/plan_*.py` | 各族测试计划（`python plans/plan_x.py` 生成同名 json）；`plan_online2.py` 是在线族的最新版 |
| `export_full.py <exportId> <out>` | 把 `GetExport` 分页拼成整份 |
| `make_ledger.py` | 从 `ledger-runs.jsonl.gz`（2026-09-20/21 全部批跑证据，993 行）+ `ledger/*.jsonl`（本机新跑的）+ 脚本内的 `o(...)` 人工裁定生成台账 md；改完裁定直接重跑即可，输出与提交的文件逐字节一致 |
| `vm_ledger.json` | 2.7.39–2.7.45 会话里跑过的工具名（状态"早期真机"的依据）；`vm_ledger.py` 是从旧机器的会话记录里提取它的脚本，换机器后不必再跑 |

Bash 里给 JSON 参数带引号很容易被 shell 吃掉——复杂参数写成 plan 文件跑，或用 `rawmsg.py`。

## 5. 发布流程里、只有旧机器记忆知道的坑（handoff §3 / §4 是主流程）

- **版本号要改 8 处**：`.claude-plugin/plugin.json`、`tools/mcp-configurator/Configurator.cs`（两行）、`TiaMcpServer.V20.csproj` / `V21.csproj`（各三行）、`docs/README.md` 的"当前发布说明"链接、`docs/reference/capabilities.md` 首段、`docs/development/roadmap.md` 标题与 §5；再加 `CHANGELOG.md` 顶部条目 + `docs/releases/vX.md`（`Validate-Bundle -Strict` 要求 CHANGELOG 最新条目 = csproj 的 InformationalVersion）。上一轮的 `docs_2750.py` 就是这套改动的模板（在旧机器 scratchpad，未入库；照 §5 这张单子做即可）。
- **`Build-Release.ps1` 要在 PowerShell 里跑**（`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build/Build-Release.ps1 -V20ReferenceRoot <绝对路径> -V21ReferenceRoot <绝对路径\net48> -Python <python.exe 绝对路径> -ReleaseDate YYYYMMDD *> build.log`）。在 Git Bash 里 `*>` 不是重定向而是通配符（会把 `LICENSE` 当成参数塞给 `-ReleaseDate`），反斜杠路径也会被改写。约 4–5 分钟；**跑的时候不要改 `src` / `tests` 里任何文件**，它在最后一步哈希源码。日志是 UTF-16。
- Build-Release 之后**不要**再跑 `Build-Configurator.ps1 -Test`（会让 `Package-Release.py` 报 "Configurator build record changed"）。
- `Check-DeadToolReferences.py` 会把 `[Description]` 文案里任何 `动词+大写` 的词当成工具名：描述里点名 Openness 成员（`ApplyConfiguration`、`ImportOptions` 之类）要么换说法，要么加进脚本 `ALLOWED` 并写明理由。
- 提交模式：`Release X (1/3)` 源码 + 版本 + 文档；`(2/3)` `runtime/v20/TiaMcpServer.exe`；`(3/3)` `runtime/v21/TiaMcpServer.exe` + `TiaMcpConfigurator.exe` + `manifest/*` + `docs/reference/tool-matrix.md`。然后 `python scripts/build/Package-Release.py --git "<git.exe>"` 本地干跑（要求工作区干净），`git rev-list --reverse --first-parent origin/master..master` 逐个 `git -c http.postBuffer=157286400 push origin <sha>:refs/heads/master`（一次推 30 MB 曾 408），CI 用 `https://api.github.com/repos/asckye/TIA_Portal_Openness_MCP/actions/runs?head_sha=<完整 40 位 sha>` 轮询（短 sha 查不到），两绿后 `git tag -a vX -m "…"` + `git push origin vX`，等 "Publish complete release" 成功、发布页出现 ZIP，再在 handoff §1 把"本机构建"改成"已发布"提交。
- 提交信息、tag、Release 正文**不加任何 AI 署名行**；不要 `git add -A`（仓库根可能有本地大压缩包）。
- 含反斜杠 / 引号的补丁脚本别经 Bash heredoc 传（转义会丢），先用编辑器写到临时文件再执行；文档补丁一律 `assert s.count(old)==1` 再替换。

## 6. 维护者定下的规则（不变）

只在 `项目1` 里用引擎自建的临时设备做硬件 / 驱动 / 在线测试；在线族只对 PLCSIM Advanced 虚拟 PLC，绝不对真实 CPU 上线 / 下载 / 写值；先 `dryRun` 再真跑；一次只开一个 TIA 实例；TIA 退出后重启并重开工程即可（`项目1` 已保存）；每个版本都要有真机回归结论写进台账；碰到"AI 调工具反复格式错误"之类的体验问题，优先在桥接层修（2.7.47 的宽松绑定就是这样来的）。
