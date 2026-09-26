# Runs the tests, publishes a self-contained build and compiles the installer into dist\.
# The version comes from -Version (used by the release workflow) or else from src\BetterSTT\BetterSTT.csproj.
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

if (-not $Version) {
    $Version = ([xml](Get-Content src\BetterSTT\BetterSTT.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version '$Version' must look like 1.2.3" }
Write-Host "Building BetterSTT $Version"

dotnet test
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }

if (Test-Path publish) { Remove-Item publish -Recurse -Force }
dotnet publish src\BetterSTT -c Release -r win-x64 --self-contained true -o publish -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

& $iscc "/DAppVersion=$Version" installer\BetterSTT.iss
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
Get-ChildItem dist\*.exe | ForEach-Object { "Installer: $($_.FullName) ({0:N0} MB)" -f ($_.Length / 1MB) }
