param(
    [Parameter(Mandatory=$true)][string]$PublicApiDirectory,
    [string]$SourceCommit='',
    [string]$OutputDirectory='',
    [switch]$DevelopmentOnly
)
$ErrorActionPreference='Stop'
if(!$DevelopmentOnly){throw 'This is a V21 development-only package. Public Releases must use Build-Release.ps1 and Package-Release.py for the complete V20/V21 delivery.'}
Write-Warning 'Development-only runtime package; do not upload this ZIP as a public Release delivery.'
$repo=Split-Path $PSScriptRoot -Parent
$built=Join-Path $repo 'tools/tiaportal-mcp/src/TiaMcpServer/bin/Release/net48'
$exe=Join-Path $built 'TiaMcpServer.exe'
if((Get-Item -LiteralPath $exe).VersionInfo.FileVersion -ne '2.7.2.7'){throw 'Build 2.7.2.7 first.'}
$name='TIA_MCP_V21_2.7.2.7_ReadOnly'
$output=if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path $repo 'bin-build/migration-read'}
$stage=Join-Path $output $name
if(Test-Path -LiteralPath $stage){throw 'Package stage already exists; choose a new output or inspect it before removing it.'}
New-Item -ItemType Directory -Force (Join-Path $stage 'runtime/v21'),(Join-Path $stage 'docs/licenses') | Out-Null
Get-ChildItem -LiteralPath $built -File | Where-Object {$_.Extension -in '.exe','.dll','.config' -and $_.Name -notlike 'Siemens.Engineering*'} | Copy-Item -Destination (Join-Path $stage 'runtime/v21')
Copy-Item -LiteralPath (Join-Path $repo 'tia.cmd'),(Join-Path $repo 'LICENSE') -Destination $stage
Copy-Item -LiteralPath (Join-Path $repo 'docs/UNIFIED_READ_ONLY_MIGRATION.md') -Destination (Join-Path $stage 'docs')
Copy-Item -LiteralPath (Join-Path $repo 'docs/licenses/Esprima-3.0.5.txt') -Destination (Join-Path $stage 'docs/licenses')
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-MigrationReadAssembly.ps1') -Exe (Join-Path $stage 'runtime/v21/TiaMcpServer.exe') -PublicApiDirectory $PublicApiDirectory > (Join-Path $output 'assembly-validation.log') 2>&1
if($LASTEXITCODE){throw 'Packaged EXE validation failed.'}
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Generate-ToolsListFromAssembly.ps1') -Exe (Join-Path $stage 'runtime/v21/TiaMcpServer.exe') -PublicApiDirectory $PublicApiDirectory -OutputPath (Join-Path $stage 'tools-list.json') -PackageName $name
if($LASTEXITCODE){throw 'Tool roster generation failed.'}
$readme=@'
TIA Portal V21 / WinCC Unified 只读采集开发验证包，文件版本 2.7.2.7

前提：在已安装 TIA Portal V21、Openness、.NET Framework 4.8 的虚拟机运行；当前用户已有 Siemens TIA Openness 权限；HTTP 8765 端口和原有防火墙配置可用。
完整解压后，在本目录地址栏输入 cmd，使用原有启动参数和原有密钥启动。新包包含 Esprima.dll，不能只复制 EXE。

call tia.cmd --tia-major-version 21 --tia-portal-location "C:\Program Files\Siemens\Automation\Portal V21" --transport http --http-prefix "http://192.168.0.172:8765/" --http-api-key "替换为你的旧密钥" --logging 1

包内没有连接密钥。上述占位文字必须替换为现有密钥。不要新开第二个占用相同端口的 MCP；仅切换 MCP 服务，保留博途及已打开工程。无需保存、编译或下载工程。

该包保留原服务功能与鉴权；新增采集接口本身只读。完整接口、分页方法、已知限制和待验收项见 docs/UNIFIED_READ_ONLY_MIGRATION.md。
本地测试已通过，但真实工程的新接口采集尚待部署后验收，不能将运行包生成视为数据迁移核对完成。
'@
[IO.File]::WriteAllText((Join-Path $stage '启动说明.txt'),$readme,[Text.UTF8Encoding]::new($true))
$files=@(Get-ChildItem -LiteralPath $stage -File -Recurse | ForEach-Object {
    [ordered]@{path=$_.FullName.Substring($stage.Length+1).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
$manifest=[ordered]@{package=$name;fileVersion='2.7.2.7';target='TIA Portal V21 / .NET Framework 4.8 x64';containsConnectionKey=$false;sourceCommit=$SourceCommit;sourceState=$(if($SourceCommit){'Release source identified by sourceCommit'}else{'Local development build; source commit not recorded'});realProjectAcceptance='pending deployment of new interfaces';files=$files}
[IO.File]::WriteAllText((Join-Path $stage 'manifest.json'),($manifest|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
$zip=Join-Path $output ($name+'.zip')
if(Test-Path -LiteralPath $zip){throw 'ZIP already exists; will not overwrite an earlier artifact.'}
Compress-Archive -LiteralPath $stage -DestinationPath $zip
Write-Output "Package: $zip"
Write-Output "SHA256: $((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash)"
