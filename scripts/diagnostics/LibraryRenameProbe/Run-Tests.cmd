@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-Tests.ps1" %*
set "RESULT=%ERRORLEVEL%"
echo.
echo Test runner exit code: %RESULT%
echo See the results folder beside this file.
pause
exit /b %RESULT%
