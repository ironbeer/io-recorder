# -Version: a release build (the version of its tag, without the leading v). It must equal the Version in
# Directory.Build.props, so that the release and a local build of the same commit carry the same version.
param([switch]$SkipTests, [string]$Version)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$workspace = Split-Path -Parent $PSScriptRoot
$sdkCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$sdk = if ($sdkCommand) { $sdkCommand.Source } else { Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $sdk)) { throw '先に scripts\setup-dotnet.ps1 を実行してください。' }
$versionArgs = @()
if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "バージョンの形式が正しくありません: $Version（例: 0.2.0）" }
    $declared = ([xml](Get-Content -Raw -LiteralPath (Join-Path $workspace 'Directory.Build.props'))).Project.PropertyGroup.Version
    if ($declared -ne $Version) { throw "タグのバージョン $Version と Directory.Build.props の Version $declared が違います。Version を合わせてコミットしてから、タグを付け直してください。" }
    $versionArgs = @("-p:Version=$Version")
}
Push-Location $workspace
try {
    & $sdk build IORecorder.sln -c Release --nologo @versionArgs
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (-not $SkipTests) {
        & $sdk run --project tests/IoRecorder.Tests -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    $output = Join-Path $workspace 'dist\IORecorder-win-x64'
    # Published fresh every time, so files from an older layout (an old CLI next to a newer library) cannot remain.
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
    & $sdk publish src/IoRecorder.App/IoRecorder.App.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -o $output --nologo @versionArgs
    if ($LASTEXITCODE -ne 0) { throw 'GUI publish failed.' }
    & $sdk publish src/IoRecorder.Cli/IoRecorder.Cli.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -o (Join-Path $output 'cli') --nologo @versionArgs
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
