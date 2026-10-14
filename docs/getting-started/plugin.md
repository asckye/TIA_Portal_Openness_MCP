# 安装 Claude Code 插件

本页适用于包含 V14 SP1、V15.1、V16、V17、V18、V19、V20 和 V21 运行时的完整交付包。安装插件后，Claude Code 会通过包内启动器运行本机 MCP 服务。

[文档目录](../README.md) · [新手使用指南](beginners.zh-CN.md) · [配置指南](configuration.md)

## 从本机交付包安装

1. 将完整交付包解压到固定目录，例如 `D:\TIA-MCP`，保留 `.claude-plugin`、`runtime`、`manifest` 和其他随附目录。
2. 在 Claude Code 会话中添加本机插件市场。将下列路径替换为实际解压目录：

   ```text
   /plugin marketplace add D:\TIA-MCP
   ```

3. 在同一会话中安装插件：

   ```text
   /plugin install tia-portal-openness-mcp@tia-portal-openness-mcp
   ```

4. 完全退出并重启 Claude Code，创建新会话，检查 `tia-portal` 服务的 `tools/list` 和 `GetToolUsage()`。

插件启动命令为 `${CLAUDE_PLUGIN_ROOT}/runtime/tools/TiaMcp.PluginLauncher.exe`。Claude Code 管理插件副本的位置；不要单独复制启动器 EXE，也不要把插件启动命令固定为某一版宿主。已有手工配置的同名服务时，请在客户端中检查重复来源，保留实际要使用的连接。

插件更新以插件市场中的版本为准。只修改原解压目录的文件不会自动刷新已安装的缓存副本；更新交付包后，在 Claude Code 中更新插件市场和已安装插件，再重启客户端。

## 与工作台共用交付包

默认使用插件自己的交付包副本。若从另一目录打开工作台，请在启动 Claude Code 前设置 `TIA_MCP_BUNDLE_ROOT`，指定该工作台使用的绝对包根目录。这样宿主使用同一包的运行时、数据目录和审批范围。

在任意目录的 PowerShell 窗口中运行下列命令，然后从该窗口启动 Claude Code。将 `D:\TIA-MCP` 替换为工作台所在的完整交付包目录：

```powershell
$env:TIA_MCP_BUNDLE_ROOT = 'D:\TIA-MCP'
claude
```

指定目录必须包含 `manifest/package-manifest.json`，且清单的 `packageType` 必须为 `tia-portal-mcp-complete-offline-delivery-bundle`。启动器只接受指定的准确包根，不查找它的父目录，也不在失败时切回插件缓存。无效值会输出错误并以非零退出码结束。

从同一目录打开根入口 `TiaOpenness.exe`，并选择相同 TIA 版本。若还使用 `TIA_MCP_DATA_DIRECTORY` 等数据位置设置，请让工作台和 Claude Code 继承相同设置。审批仍由工作台处理；关闭启动器会停止它启动的 MCP 宿主。

## 选择 TIA 版本

`TIA_MCP_RELEASE_KEY` 指定插件要服务的准确发布键：`14sp1`、`15.1`、`16`、`17`、`18`、`19`、`20` 或 `21`。例如，在任意目录的 PowerShell 窗口中选择 V19 后启动 Claude Code：

```powershell
$env:TIA_MCP_RELEASE_KEY = '19'
claude
```

未设置时，启动器和工作台采用同一规则：先选择本机已安装的最新 TIA 发布版本；未发现安装时，选择所选包内最新且具有宿主和匹配 worker 的可运行发布版本。此规则不转换工程版本。检测到的安装缺少对应包内宿主、指定键无效或所选版本文件缺失时，启动器明确报错，不切换到其他版本。

所选命令为 `runtime/v<发布键>/TiaMcp.FoundationHost.exe --release-key <发布键>`。启动器保持 MCP 标准输入、标准输出和标准错误流，转发宿主退出码，并原样传递额外启动器参数。

要恢复自动选择，可在启动客户端前从当前 PowerShell 会话移除变量：

```powershell
Remove-Item Env:TIA_MCP_RELEASE_KEY -ErrorAction SilentlyContinue
Remove-Item Env:TIA_MCP_BUNDLE_ROOT -ErrorAction SilentlyContinue
```

环境变量由启动客户端的进程继承。更改后请完全退出并重启 Claude Code；正在运行的会话不会自动采用新值。
