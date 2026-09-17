# 运行时

- V20：`runtime/v20/TiaMcpServer.exe`
- V21：`runtime/v21/TiaMcpServer.exe`

每个目录的依赖均需保留，不可只复制引擎 EXE。Siemens PublicAPI 由匹配版本的本机 TIA / Openness 提供，不随包分发。版本与逐文件哈希见 [引擎记录](../manifest/release-build.json)。

Git 仓库和交付 ZIP 使用相同路径，不再附带旧 `tools/.../bin/Release/net48` 副本。旧客户端改为上述引擎绝对路径，或通过 [图形配置器](../docs/getting-started/configuration.md) 重新保存。
