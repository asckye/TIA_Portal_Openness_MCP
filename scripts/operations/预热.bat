@echo off
chcp 65001 >nul
setlocal
rem 预热一个常驻 headless TIA 实例：跑一次留着，后续 CLI 命令可以复用该实例。
rem 按 Ctrl+C 停止并关闭该实例。
set "EXE=%~dp0..\..\runtime\v21\TiaMcpServer.exe"
if not exist "%EXE%" set "EXE=%~dp0..\..\runtime\v20\TiaMcpServer.exe"
if not exist "%EXE%" (
  echo 找不到 tia 可执行文件（runtime\v21 和 runtime\v20 均不存在）。
  pause
  exit /b 2
)
echo 正在冷启动 headless TIA 并保活，按 Ctrl+C 停止...
"%EXE%" prewarm
