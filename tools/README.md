# 源码与 AI skill

| 目录 | 内容 |
|---|---|
| [mcp-configurator](mcp-configurator) | WPF XAML、交互逻辑、8 种客户端适配及隔离测试 |
| [tiaportal-mcp/src/TiaMcpServer](tiaportal-mcp/src/TiaMcpServer) | V20/V21 共享引擎源码和两个工程文件。`ModelContextProtocol/Tools/` 是全部 `McpServer.*` 工具声明，`ModelContextProtocol/Builders/` 是离线构造器/分析器/校验套件，其余为 MCP 基础设施（响应、分类、指南、导出寄存）；`Siemens/Portal/` 是全部 `Portal.*` 实现，`Siemens/Hmi/` 是 Unified/经典 HMI 访问层，其余为 Openness 解析、反射与纯逻辑助手；`Runtime/` 是不经 Openness 的运行时通道；`Cli/` 与根目录 `Program*.cs` 是命令行入口 |
| [tiaportal-mcp/tests](tiaportal-mcp/tests) | 离线回归及实际 EXE 的 HTTP / API 检查 |
| [tiaportal-mcp/skill](tiaportal-mcp/skill/SKILL.md) | AI 工程操作规则与调用示例 |
| [vci-watch](vci-watch/README.md) | 可选看门狗脚本：程序编译后自动经 VCI 导出、写 CHANGELOG 并提交 Git（见[版本控制指南](../docs/guides/version-control.md)） |

引擎源码/测试保持稳定目录，与已验证运行文件的哈希对照。运行入口在 `runtime/`，开发命令见 [验证说明](../docs/development/validation.md)。
