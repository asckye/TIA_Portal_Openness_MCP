@echo off
setlocal
rem Read-only local diagnostics. Does not attach, start, stop, or modify TIA.
set "TiaEvidenceDir=%TEMP%\TIA_MCP_ExitEvidence_%RANDOM%_%RANDOM%"
if exist "%TiaEvidenceDir%" (
  echo Output directory already exists. Run this command again.
  exit /b 1
)
mkdir "%TiaEvidenceDir%" || exit /b 1
echo Events: most recent 100 matches per query in the last 24 hours.>"%TiaEvidenceDir%\scope.txt"
echo Application: 1000, 1001, 1026; ProcessExitMonitor: 3000; System Resource-Exhaustion-Detector: 2004.>>"%TiaEvidenceDir%\scope.txt"
echo Empty results do not prove the absence of a fault. Review query errors and TIA logs.>>"%TiaEvidenceDir%\scope.txt"
echo Local capture time: %date% %time%>"%TiaEvidenceDir%\capture-time.txt"
powershell -NoProfile -NonInteractive -Command "[DateTimeOffset]::UtcNow.ToString('o')" >>"%TiaEvidenceDir%\capture-time.txt" 2>"%TiaEvidenceDir%\capture-time-errors.txt"
tasklist /FI "IMAGENAME eq Siemens.Automation.Portal.exe" /FO CSV >"%TiaEvidenceDir%\portal-processes.csv" 2>"%TiaEvidenceDir%\tasklist-errors.txt"
wevtutil qe Application /q:"*[System[(EventID=1000 or EventID=1001 or EventID=1026) and TimeCreated[timediff(@SystemTime) <= 86400000]]]" /rd:true /f:xml /c:100 >"%TiaEvidenceDir%\application-events.xml" 2>"%TiaEvidenceDir%\event-query-errors.txt"
wevtutil qe Application /q:"*[System[Provider[@Name='Microsoft-Windows-ProcessExitMonitor'] and (EventID=3000) and TimeCreated[timediff(@SystemTime) <= 86400000]]]" /rd:true /f:xml /c:100 >"%TiaEvidenceDir%\process-exit-events.xml" 2>"%TiaEvidenceDir%\process-exit-query-errors.txt"
wevtutil qe System /q:"*[System[Provider[@Name='Microsoft-Windows-Resource-Exhaustion-Detector'] and (EventID=2004) and TimeCreated[timediff(@SystemTime) <= 86400000]]]" /rd:true /f:xml /c:100 >"%TiaEvidenceDir%\resource-exhaustion-events.xml" 2>"%TiaEvidenceDir%\resource-exhaustion-query-errors.txt"
if exist "%TEMP%\TiaMcpServer.native-export.log" copy /y "%TEMP%\TiaMcpServer.native-export.log" "%TiaEvidenceDir%\native-export.log" >nul
echo Diagnostic files saved locally:
echo %TiaEvidenceDir%
echo Review before sharing: logs and Windows events can contain private paths.
endlocal
