# Runs the tests, publishes a self-contained build and compiles the installer into dist\.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

dotnet test
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }

if (Test-Path publish) { Remove-Item publish -Recurse -Force }
dotnet publish src\BetterTTS -c Release -r win-x64 --self-contained true -o publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

& $iscc installer\BetterTTS.iss
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
Get-ChildItem dist\*.exe | ForEach-Object { "Installer: $($_.FullName) ({0:N0} MB)" -f ($_.Length / 1MB) }
