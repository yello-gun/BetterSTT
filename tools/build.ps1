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

# The speech engine's native libraries need Microsoft's Visual C++ runtime (MSVCP140, VCRUNTIME140, VCRUNTIME140_1 and,
# for the processor engine, VCOMP140). A PC that has never had the "Visual C++ Redistributable" installed doesn't have
# them, and the engine then can't load. They're shipped with the app (app-local deployment of the redistributable).
# Windows loads the engine's DLLs searching their own folder first and then System32, but not the app's folder, so the
# runtime has to sit next to each engine's DLLs.
foreach ($dll in 'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'vcomp140.dll') {
    $source = Join-Path $env:SystemRoot "System32\$dll"
    if (-not (Test-Path $source)) { throw "$dll was not found in System32. Install the Visual C++ Redistributable on the build machine." }
    foreach ($engine in 'publish\runtimes\win-x64', 'publish\runtimes\vulkan\win-x64') {
        if (-not (Test-Path $engine)) { throw "$engine is missing from the published app." }
        Copy-Item $source $engine -Force
    }
}

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

& $iscc "/DAppVersion=$Version" installer\BetterSTT.iss
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
Get-ChildItem dist\*.exe | ForEach-Object { "Installer: $($_.FullName) ({0:N0} MB)" -f ($_.Length / 1MB) }
