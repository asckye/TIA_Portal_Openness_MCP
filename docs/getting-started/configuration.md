# WPF 图形配置指南

双击 `TiaMcpConfigurator.exe` 打开连接中心。界面是一页两栏：左 **A 服务端**、右 **B AI 客户端**，中间的连线条实时显示当前端点与状态，下方是共用密钥和运行日志；右上角切换 **虚拟机 ↔ 宿主机** 与 **同一台电脑** 两种模式。日常配置与启动无需 CMD/BAT。需要 Windows .NET Framework 4.8。服务端把此 EXE 放在完整 Release 包根目录（与 `runtime` 文件夹同级）；宿主机配置客户端时只需此 EXE，不用安装 TIA。

两种模式的差别只有传输方式：**虚拟机 ↔ 宿主机** 走 HTTP，需要地址、端口和密钥；**同一台电脑** 走 stdio，由客户端按需启动引擎，A 栏只剩版本与安装目录，地址、密钥和服务按钮随之隐藏。

## 虚拟机 ↔ 宿主机模式

同一页的两栏分别在两台机器上填：A 栏在装有 TIA 的虚拟机上操作，B 栏在宿主机上操作，两侧密钥必须相同。

### A 栏：在虚拟机上启动服务

1. 用运行 TIA 的 Windows 用户打开 EXE。
2. 选择 V20/V21。安装根目录会自动探测（`TiaPortalLocation` 环境变量 → 注册表 `TIAP{版本}\TIA_Opns` → 默认安装目录，与引擎相同顺序），也可点“自动检测”重新探测或用“浏览”手动选择（到 `Portal Vxx` 为止，不带 `Bin`），例如 `C:\Program Files\Siemens\Automation\Portal V21`。必须安装对应 Openness API。
3. 填虚拟机 IPv4、端口（默认 8765），在下方密钥条点“生成密钥”，再点 **保存两端配置**。
4. **网络权限 → 启动服务**。网络权限按钮会请求 Windows 管理员授权，只为当前用户预留指定 HTTP 地址，并放行本地子网到该地址/端口的 TCP 访问。
5. 日志出现 listening、连线条状态变成 `running` 后保持窗口打开。以后打开 EXE，直接点“启动服务”。

日常打开程序不需要管理员身份。用户须属于 `Siemens TIA Openness` 组，新增组后注销重登；首次访问 TIA 时处理 Openness 提示。跨路由/VPN 子网连接需管理员另配实际源地址的防火墙规则。程序不关闭防火墙、不覆盖其它用户的 URL 授权。

服务端配置保存在 `%LOCALAPPDATA%\TiaPortalMcp\http-v21.json`（或 v20），密钥用 Windows DPAPI 按当前用户加密。换电脑/用户时重新配置。IP 改变时重新保存并配置新地址权限，旧规则不会自动删除。

关闭窗口会询问是否停止本窗口启动的 MCP。停止前确认没有工程操作进行中；不主动关闭博途，也不配置开机自启。

### B 栏：在宿主机上写入客户端

1. 在宿主机打开同一个 EXE，在 A 栏填虚拟机 IPv4、端口，在密钥条粘贴服务端的同一把密钥。宿主机没有 TIA 时，**保存两端配置** 只记录客户端侧连接信息，并在日志里说明服务端校验未通过，这是正常的。
2. 在 B 栏点击选择一个或多个客户端。
3. 点击 **测试连接**。它检查 HTTP、鉴权与 MCP 就绪状态，不修改工程。
4. 退出选中的 AI 客户端，再点击 **写入客户端配置**。确认窗口会展示具体写入位置。
5. 重启对应客户端，新建会话，使用 `tia-portal-vm` 读取工程树。不要让多个 AI 同时修改同一工程。

## 同一台电脑模式

右上角切到 **同一台电脑**：A 栏只需版本与安装目录，B 栏选客户端后点 **写入客户端配置** 即可。客户端通过 stdio 自动启动包内引擎，服务器名为 `tia-portal`，无需地址、端口、密钥、网络权限和“启动服务”。

## 客户端卡片

客户端卡片支持多选，B 栏右上角实时显示选择结果；点击该文字可查看所选客户端的使用说明，连线条左侧同步显示当前选择。卡片区在卡内上下滚动，B 栏因此与 A 栏等高、两栏按钮底部对齐；窗口缩小时卡片自动由三列换成两列。卡片顺序：CLI 在前（Claude Code、Codex 居首），IDE 在后。

**官方 Claude 桌面客户端的 Code 页**：选择“Claude Code”，保存后重启，进入 **Code → Local → 新建会话**。Claude Desktop 的 Chat 页不再提供卡片。

**Codex 桌面客户端 / CLI**：选择“Codex”，保存后重启并重新打开任务；写入用户级 `config.toml`，尊重 `CODEX_HOME`。

**通义千问、Kimi、腾讯元宝、DeepSeek、智谱清言、Grok**：这些模型的聊天 App / 网页不支持 MCP，卡片按模型命名，实际写入的是各家官方 CLI 或 OpenCode 的配置（卡片小字标出）；在该客户端里选对应模型即可操作 TIA。

| 卡片 | 实际写入 | 默认配置位置 | 远程条目 |
|---|---|---|---|
| Claude Code | Claude Code | `%USERPROFILE%\.claude.json` | `type: http` |
| Codex | Codex | `%CODEX_HOME%\config.toml`，未设置时用 `%USERPROFILE%\.codex\config.toml` | TOML `url` / `http_headers` |
| Gemini CLI | Gemini CLI | `%USERPROFILE%\.gemini\settings.json` | `httpUrl` |
| 通义千问 | 阿里 Qwen Code | `%USERPROFILE%\.qwen\settings.json` | `httpUrl`（同 Gemini CLI） |
| Kimi | 月之暗面 Kimi Code CLI | `%KIMI_CODE_HOME%\mcp.json`，未设置时用 `%USERPROFILE%\.kimi-code\mcp.json` | `url` |
| 腾讯元宝 | 腾讯 CodeBuddy Code CLI | `%USERPROFILE%\.codebuddy\.mcp.json` | `type: http` |
| DeepSeek / 智谱清言 / Grok | OpenCode（provider 分别选 DeepSeek / 智谱 GLM / xAI） | `%USERPROFILE%\.config\opencode\opencode.json` 的 `mcp` 节 | `type: remote` |
| Cursor | Cursor | `%USERPROFILE%\.cursor\mcp.json` | `url` |
| VS Code · Copilot | VS Code | `%APPDATA%\Code\User\mcp.json` | `servers` / `type: http` |

三张 OpenCode 卡片写同一个文件，多选时只写一次。本机 stdio 模式下 OpenCode 条目为 `type: local` 且 `command` 是含可执行文件的单个数组，其它客户端为 `command` + `args`。VS Code 默认路径针对标准 VS Code 默认用户配置，不涵盖 Insiders、portable 或自定义 profile。Qwen Code / Kimi Code CLI / CodeBuddy / OpenCode 的路径与字段按各自当前官方文档写入，尚未在真实客户端上联调。

豆包没有对应卡片：其 IDE（Trae）的全局 MCP 文件位置未公开、远程只支持 SSE，而本引擎只提供 Streamable HTTP；如需在 Trae 里用，可在其 MCP 面板“手动添加”里粘贴本机 stdio 条目。

配置会保留其它设置和服务，为旧文件创建唯一 `.bak_...` 备份；多选时逐个保存，若有失败会明确列出，不撤销其它成功项。无效 JSON 拒绝覆盖。JSONC 注释和尾随逗号支持读取，重写为标准 JSON，原注释保存在备份中。Codex 保留无关 TOML 文本和多行字符串；罕见的根级内联/点号 MCP 定义无法安全合并时会停止并保留原文件。

AI 客户端依照自身格式保存连接密钥，请勿分享或提交配置和备份。连接中心另在 `%LOCALAPPDATA%\TiaPortalMcp\client.json` 加密保存上次填写的连接信息，方便下次使用。

远程统一使用 `tia-portal-vm`，本机 stdio 使用 `tia-portal`，与插件一致。项目或客户端若已有同名定义，请消除重复配置。

原根目录配置 BAT 和 tia CMD 已删除，日常配置与启动统一使用 GUI；CLI 直接调用 runtime 下的 EXE。

## 故障处理与验证边界

- HTTP 拒绝访问：点击“网络权限”。已有其它用户的 URL 授权不会被自动覆盖。
- 端口占用：停止旧 MCP 或改端口，不要重复运行服务。
- 找不到 API：确认目录和版本，安装 Openness。
- 连接超时：检查虚拟机地址、网络模式、服务状态与防火墙。
- 401：两端密钥不一致；503 / 未就绪：检查服务端日志。

图形入口启动前独立检查 HTTP 监听，直接展示权限/端口错误，避免旧引擎可能把原始错误掩盖为取消异常。原 TIA 引擎二进制没有修改。HTTP 测试成功不等于 TIA 工程已连接。

已用隔离配置和模拟 HTTP 验证 11 张卡片（9 种客户端文件格式）的配置生成、合并、备份、密钥保护，以及两种模式的 WPF 渲染与可见性切换。真实虚拟机网络/UAC、各实际 AI 客户端以及真实 TIA 工程需分别联调；不将配置生成视为实际连接验收。

## 工具显示与手动 HTTP 启动

默认 lite 档显示 52 个常用工具，其余通过 `FindTools` / `CallTool` 使用；静态完整清单为 298 个，实际以服务的 `tools/list` 为准。需要全量直接显示时给服务传 `--profile full`。修改档位后重启服务与客户端并新建会话，避免读取旧缓存。

如需排查，可在安装 TIA 的电脑上从完整包根目录直接启动（示例 IP/密钥须替换）：

```powershell
.\runtime\v21\TiaMcpServer.exe --tia-major-version 21 --tia-portal-location "C:\Program Files\Siemens\Automation\Portal V21" --transport http --http-prefix "http://192.0.2.10:8765/" --http-api-key "REPLACE_WITH_YOUR_KEY"
```

地址必须是该电脑实际持有的 IPv4，前缀保留末尾 `/`。客户端连接使用相同地址端口下的 `/mcp` 端点和相同密钥。优先通过图形页配置网络权限；若出现 `OperationCanceledException`，不能仅凭此判断是用户取消，须检查原始监听错误、端口占用及 URL 授权。

## Claude Code 写保护钩子与审计日志

作为 Claude Code 插件使用时（`.claude-plugin/plugin.json` → `hooks/hooks.json`），每次调用 `tia-portal` 工具前都会经过 `hooks/tia-write-guard.ps1`：

- 非只读调用（WRITE / FILE / ONLINE / ONLINE-WRITE / EXECUTE，含经 `CallTool` 桥接的目标）追加记录到 `%LOCALAPPDATA%\TiaMcpServer\audit\tool-calls.jsonl`，`password` / `secret` / `token` 类参数写为 `<redacted>`；
- 真实 ONLINE-WRITE 调用（`DownloadToPlc`、站/参数上载、S7 Web / Unified 运行时写值与模式切换、PLCSIM Advanced 实例变更与写值）被拒绝并说明原因；带 `dryRun` 的工具在未显式传 `dryRun=false` 时视为预览放行。

| 环境变量 | 作用 |
|---|---|
| `TIA_MCP_ALLOW_ONLINE_WRITE=1` | 放行在线写入（仍审计）。在 Claude Code 的 `settings.json` `env` 中设置，或只在需要下载的会话里设置 |
| `TIA_MCP_WRITE_GUARD=0` | 完全关闭钩子（不审计、不拒绝） |
| `TIA_MCP_GUARD_DENY_OPERATIONS=EXECUTE,WRITE` | 追加要拒绝的操作类型 |
| `TIA_MCP_AUDIT_LOG` | 审计文件路径 |

钩子只对 Claude Code 生效；其他客户端仍依赖工具自身的 `dryRun=true` 默认值与 `confirm*` 参数。自检：`scripts/checks/Test-WriteGuard.ps1`。

## 开发

源码在 `tools/mcp-configurator/`：`MainWindow.xaml` 为 WPF 界面，`Configurator.cs` 为交互，`ConfigCore.cs` 为公共逻辑，`ClientProfiles.cs` 为 11 张卡片（9 种客户端格式）适配。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build/Build-Configurator.ps1 -Test
```

使用 Windows 自带 .NET Framework 编译器，XAML 嵌入单个 EXE。测试输出在 `bin-build/configurator-tests`；不修改真实 AI 配置、防火墙或工程。

手动配置示例：[cursor.example.json](cursor.example.json) 是本机 V21 stdio 示例，把 `command` 替换为交付包中 `runtime/v21/TiaMcpServer.exe` 的绝对路径；V20 同时修改路径与版本参数。宿主机连接虚拟机及其他客户端优先使用 `TiaMcpConfigurator.exe`。示例不含现场 IP、密钥或用户配置，不能直接当作已配置文件使用。

配置格式依据：[Claude Code](https://code.claude.com/docs/en/mcp)、[Codex](https://developers.openai.com/codex/mcp)、[Cursor](https://cursor.com/docs/mcp)、[VS Code](https://code.visualstudio.com/docs/agent-customization/mcp-servers)、[Gemini CLI](https://geminicli.com/docs/tools/mcp-server/)、[Qwen Code](https://qwenlm.github.io/qwen-code-docs/en/users/features/mcp/)、[Kimi Code CLI](https://www.kimi.com/code/docs/en/kimi-code-cli/customization/mcp.html)、[CodeBuddy Code](https://www.codebuddy.cn/docs/cli/mcp)、[OpenCode](https://opencode.ai/docs/mcp-servers/)。
