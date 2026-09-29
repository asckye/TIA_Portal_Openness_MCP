param([string]$Python = 'python', [string]$EnvironmentPath = '')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (!$EnvironmentPath) { $EnvironmentPath = Join-Path $repo 'TiaMcp_Output/ecosystem-python' }
$environment = [IO.Path]::GetFullPath($EnvironmentPath)
& $Python -c 'import sys; assert sys.version_info >= (3,12), "Python 3.12+ required"'
if ($LASTEXITCODE) { throw 'Unsupported Python' }
& $Python -m venv $environment
if ($LASTEXITCODE) { throw 'venv creation failed' }
$exe = Join-Path $environment 'Scripts/python.exe'
$source = Join-Path $repo 'tools/third-party/siemens-plc-tools'
$packages = @($source)
foreach ($package in @('plc-core','plc-code','plc-iol','plc-modbus','plc-net','plc-sim','plc-sup','plc-trace')) {
    $path = Join-Path $source "packages/$package"
    if ($package -eq 'plc-core') { $path += '[opcua]' }
    if ($package -in @('plc-code','plc-sim')) { $path += '[web]' }
    $packages += $path
}
& $exe -m pip install @packages pytest pytest-asyncio pytest-cov reportlab
if ($LASTEXITCODE) { throw 'PLC Tools dependency installation failed' }
Write-Output "Installed. Set TIA_MCP_PLC_TOOLS_PYTHON=$exe in the MCP server environment."
Write-Output 'This script installs local tooling only; it does not connect to any PLC or TIA project.'
