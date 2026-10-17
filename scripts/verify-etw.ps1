param([string]$Output)
$ErrorActionPreference = 'Stop'
$isAdmin = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$scriptPath = $PSCommandPath
if (-not $isAdmin) {
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $scriptPath + '"'
    if ($Output) { $arguments += ' -Output "' + $Output.Replace('"','') + '"' }
    Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList $arguments
    exit
}
$candidate = Join-Path $PSScriptRoot 'cli\iorecorder-cli.exe'
if (-not (Test-Path -LiteralPath $candidate)) { $candidate = Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\IORecorder-win-x64\cli\iorecorder-cli.exe' }
if (-not (Test-Path -LiteralPath $candidate)) { throw '実行ファイルがありません。先にビルドしてください。' }
if (-not $Output) { $Output = Join-Path ([Environment]::GetFolderPath('MyDocuments')) ('IORecorder\Verification\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '_verify-etw') }
New-Item -ItemType Directory -Force -Path $Output | Out-Null
& $candidate verify-etw --out $Output *> (Join-Path $Output 'console.log')
$resultCode = $LASTEXITCODE
$status = if ($resultCode -eq 0) { 'PASS' } else { 'FAIL' }
$lines = @("ETW integration verification: $status", "Exit code: $resultCode", "Output: $Output", '')
$verification = Join-Path $Output 'verification.json'
if (Test-Path -LiteralPath $verification) {
    # INFO items (rename destination, paging split) describe this PC's ETW behaviour and do not decide PASS/FAIL.
    foreach ($check in (Get-Content -LiteralPath $verification -Raw -Encoding utf8 | ConvertFrom-Json).checks) { $lines += ('[{0}] {1} — {2}' -f $check.status, $check.name, $check.detail) }
}
$lines -join "`r`n" | Set-Content -LiteralPath (Join-Path $Output 'RESULT.txt') -Encoding utf8
Start-Process notepad.exe -ArgumentList ('"' + (Join-Path $Output 'RESULT.txt') + '"') -WindowStyle Hidden
