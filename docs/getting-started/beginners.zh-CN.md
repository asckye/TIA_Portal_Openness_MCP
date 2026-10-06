# 新手使用指南

适用版本：当前源码构建的统一工作台（尚未正式发布；v3.2.0 ZIP 仍使用独立配置器与 Studio）；TIA Portal V14 SP1、V15.1、V16、V17、V18、V19、V20、V21。

第一次使用，按本文完成“打开软件 → 连接已有练习工程 → 查看程序块 → 导入一个小函数 → 查看编译结果”。这套练习只涉及电脑上的工程，不需要连接或下载到 PLC。

文中“虚拟机文件路径”说明导入文件要放在哪台电脑；“选择 PLC”说明工程中要操作哪一台控制器；“单独保存工程”说明导入和编译后还要保存修改。这些是使用现有功能的步骤。

## 1. 先选一种使用方式

| 想做什么 | 打开什么 | 是否需要 AI 客户端 |
|---|---|---|
| 用中文告诉 AI，让它查看、修改 TIA 工程 | 根目录 `TiaOpenness.exe`，完成配置后在 AI 客户端中操作 | 需要支持 MCP 的客户端 |
| 自己点击界面，浏览、导出、导入、编译 | 同一个 `TiaOpenness.exe` 的 **工程操作** 页面 | 不需要，直接调用 Openness |
| 暂时没有安装 TIA，先熟悉 Studio 页面 | 使用下面的演示命令 | 不需要 |

演示：在完整解压的交付包文件夹中打开 PowerShell，运行：

```powershell
.\TiaOpenness.exe --mock --lang zh
```

演示数据是模拟数据，连接、导入和编译结果不能用于判断真实工程是否成功。正式操作时，关闭演示窗口，再打开工作台的 **工程操作** 页面。

## 2. 下载、解压和准备环境

1. 当前统一工作台使用源码构建产物，构建方式见[验证流程](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/validation.md)。[已发布 v3.2.0](https://github.com/asckye/TIA_Portal_Openness_MCP/releases/tag/v3.2.0) 仍是合并前界面；该 ZIP 不包含本页新增的顶部页面切换。后续正式包仍应完整下载 ZIP 和同名 `.sha256`，GitHub 的 `Source code` 不含运行二进制。
2. 完整解压，建议放在固定位置，例如 `D:\TIA-MCP`。打开后应能看见 `TiaOpenness.exe`、`runtime`、`docs`、`reference` 和 `manifest`。如果外面还有一层同名文件夹，继续进入这一层；下文的“包根目录”指能看见这些内容的文件夹。
3. 在安装 TIA 的电脑上检查下面的依赖。不要只复制引擎或 Studio 的 EXE，它们需要同目录下的 DLL、适配器和示例文件。
4. 用 TIA 打开一个练习工程，确认 TIA 自己能正常查看和编译。首次练习建议使用工程副本，并只打开这一份目标工程。

| 使用内容 | 需要提前安装 |
|---|---|
| 所有真实工程操作 | 对应版本的 TIA Portal、TIA Openness，以及工程所需的 Siemens 产品和许可证 |
| Openness 桥接进程、工作进程、V20/V21 MCP 引擎 | .NET Framework 4.8（Windows 10 1903 及以后已自带，更早的系统需安装） |
| V14 SP1–V19 MCP 引擎、统一工作台（包含配置页面） | 无需安装：.NET 10 运行时已随包附带（`runtime/dotnet`）；实际工程操作还需要对应 TIA/Openness |

如需检查下载完整性，在 ZIP 所在文件夹运行下面的命令，把 `Hash` 与 `.sha256` 文件中的哈希值比较：

```powershell
Get-FileHash .\TIA_MCP_Delivery_v3.2.0_20261003.zip -Algorithm SHA256
Get-Content .\TIA_MCP_Delivery_v3.2.0_20261003.sha256
```

### 让当前 Windows 用户能够使用 Openness

在安装 TIA 的电脑上，管理员打开“计算机管理 → 本地用户和组 → 组 → Siemens TIA Openness”，把实际运行 TIA 和本程序的 Windows 用户加入该组，然后注销 Windows 并重新登录。首次连接 TIA 时，按 TIA 的 Openness 访问提示允许本程序访问。只有更改用户组等系统设置时才需要管理员权限。

这是 Siemens 的安装要求；详细操作见[官方用户组说明](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/basics/installation/adding-users-to-the-siemens-tia-openness-user-group)。

### 版本到底选哪个

选择这次实际运行的 TIA 版本。V14 必须是 **V14 SP1**，V15 必须是 **V15.1**。版本选择用于加载对应 API，不会把旧工程自动转换为新工程。需要升级工程时，先在对应 TIA 中完成转换，再连接转换后的工程。

V14 SP1–V19 提供 PLC 基础工具；V20/V21 提供完整引擎，包含更多 HMI、驱动及选件相关工具。安装多个版本时，一次会话使用一个准确版本。完整清单见[版本工具说明](../reference/version-tools.md)。

## 3. 让 AI 连接 TIA

### 情况 A：TIA 和 AI 在同一台电脑

1. 双击根目录 `TiaOpenness.exe`。需要中文时，在窗口顶部切换语言。
2. 顶部选择 **同一台电脑**。
3. 选择 TIA 版本，点击 **自动检测**。没找到时，点击 **浏览**，选择 TIA 安装根目录，例如 `C:\Program Files\Siemens\Automation\Portal V21`，不要选它下面的 `Bin`。
4. 在 AI 客户端卡片中，选择自己实际安装、准备使用的客户端。点击所选客户端的说明，确认卡片对应的软件；模型品牌名称不一定就是聊天网页或桌面 App。
5. 完全退出该 AI 客户端，点击 **写入客户端配置**，查看写入结果。
6. 重新启动 AI 客户端，新建会话。MCP 服务名是 `tia-portal`。客户端会自动启动对应引擎，本机模式不需要填写 IP、密钥或手动点击启动服务。

### 情况 B：TIA 在虚拟机，AI 在宿主机

**先在装有 TIA 的虚拟机上：**

1. 完整解压交付包，打开配置器，选择 **虚拟机 ↔ 宿主机**。
2. 选择 TIA 版本及安装目录，填写虚拟机实际 IPv4 地址和端口，默认端口为 `8765`。
3. 点击密钥条上的 **生成**，再点击 **AI 客户端** 卡片右上角的 **保存两端配置**。
4. 点击 **网络权限**，完成 Windows 管理员授权，再点击 **启动服务**。
5. 确认活动日志出现监听信息、连接摘要显示“运行中”，保持配置器窗口打开。

**再在运行 AI 的宿主机上：**

1. 在宿主机完整解压交付目录后打开 `TiaOpenness.exe`，通过 **视图 → MCP 与客户端**（Ctrl+2）进入配置页；不能只复制入口 EXE。此处使用包内附带的 .NET 10 运行时，不需要 TIA。
2. 同样选择 **虚拟机 ↔ 宿主机**，填写虚拟机的地址、端口和刚才生成的同一把密钥。这里填的是虚拟机地址，不是宿主机地址或 `localhost`。
3. 选择 AI 客户端，点击 **测试连接**。成功表示网络、鉴权和 MCP 服务可用，下一步仍需连接 TIA 工程。
4. 完全退出 AI 客户端，点击 **写入客户端配置**，再重启客户端并新建会话。服务名是 `tia-portal-vm`。

两台电脑通过局域网连接时，也按这种方式配置。密钥只用于两端连接，请勿粘贴到公开讨论或代码仓库。配置位置、备份和客户端差异见[详细配置指南](configuration.md)。

### 第一条消息：先确认版本和工程

把下面整段发给 AI，并将方括号内容替换为你的实际值：

```text
请使用已经配置的 TIA MCP，连接我在 TIA [V21] 中打开的练习工程 [工程名称]。
先读取当前服务的工具列表和参数定义，使用 GetToolUsage 查看连接、状态查询及工程浏览工具的示例，然后按当前版本调用。
报告实际 TIA 版本、工程名称或路径、PLC 列表，以及我应选择的 PLC 路径。
这一步只查看工程，不修改、保存或下载。目标不明确时，先把候选工程和 PLC 列出来。
```

成功标志是 AI 能报告实际工程和 PLC，并列出程序块。仅回答“服务连接成功”还没有完成这一步。

V20/V21 默认只直接显示 60 个常用工具，AI 可用 `FindTools` 查找其余工具，再用 `CallTool` 调用。V14 SP1–V19 直接注册各自的基础目录，不使用这套发现入口。让 AI 读取当前服务的定义，可以避免把另一版本的参数套过来。V4 工具返回 `ok`、`data`、`error` 和 `meta`；先检查 `meta.outcome`。结果为 `OUTCOME_UNKNOWN` 时，先核对工程现状，不要自动重复写入。

## 4. 第一个 AI 练习：导入加法函数并编译

交付包已有完整的小例子：[FC_Add.scl](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/reference/tool-examples/languages/FC_Add.scl)。它有两个 `Int` 输入 `LeftValue`、`RightValue`，返回二者之和。无需先让 AI 从零编写代码。

1. 先完成上一节，选择一个含 S7 PLC 的练习工程及准确 PLC 路径，并在 TIA 中结束工程内所有设备的在线连接后再编译。
2. 确认工程中还没有自己的 `FC_Add`，避免覆盖同名程序。
3. 找到示例文件的实际完整路径，例如 `D:\TIA-MCP\reference\tool-examples\languages\FC_Add.scl`。文件必须在**运行 TIA/MCP 的电脑**上；虚拟机模式不能直接读取宿主机的同名路径。
4. 把下面的提示词发给 AI，替换版本、PLC 路径及文件路径：

```text
请在 [TIA V21] 的练习工程中，对 PLC [上一步返回的准确 PLC 路径] 完成一次 SCL 导入练习。
先用 GetToolUsage(exampleId="scl-add") 读取完整编程示例。
V20/V21 用 GetToolUsage(exampleId="sequence/plc-scl-block") 读取调用序列；V14 SP1–V19 改用 exampleId="sequence/plc-scl-block-foundation"。
按当前 GetToolUsage 和工具参数定义使用以下文件：
D:\TIA-MCP\reference\tool-examples\languages\FC_Add.scl
先确认文件和目标，再导入外部源、生成程序块、读回 FC_Add 的实际路径及接口，最后编译所选 PLC 软件。
报告实际生成结果、编译错误数、警告数和嵌套诊断，不要把预览或工具返回成功直接当成编译通过。
这次先不保存项目，不下载 PLC。
```

如果 AI 客户端没有创建或访问文件的能力，把已经解压的示例文件放在上述位置即可；导入工具读取现有文件，不会自动把对话中的代码变成磁盘文件。V14 SP1–V19 的基础源导入目前接受 ASCII；第一次使用包内原始示例，不要先加入中文注释。

完成后应能找到实际生成的 `FC_Add`，接口包含两个 `Int` 输入和一个 `Int` 返回值，编译结果的错误数为 0。有警告时，让 AI 逐条解释。编译通过不等于函数已运行：此练习没有在调用块中调用它，也没有进行在线运行或下载。

V14 SP1 的生成 API 不返回生成对象列表，因此应查看导入前后的块清单和实际读回结果。遇到编译错误，把诊断、对应源码和当前工具示例交给 AI 修正后重试；遇到结果未知，先检查工程中的实际对象，再决定下一步。

确认结果后，可另外发送：

```text
请再次确认当前工程名称和路径，按当前版本的保存工具示例保存这个练习工程，并报告保存结果。
```

## 5. 用 Studio 自己操作

Studio 不需要配置 MCP，也不需要 AI 客户端。下面使用中文界面中的名称。

1. 双击同一个 `TiaOpenness.exe`，通过 **视图 → 工程操作**（Ctrl+1）进入工程页，在 **工具 → 语言** 中选择中文。标题栏在应用名旁显示当前页面。
2. 在连接前选择准确 TIA 版本。连接后需要更换版本时，关闭并重新打开 Studio，再选择新版本。
3. 点击 **项目** 打开连接选项，先执行 **环境检查**，处理缺少的运行环境或安装路径。
4. 首次练习保持 **无界面** 关闭，先在 TIA 中打开练习工程，再点击 **连接**，这样能看到首次 Openness 访问提示。
5. 检查 Studio 显示的工程名称。需要另开工程时，在项目选项中 **浏览…** 选择文件，再点击 **打开**；使用与所选版本匹配的工程。
6. 在程序块页面顶部的设备下拉框中选择 PLC，查看程序块。先选择一个已有块，通过 **导出 · 导入** 配置输出目录与格式，再使用 **导出 · SimaticML XML** 导出，检查输出文件。
7. 要做导入练习，点击 **导入 XML…**。这个现有按钮也支持选择 `.scl`、`.db` 和 `.udt`；选择包内的 `FC_Add.scl`。阅读覆盖提示，再决定是否允许替换同名对象。
8. 查看导入日志和块列表，确认 `FC_Add` 的实际结果。在 TIA 中结束工程内所有设备的在线连接，再点击 **编译 PLC 软件**，查看错误、警告和诊断。
9. 确认结果后点击 **保存项目**。显示“尚未保存”时，工程修改还没有由 Studio 保存。

入门时先熟悉浏览、导出、导入、编译和保存。VCI 是工程与本地工作区的同步功能，其中的推送/拉取不等于推送到 GitHub；V14 SP1/V15.1 没有 VCI 实现。

## 6. 怎样看出操作真的完成了

| 看到的结果 | 应怎样理解 |
|---|---|
| 配置已写入 | 客户端配置文件已更新；还需要重启客户端并连接工程 |
| 测试连接成功 | MCP 连接可用；还需要确认实际 TIA 和工程 |
| 预览、`planned` 或待执行 | 只完成计划，工程还没有因此改变 |
| 导入成功 | 检查实际导入对象及路径；接着编译检查工程 |
| 编译返回 | 查看错误数和嵌套诊断；错误数为 0 才能按编译通过处理 |
| `failed` | 查看具体原因，修正后再操作 |
| `outcome-unknown` 或连接中断 | 先核对工程现状，避免把可能已执行的写入重复一遍 |
| Studio“尚未保存” | 修改尚待保存；导入和编译本身不代表已保存 |

当前 4.0 的 D1 行为族仍标为 `current / NOT RUN`；真实 TIA 工程验收没有匹配记录时保持未运行。本文描述使用步骤，不把离线或演示结果当作真实工程验收。

## 7. 常见问题

| 现象 | 先检查什么 |
|---|---|
| 双击程序提示缺少 .NET | 确认完整解压且保留 `runtime/dotnet`；提示缺少 .NET Framework 时按第 2 节安装 4.8 |
| 找不到 Openness API、程序集版本不匹配 | 核对所选 TIA 版本、安装根目录和对应 Openness 安装，不混用另一个版本的 DLL |
| 拒绝访问 TIA | 检查当前 Windows 用户的 Openness 用户组，添加后注销重登，并处理 TIA 中的访问提示 |
| 虚拟机连接超时 | 虚拟机实际 IP、服务是否运行、端口、两台电脑是否能通信；用“网络权限”配置规则 |
| HTTP 401 | 两端密钥是否完全一致，修改后是否重新写入客户端并重启 |
| AI 看不到 MCP 或仍使用旧配置 | 完全退出客户端再启动、新建会话，检查使用的是所选卡片对应的实际客户端 |
| 少了某个工具 | 先查看当前版本的工具目录；V20/V21 用 `FindTools`，旧版基础目录的功能范围较小 |
| 导入说找不到文件 | 使用 TIA/MCP 所在电脑上的绝对路径，确认文件扩展名及文件确实存在 |
| 编译失败 | 读取所有层级的诊断，区分源代码错误、缺失类型/依赖和设备配置问题 |
| Studio 不能编译 | 确认已连接工程并选中 PLC，再查看操作日志中的原因 |

需要继续排查时，提供应用版本、TIA 准确版本、操作方式、工具名及脱敏后的错误/编译诊断。不要提供连接密钥；工程路径、程序内容和日志按自己的项目保密要求处理。

## 8. 以后怎样更新和继续学习

升级前结束正在进行的工程操作，保存需要保留的修改，关闭 Studio、AI 中的 TIA 会话以及 MCP 服务。可以下载新版本完整 ZIP 到新目录，重新用其中的配置器写入客户端配置；也可在现有交付目录用 **帮助 → 更新 → 更新引擎…**，具体更新和回退方式见[配置指南](configuration.md)。更新器不用于 Git 源码工作区。

完成第一次练习后，按需要继续阅读：

- [配置指南](configuration.md)：两种连接模式、客户端配置、升级和故障处理。
- [逐版本工具](../reference/version-tools.md)：所选版本实际提供的工具及差异。
- [统一工具示例说明](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/official-tool-usage.md)：让 AI 使用 `GetToolUsage` 获取参数、语言示例、调用序列和结果解释。
- [命令行指南](cli.md)：V20/V21 的生成、导入导出和编译流程。
- [Studio 说明](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/src/Studio/README.md)：桌面工具的工作流和支持范围。
- [文档目录](../README.md)：PLC、HMI、硬件和其他专题。

软件自己的连接记录、界面偏好、普通日志、审计记录和调用诊断分别保存在交付包的 `data\config`、`data\ui`、`data\logs` 和 `data\diagnostics` 中，更新会保留 `data`。MCP 写操作默认在派发前等待 Workbench 审批；拒绝、超时或工作台未连接都不会开始操作。审计写入 `data\logs\audit`，可由 `tia audit verify` 校验保留链，但哈希链不能证明整段记录未被删除。数据根可由绝对路径环境变量 `TIA_MCP_DATA_DIRECTORY` 指定，诊断还可由 `TIA_MCP_DIAGNOSTICS_DIRECTORY` 单独覆盖。包目录不可写或无法定位时，配置仍使用 `%LOCALAPPDATA%\TiaPortalMcp`，诊断仍使用 `%LOCALAPPDATA%\TiaMcp\diagnostics`；首次使用新配置或偏好目录只复制缺失文件，不删除旧记录。伴随 PLC Tools 的 Python 默认解释器是 `%LOCALAPPDATA%\TiaMcp\ecosystem-python\Scripts\python.exe`，可用 `TIA_MCP_PLC_TOOLS_PYTHON` 覆盖。详见[配置说明](configuration.md)。
