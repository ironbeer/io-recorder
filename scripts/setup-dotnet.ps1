$ErrorActionPreference = 'Stop'
$setupDirectory = Join-Path $env:LOCALAPPDATA 'IORecorder\setup'
New-Item -ItemType Directory -Force -Path $setupDirectory | Out-Null
$installScript = Join-Path $setupDirectory 'dotnet-install.ps1'
Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installScript
& $installScript -Channel '10.0' -InstallDir (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet') -NoPath
if ($LASTEXITCODE -ne 0) { throw '.NET installation failed.' }
$dotnetDirectory = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
$currentUserPath = [Environment]::GetEnvironmentVariable('Path','User')
if (($currentUserPath -split ';') -notcontains $dotnetDirectory) { [Environment]::SetEnvironmentVariable('Path',($dotnetDirectory+';'+$currentUserPath),'User') }
[Environment]::SetEnvironmentVariable('DOTNET_ROOT',$dotnetDirectory,'User')
Write-Host 'SDKの準備ができました。scripts\build.ps1 でビルドできます。'
