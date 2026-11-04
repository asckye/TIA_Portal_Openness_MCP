# 保留的程序集检查夹具

`prepare` 为包内审批检查构建本工程；名称匹配脚本仍可使用 `test-match-plc-name` 模式。`Golden/` 中的文件也由 KeptCode 注册套件嵌入，不能随 PLC 模拟删除。

PREP-SLIM 删除了 `Test-PlcHarness.py`、对应 file app、`--plc` 入口及其专用模拟模式。共享的程序集检查辅助代码仍供保留入口使用；注册套件和传输检查由 `merge-check` 执行，详见[验证](../../../docs/manuals/validation.md)。
