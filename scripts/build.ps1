param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$workspace = Split-Path -Parent $PSScriptRoot
$sdkCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$sdk = if ($sdkCommand) { $sdkCommand.Source } else { Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $sdk)) { throw '先に scripts\setup-dotnet.ps1 を実行してください。' }
Push-Location $workspace
try {
    & $sdk build IORecorder.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (-not $SkipTests) {
        & $sdk run --project tests/IoRecorder.Tests -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    $output = Join-Path $workspace 'dist\IORecorder-win-x64'
    # Published fresh every time, so files from an older layout (an old CLI next to a newer library) cannot remain.
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
    & $sdk publish src/IoRecorder.App/IoRecorder.App.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw 'GUI publish failed.' }
    & $sdk publish src/IoRecorder.Cli/IoRecorder.Cli.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -o (Join-Path $output 'cli') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }
    Copy-Item -LiteralPath (Join-Path $workspace 'README.md') -Destination (Join-Path $output 'README.md')
    Copy-Item -LiteralPath (Join-Path $workspace 'LICENSE') -Destination (Join-Path $output 'LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $workspace 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $output 'THIRD-PARTY-NOTICES.md')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'verify-etw.ps1') -Destination (Join-Path $output 'verify-etw.ps1')
    Copy-Item -LiteralPath (Join-Path $workspace 'Verify ETW.cmd') -Destination (Join-Path $output 'Verify ETW.cmd')
    Copy-Item -LiteralPath (Join-Path $workspace 'licenses') -Destination $output -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $workspace 'samples') -Destination $output -Recurse -Force
    Write-Host "実行ファイル: $output\IORecorder.exe"
} finally { Pop-Location }
