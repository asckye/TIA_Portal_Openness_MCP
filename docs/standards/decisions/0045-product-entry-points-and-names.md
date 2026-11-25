# 0045 4.0 的程序改用新名称，包根启动器打开工作台，3.x 升级是全新安装

- 状态：已采纳
- 日期：2026-10-03
- 决策者：维护者
- 置信度：高
- 相关：[阶段 6 契约规范](../../archive/development/phase6-review.md)第 5 节、第 6 节决策 7 和 8；[4.0 发布说明](../../releases/v4.0.0.md)“从 3.x 升级”；[迁移指南](../../guides/migrate-3.3-to-4.0.md)；[ADR 0001](0001-hard-cut-v4-contract.md)、[0005](0005-v20-v21-on-foundation-host.md)、[0022](0022-engine-retirement-and-eight-release-unification.md)；提交 `b0b26fef4`

回溯记录（写于 2026-11-25，待补写编号 B03）。日期取阶段 6 契约规范的决策日期；“3.x 升级是全新安装”的单独决定日期没有记录。

## 背景

4.0 已决定硬切到 V4 契约（ADR 0001）。当时有三个同名的程序，包根还有旧配置器入口。

## 选项

没有记录其他方案。改名的原因没有记录；写明的约束只有一条：根启动器的程序集名不能与工作台相同，避免同基名程序集身份混淆。

## 决定

- 三个同名程序改为 `TiaMcp.FoundationHost.exe`、`TiaMcp.Engine.V20.exe`、`TiaMcp.Engine.V21.exe`，程序集名同步。发布键目录、开发输出目录、源命名空间、客户端配置的 server key 和 HTTP `/mcp` 路径不因改名变化。
- 包根 `TiaOpenness.exe` 是正式启动器，启动 `runtime/studio/TiaOpenness.exe`，程序集名为 TiaOpenness.Launcher；删除包根旧配置器入口。
- 3.x 升级到 4.0 是全新安装：解压到新目录，保留旧安装，重建各客户端配置。

## 后果

- 正面：根入口只有一个。
- 负面与取舍：3.x 用户须手动并排安装，重建客户端配置；只有部分设置在目标不存在时复制到新位置。
- 后续变化：两个引擎程序随引擎退役删除，八个版本只用 FoundationHost（ADR 0005、0022）。
- 落实：`b0b26fef4`（2026-10-05）。
- 验证方式：交付清单 `scripts/operations/delivery-files.json` 与包校验。
