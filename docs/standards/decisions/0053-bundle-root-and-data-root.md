# 0053 包根由显式输入或已知锚点决定，软件数据优先写包内 data

- 状态：已采纳
- 日期：2026-10-03
- 决策者：维护者
- 置信度：中
- 相关：[阶段 6 契约规范](../../archive/development/phase6-review.md)第 5 节、第 6 节决策 9；[运行布局历史](../../archive/development/runtime-layout-history.md) D-G7-2、D-G7-6；[设计规范](../design.md) 第 3.2、3.3 节；提交 `b91030490`、`42c83c503`、`4f849bd5f`

回溯记录（写于 2026-11-25，待补写编号 B29）。决策 9 原定日志放在 LocalAppData，后被数据目录政策取代；数据目录政策由 `b91030490`（2026-10-04）实现，其决定日期没有单独记录。

## 背景

当时的定位会向祖先仓库查找、猜测 `templates` 与 `tools` 目录，同级引擎找不到时改用自身；软件数据写在多个用户目录中。决定的原因没有记录，把数据放在包内的原因也没有记录。

## 选项

没有记录其他完整方案。被放弃的是祖先探测、旧变量 `TIA_MCP_REPOSITORY_ROOT`（不作别名）和日志放在 LocalAppData。

## 决定

- 包根按 `--bundle-root` → `TIA_MCP_BUNDLE_ROOT` → 已知安装或开发锚点选择，命令行优先；根须含 `manifest/package-manifest.json`。显式根无效立即报错，不向别处查找；资源缺失报 `RESOURCE_UNAVAILABLE`。
- 软件自身的数据优先写 `<包根>\data`；`TIA_MCP_DATA_DIRECTORY` 优先于它；包根不可写时按用途回退到原用户目录。
- 私人工作区须显式指定；伴随 Python 环境仍在 `%LOCALAPPDATA%`。

## 后果

- 正面：定位不依赖当前目录和 `.git`，错误根立即可见；只读安装照常工作。
- 负面与取舍：更新和回滚必须保留安装目录中的 `data`。
- 验证方式：`BundleRootTests`；源码扫描拒绝 `DataLocations` 与允许列表之外新增的直接临时或应用数据路径（`b91030490`）。
