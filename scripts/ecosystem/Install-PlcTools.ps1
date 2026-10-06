param([string]$Python = 'python', [string]$EnvironmentPath = '')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (!$EnvironmentPath) {
    if (!$env:LOCALAPPDATA -or ![IO.Path]::IsPathRooted($env:LOCALAPPDATA)) { throw "IO_FAILED: LocalAppData is unavailable: $env:LOCALAPPDATA/TiaMcp/ecosystem-python" }
    $EnvironmentPath = Join-Path $env:LOCALAPPDATA 'TiaMcp/ecosystem-python'
}
if (![IO.Path]::IsPathRooted($EnvironmentPath)) { throw "IO_FAILED: EnvironmentPath must be absolute: $EnvironmentPath" }
$environment = [IO.Path]::GetFullPath($EnvironmentPath)
try {
    [IO.Directory]::CreateDirectory($environment) | Out-Null
    $probePath = Join-Path $environment ('.write-probe-' + [Guid]::NewGuid().ToString('N'))
    $probe = [IO.File]::Open($probePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $probe.WriteByte(0) } finally { $probe.Dispose(); [IO.File]::Delete($probePath) }
} catch { throw "IO_FAILED: Python environment is not writable: $environment; $($_.Exception.Message)" }
& $Python -c 'import sys; assert sys.version_info >= (3,12), "Python 3.12+ required"'
if ($LASTEXITCODE) { throw 'Unsupported Python' }
& $Python -m venv $environment
if ($LASTEXITCODE) { throw "IO_FAILED: venv creation failed: $environment" }
$exe = Join-Path $environment 'Scripts/python.exe'
# The bridges import the pinned source trees directly. Only their external runtime
# dependencies are installed; local pyproject.toml/build tooling is not delivered.
# Check-BundleLayout.py guards this list against the pinned upstream project metadata.
$dependencies = @(
    'asyncua>=1.1.0',
    'click>=8.0',
    'click>=8.1.0',
    'fastapi>=0.109.0',
    'fastapi>=0.128.0',
    'httpx>=0.27.0',
    'jinja2>=3.0.0',
    'mkdocs-material>=9.7.0',
    'mkdocs>=1.6.1',
    'mkdocstrings[python]>=0.24.0',
    'msgpack>=1.0.0',
    'openpyxl>=3.1.0',
    'psycopg[binary]>=3.1.0',
    'pydantic>=2.0.0',
    'pydantic>=2.6.0',
    'pymodbus>=3.6,<4.0',
    'python-docx>=1.0.0',
    'pyyaml>=6.0.0',
    'redis>=5.0.0',
    'rich>=13.0',
    'rich>=13.0.0',
    'scapy>=2.5',
    'uvicorn[standard]>=0.27.0',
    'uvicorn[standard]>=0.40.0'
)
& $exe -m pip install @dependencies pytest pytest-asyncio pytest-cov reportlab
if ($LASTEXITCODE) { throw 'PLC Tools dependency installation failed' }
Write-Output "Installed. Set TIA_MCP_PLC_TOOLS_PYTHON=$exe in the MCP server environment."
Write-Output 'This script installs local tooling only; it does not connect to any PLC or TIA project.'
