# MCP 图形配置指南

第一次使用请先阅读[新手使用指南](beginners.zh-CN.md)，完成环境准备、连接和单个函数练习。本页用于查询连接选项、客户端文件、升级和故障处理。

当前源码构建的统一工作台从包根目录 `TiaMcpConfigurator.exe` 打开，选择 **MCP 与客户端** 页面。顶部统一切换 TIA 版本、中文 / English 和主题；配置页右上角 **•••** 保留更新、帮助与保存两端配置。配置页编译在工作台内，桌面需要 .NET 10 Desktop Runtime，必须保留完整解压包，不能单独复制入口 EXE。已发布 v3.2.0 的独立配置器不采用此界面。

## 同一台电脑

1. 选择 **同一台电脑**。
2. 选择准确 TIA 版本：V14 SP1、V15.1、V16、V17、V18、V19、V20 或 V21。
3. 点击 **自动检测**，或 **浏览** 选择安装根目录，例如 `C:\Program Files\Siemens\Automation\Portal V21`，不带 `Bin`。
4. 选择实际使用的客户端卡片，完全退出该客户端，点击 **写入客户端配置** 并检查结果。
5. 重启客户端、新建会话，使用服务 `tia-portal`。

本机使用 stdio，由客户端启动对应引擎，不需要 IP、密钥或手动启动 HTTP 服务。所选版本应与实际 TIA 和工程匹配，选择器不转换工程版本。

## 虚拟机与宿主机，或两台电脑

### TIA 所在电脑：启动服务

1. 用运行 TIA 的 Windows 用户打开配置器，选择 **虚拟机 ↔ 宿主机**。
2. 选择 TIA 版本和安装根目录，填写该电脑的实际 IPv4 和端口，默认 `8765`。
3. 点击 **生成** 得到密钥，从 **••• → 保存两端配置** 保存。
4. 点击 **网络权限**，完成管理员授权，再点击 **启动服务**。
5. 查看活动日志的监听信息和连接摘要“运行中”，保持窗口打开。

用户应属于 `Siemens TIA Openness` 组，添加后注销重登，并处理首次 TIA 访问提示。“网络权限”配置所选 HTTP 地址和本地子网入站规则；跨路由或 VPN 的访问按实际网络另行配置。

### AI 所在电脑：写入连接

1. 打开配置器，选择相同模式，填写 TIA 电脑的 IPv4、端口和相同密钥。
2. 选择实际使用的客户端，点击 **测试连接**。
3. 完全退出客户端，点击 **写入客户端配置**，检查写入位置及结果。
4. 重启客户端，新建会话，使用服务 `tia-portal-vm`。

宿主机未安装 TIA 时，“保存两端配置”可能记录客户端信息并报告服务端环境不可用；实际服务端是前一台电脑。测试连接成功说明 MCP 可达，仍需让 AI 确认实际 TIA 工程和 PLC。

### 文件、PLC 和保存分别是什么

- **文件路径**：导入工具读取 TIA/MCP 电脑上的文件。在虚拟机模式中，宿主机 `D:\...` 不会自动成为虚拟机的同一路径；先把源文件复制到服务端可读取的位置。
- **选择 PLC**：一个工程可能包含多台 PLC，程序导入、浏览和编译需要明确目标。使用工程树实际返回的软件路径；Studio 在设备下拉框中选择。
- **保存工程**：导入和编译可能只改变当前打开的工程。核对结果后调用 `SaveProject`，或在 Studio 点击 **保存项目**，才完成相应保存操作。

这些是完成一次实际操作的步骤，不是另外增加三个功能。

## 客户端卡片和配置文件

卡片支持多选，副标题显示本机检测结果；未检测到的客户端也可先写配置。模型名称卡片实际对应其支持 MCP 的 CLI 或宿主，按卡片说明选择软件。

| 卡片 | 配置目标及默认位置 |
|---|---|
| Claude Code | `%USERPROFILE%\.claude.json` |
| Codex | `%CODEX_HOME%\config.toml`，未设置时为 `%USERPROFILE%\.codex\config.toml` |
| Gemini CLI | `%USERPROFILE%\.gemini\settings.json` |
| Qwen | Qwen Code：`%USERPROFILE%\.qwen\settings.json` |
| Kimi | Kimi Code CLI：`%KIMI_CODE_HOME%\mcp.json`，未设置时为 `%USERPROFILE%\.kimi-code\mcp.json` |
| Yuanbao | CodeBuddy Code：`%USERPROFILE%\.codebuddy\.mcp.json` |
| DeepSeek / GLM / Grok | OpenCode：`%USERPROFILE%\.config\opencode\opencode.json` |
| Qwen Agent | `%USERPROFILE%\.qwen-agent\mcp.json` |
| Cursor | `%USERPROFILE%\.cursor\mcp.json` |
| VS Code · Copilot | `%APPDATA%\Code\User\mcp.json`；仅检测到 Insiders 时选择对应目录 |

三张 OpenCode 卡片共用一个文件；选择后还需在 OpenCode 中使用相应模型。写入配置不代表已完成真实客户端联调。自定义 profile、portable 安装等位置以写入确认中展示的路径为准。

配置器保留已有其他服务和设置，并为旧文件生成 `.bak_...` 备份。无效输入会报告具体失败；多选客户端时分别查看各项结果。JSONC 重写后为标准 JSON，原文保留在备份中。已有同名 `tia-portal` / `tia-portal-vm` 定义时检查是否存在重复来源。

本机连接记录位于 `%LOCALAPPDATA%\TiaPortalMcp`；HTTP 服务配置按所选版本保存为 `http-v<版本>.json`，客户端记录为 `client.json`。这些本机记录使用当前 Windows 用户的加密保护；客户端自身配置文件按其格式保存密钥，不要公开配置或备份。

## 工具显示与示例

V20/V21 默认 lite 显示 63 个常用工具，其余用 `FindTools` / `CallTool`；完整目录分别为 477 / 488 个。需要全量直接显示时给对应完整引擎传 `--profile full`。

基础目录的工具数依次为 V14 SP1 57、V15.1 58、V16/V17/V18 各 60、V19 62，不使用完整引擎的 profile 和发现入口。所有版本都用 `GetToolUsage` 读取自己的工具参数、语言示例和调用序列。修改服务配置后重启服务与客户端，避免旧目录缓存。

## 更新

先结束工程操作并保存需要保留的修改，结束工作台的工程会话、AI 中的 TIA 会话和 MCP 服务。可下载新版本完整 ZIP 到新目录，再用新配置器写入客户端配置。

交付目录也可使用 **••• → 更新 → 更新引擎…**。更新器检查下载校验和、备份到 `.previous`，替换交付文件后重新打开配置器；日志保留在更新窗口。源码 Git 工作区使用仓库的开发流程，不由交付更新器覆盖。虚拟机服务端更新完成后重新启动服务。

## 常见问题

| 现象 | 检查或操作 |
|---|---|
| HTTP 地址拒绝访问 | 在服务端点击“网络权限”，检查实际地址 |
| 端口占用 | 停止旧服务或使用另一个明确端口，并重写客户端配置 |
| 找不到 API | 版本、安装根目录和对应 Openness 安装 |
| 连接超时 | 服务端实际 IP、服务运行状态、网络和防火墙规则 |
| HTTP 401 | 两端密钥及客户端重新加载情况 |
| HTTP 503 / 未就绪 | 查看服务端日志 |
| 连接后没有工程 | 用当前版本的连接示例明确绑定目标工程 |
| 工具不存在 | 检查当前版本目录，V20/V21 再使用 FindTools |

需要命令行诊断时使用 [CLI 指南](cli.md)。V20/V21 可选工作进程隔离的参数与恢复方式见[隔离指南](../guides/openness-worker-isolation.md)，配置器没有对应勾选项。

作为 Claude Code 插件使用时，还会加载仓库的 `hooks/tia-write-guard.ps1`；它记录审计并按环境设置控制在线写入。普通 GUI/MCP 连接不等于加载了该插件。具体工具执行参数与结果解释仍以 `GetToolUsage` 为准。

手动本机 V21 配置见 [cursor.example.json](cursor.example.json)，替换引擎绝对路径后使用。其他版本和远程模式优先由配置器生成，避免复制不匹配参数。
