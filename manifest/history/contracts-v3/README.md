# 3.x 工具契约归档

| 文件 | 用途 |
|---|---|
| `baseline/<releaseKey>.json` | 3.2.0 八版工具名称、输入 schema、描述摘要和完整引擎 lite 名单 |
| `responses/<releaseKey>.json` | 3.2.0 八版离线响应与调用前拒绝证据 |
| [provenance.json](provenance.json) | 逐文件来源提交、发布版本、发布键和 SHA-256；README 的 SHA-256 |

发布键固定为 `14sp1`、`15.1`、`16`、`17`、`18`、`19`、`20`、`21`。
这些字节来自迁移前的 `manifest/contracts/baseline` 与 `manifest/contracts/responses`，
来源版本按各来源提交的 Version.props 或 package-manifest.json 确认。

归档只读：不得新增、删除、覆盖、重新序列化、改变编码或行尾，也不得重算文件内哈希。
`Check-Repository.py --no-binaries` 对 provenance.json 校验文件清单和 SHA-256，
同时拒绝旧目录重新出现。当前 V4 基线位于 [../../contracts/v4/](../../contracts/v4/)，
由现有 Snapshot-ToolContracts/Responses capture 先写临时目录，再比较和审查。
