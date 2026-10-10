# Builds Claude Usage Orb.
#   .\tools\build.ps1            -> Release build into src\ClaudeUsageOrb\bin\Release
#   .\tools\build.ps1 -Publish   -> single-file exe into .\dist (needs the .NET 10 Desktop Runtime to run)
#   .\tools\build.ps1 -Run       -> build, then start the app
param([switch]$Publish, [switch]$Run, [string]$Dotnet = "")

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\ClaudeUsageOrb\ClaudeUsageOrb.csproj'

if (-not $Dotnet) {
    $local = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet-sdk\dotnet.exe'
    $Dotnet = if (Test-Path $local) { $local } else { 'dotnet' }
}

Get-Process ClaudeUsageOrb -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

if ($Publish) {
    $dist = Join-Path $root 'dist'
    & $Dotnet publish $proj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o $dist
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }
    Get-ChildItem $dist -Filter *.exe | ForEach-Object { "{0}  {1:N0} bytes" -f $_.FullName, $_.Length }
    $exe = Join-Path $dist 'ClaudeUsageOrb.exe'
} else {
    $log = & $Dotnet build $proj -c Release 2>&1
    $problems = $log | Select-String -Pattern ': (error|warning) ' | ForEach-Object { ($_.Line -replace '\s*\[[^\]]+\.csproj\]$', '').Trim() } | Sort-Object -Unique
    if ($problems) { $problems }
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
    "BUILD OK"
    $exe = Join-Path $root 'src\ClaudeUsageOrb\bin\Release\net10.0-windows\ClaudeUsageOrb.exe'
}

if ($Run) {
    Start-Process $exe
    "started $exe"
}
