$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$distribution = Join-Path $workspace 'dist\IORecorder-win-x64'
if (-not (Test-Path -LiteralPath (Join-Path $distribution 'IORecorder.exe'))) { throw '先に scripts\build.ps1 を実行してください。' }
# Exclude superseded CLI files from early builds; the supported CLI is in cli/.
$items = Get-ChildItem -LiteralPath $distribution | Where-Object { -not $_.Name.StartsWith('iorecorder-cli.', [StringComparison]::OrdinalIgnoreCase) }
$archive = Join-Path $workspace 'dist\IORecorder-win-x64.zip'
Compress-Archive -LiteralPath $items.FullName -DestinationPath $archive -Force
Get-FileHash -LiteralPath $archive -Algorithm SHA256 | ForEach-Object { $_.Hash + '  IORecorder-win-x64.zip' } | Set-Content -LiteralPath ($archive + '.sha256') -Encoding ascii
Write-Host "配布用ZIP: $archive"
