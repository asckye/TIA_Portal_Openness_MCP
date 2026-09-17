# WPF 图形配置指南

双击 `TiaMcpConfigurator.exe` 打开连接中心。界面采用深色编号侧栏、浅色内容区、圆角卡片和蓝色强调；日常配置与启动无需 CMD/BAT。需要 Windows .NET Framework 4.8。服务端把此 EXE 放在完整 Release 包根目录（与 `runtime` 文件夹同级）；宿主机配置客户端时只需此 EXE，不用安装 TIA。

## 第一步：在虚拟机配置服务

1. 用运行 TIA 的 Windows 用户打开 EXE，选择 **虚拟机服务端**。
2. 选择 V20/V21，填写 TIA 安装根目录（到 `Portal Vxx` 为止，不带 `Bin`），例如 `C:\Program Files\Siemens\Automation\Portal V21`。必须安装对应 Openness API。
3. 填虚拟机 IPv4、端口（默认 8765），输入或生成连接密钥。宿主机需填写同一个密钥。
4. **保存配置 → 配置网络权限 → 启动服务**。网络权限按钮会请求 Windows 管理员授权，只为当前用户预留指定 HTTP 地址，并放行本地子网到该地址/端口的 TCP 访问。
5. 日志出现 listening 后保持窗口打开。以后打开 EXE，直接点“启动服务”。

日常打开程序不需要管理员身份。用户须属于 `Siemens TIA Openness` 组，新增组后注销重登；首次访问 TIA 时处理 Openness 提示。跨路由/VPN 子网连接需管理员另配实际源地址的防火墙规则。程序不关闭防火墙、不覆盖其它用户的 URL 授权。

服务端配置保存在 `%LOCALAPPDATA%\TiaPortalMcp\http-v21.json`（或 v20），密钥用 Windows DPAPI 按当前用户加密。换电脑/用户时重新配置。IP 改变时重新保存并配置新地址权限，旧规则不会自动删除。

关闭窗口会询问是否停止本窗口启动的 MCP。停止前确认没有工程操作进行中；不主动关闭博途，也不配置开机自启。

## 第二步：在宿主机配置 AI

1. 在宿主机打开同一个 EXE，选择 **AI 客户端**。
2. 点击选择一个或多个客户端，填虚拟机 IPv4、端口、相同密钥。
3. 点击 **测试连接**。它检查 HTTP、鉴权与 MCP 就绪状态，不修改工程。
4. 退出选中的 AI 客户端，再点击 **配置所选客户端**。确认窗口会展示具体写入位置。
5. 重启对应客户端，新建会话，使用 `tia-portal-vm` 读取工程树。不要让多个 AI 同时修改同一工程。

客户端卡片支持多选，右上角实时显示选择结果；点击该文字可查看所选客户端的使用说明。窗口缩小时卡片自动换成四列，右侧内容可滚动到操作按钮和日志。

**官方 Claude 桌面客户端的 Code 页**：选择“Claude Code”，保存后重启，进入 **Code → Local → 新建会话**。

**Codex 桌面客户端 / CLI**：选择“Codex”，保存后重启并重新打开任务；写入用户级 `config.toml`，尊重 `CODEX_HOME`。

**Claude Desktop 的 Chat 页**：选择“Claude Desktop · Chat”。其局域网远程配置通过 `mcp-remote` stdio 桥接，需要先安装 Node.js LTS（含 npx）；第一次使用将通过 npm 下载桥接包。Code 页不需要这个桥接。此功能不使用云端自定义连接器。

| 客户端 | 默认配置位置 | 远程方式 |
|---|---|---|
| Claude Code | `%USERPROFILE%\.claude.json` | HTTP |
| Codex | `%CODEX_HOME%\config.toml`，未设置时用 `%USERPROFILE%\.codex\config.toml` | TOML URL / HTTP headers |
| Cursor | `%USERPROFILE%\.cursor\mcp.json` | HTTP URL |
| VS Code / Copilot | `%APPDATA%\Code\User\mcp.json` | `servers` / HTTP |
| Claude Desktop · Chat | `%APPDATA%\Claude\claude_desktop_config.json` | npx / mcp-remote 桥接 |
| Gemini CLI | `%USERPROFILE%\.gemini\settings.json` | `httpUrl` |
| Windsurf | `%USERPROFILE%\.codeium\windsurf\mcp_config.json` | `serverUrl` |
| Cline · VS Code | `%APPDATA%\Code\User\globalStorage\saoudrizwan.claude-dev\settings\cline_mcp_settings.json` | `streamableHttp` |

VS Code / Cline 默认路径针对标准 VS Code 默认用户配置，不涵盖 Insiders、portable 或自定义 profile。

配置会保留其它设置和服务，为旧文件创建唯一 `.bak_...` 备份；多选时逐个保存，若有失败会明确列出，不撤销其它成功项。无效 JSON 拒绝覆盖。JSONC 注释和尾随逗号支持读取，重写为标准 JSON，原注释保存在备份中。Codex 保留无关 TOML 文本和多行字符串；罕见的根级内联/点号 MCP 定义无法安全合并时会停止并保留原文件。

AI 客户端依照自身格式保存连接密钥，请勿分享或提交配置和备份。连接中心另在 `%LOCALAPPDATA%\TiaPortalMcp\client.json` 加密保存上次填写的连接信息，方便下次使用。

远程通常使用 `tia-portal-vm`；Claude Desktop Chat 使用 `tia-portal-vm-chat`，防止桥接配置覆盖 Code 页的原生 HTTP 连接。两者均避免与插件/本机的 `tia-portal` 冲突。项目或客户端若已有同名定义，请消除重复配置。

## 本机连接

当 TIA 和 AI 在同一台电脑：在服务端页选择 TIA 版本与路径，然后进入“本机连接”，多选客户端并保存。客户端通过 stdio 自动启动包内引擎，无需端口和密钥。原根目录配置 BAT 和 tia CMD 已删除，日常配置与启动统一使用 GUI；CLI 直接调用 runtime 下的 EXE。

## 故障处理与验证边界

- HTTP 拒绝访问：点击“配置网络权限”。已有其它用户的 URL 授权不会被自动覆盖。
- 端口占用：停止旧 MCP 或改端口，不要重复运行服务。
- 找不到 API：确认目录和版本，安装 Openness。
- 连接超时：检查虚拟机地址、网络模式、服务状态与防火墙。
- 401：两端密钥不一致；503 / 未就绪：检查服务端日志。

图形入口启动前独立检查 HTTP 监听，直接展示权限/端口错误，避免旧引擎可能把原始错误掩盖为取消异常。原 TIA 引擎二进制没有修改。HTTP 测试成功不等于 TIA 工程已连接。

已用隔离配置和模拟 HTTP 验证 8 个客户端的配置生成、合并、备份、密钥保护和 WPF 渲染。真实虚拟机网络/UAC、8 个实际 AI 客户端以及真实 TIA 工程需分别联调；不将配置生成视为实际连接验收。

## 工具显示与手动 HTTP 启动

默认 lite 档显示 52 个常用工具，其余通过 `FindTools` / `CallTool` 使用；静态完整清单为 298 个，实际以服务的 `tools/list` 为准。需要全量直接显示时给服务传 `--profile full`。修改档位后重启服务与客户端并新建会话，避免读取旧缓存。

如需排查，可在安装 TIA 的电脑上从完整包根目录直接启动（示例 IP/密钥须替换）：

```powershell
.\runtime\v21\TiaMcpServer.exe --tia-major-version 21 --tia-portal-location "C:\Program Files\Siemens\Automation\Portal V21" --transport http --http-prefix "http://192.0.2.10:8765/" --http-api-key "REPLACE_WITH_YOUR_KEY"
```

地址必须是该电脑实际持有的 IPv4，前缀保留末尾 `/`。客户端连接使用相同地址端口下的 `/mcp` 端点和相同密钥。优先通过图形页配置网络权限；若出现 `OperationCanceledException`，不能仅凭此判断是用户取消，须检查原始监听错误、端口占用及 URL 授权。

## 开发

源码在 `tools/mcp-configurator/`：`MainWindow.xaml` 为 WPF 界面，`Configurator.cs` 为交互，`ConfigCore.cs` 为公共逻辑，`ClientProfiles.cs` 为 8 个客户端适配。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build/Build-Configurator.ps1 -Test
```

使用 Windows 自带 .NET Framework 编译器，XAML 嵌入单个 EXE。测试输出在 `bin-build/configurator-tests`；不修改真实 AI 配置、防火墙或工程。

配置格式依据：[Claude Code](https://code.claude.com/docs/en/mcp)、[Codex](https://developers.openai.com/codex/mcp)、[Cursor](https://cursor.com/docs/mcp)、[VS Code](https://code.visualstudio.com/docs/agent-customization/mcp-servers)、[Gemini CLI](https://geminicli.com/docs/tools/mcp-server/)、[Windsurf](https://docs.windsurf.com/windsurf/cascade/mcp)、[Cline](https://docs.cline.bot/mcp/mcp-overview)、[mcp-remote](https://github.com/punkpeye/mcp-remote)。
