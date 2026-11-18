param([string]$ExpectedVersion, [string]$InnoSetupCompiler)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $workspace
$env:DOTNET_CLI_HOME = Join-Path $workspace '.local'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:NUGET_PACKAGES = Join-Path $workspace '.local/nuget/packages'
[xml]$project = Get-Content -Raw -LiteralPath 'src/PulseBattery.App/PulseBattery.App.csproj'
$version = [string]$project.Project.PropertyGroup.Version
if ($ExpectedVersion -and $ExpectedVersion.TrimStart('v') -ne $version) { throw "Release version $ExpectedVersion does not match application version $version." }
if (-not $InnoSetupCompiler) { $InnoSetupCompiler = & (Join-Path $PSScriptRoot 'Get-InnoSetup.ps1') }

dotnet build PulseBattery.slnx -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
dotnet run --project tests/PulseBattery.Checks -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Release checks failed.' }
dotnet run --project tests/PulseBattery.Checks -c Release --no-build -- --simulate-timeout
if ($LASTEXITCODE -ne 2) { throw 'Timeout handling regression: expected exit code 2.' }

# Hosted runners have no interactive Explorer desktop or physical peripherals.
# Desktop and real-hardware verification remains in build.ps1 / Verify-Portable.ps1.
$portableFolder = Join-Path $workspace 'artifacts/portable/win-x64'
dotnet publish src/PulseBattery.App -c Release -r win-x64 --self-contained true -o $portableFolder --nologo `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=false -p:PublishTrimmed=false -p:DebugType=embedded
if ($LASTEXITCODE -ne 0) { throw 'Release publish failed.' }
$files = @(Get-ChildItem -LiteralPath $portableFolder -File)
if ($files.Count -ne 1 -or $files[0].Name -ne 'PulseBattery.exe') { throw 'Expected exactly one self-contained executable.' }
& (Join-Path $PSScriptRoot 'Build-PortableZip.ps1') -Executable $files[0].FullName
& (Join-Path $PSScriptRoot 'Build-Installer.ps1') -Executable $files[0].FullName -InnoSetupCompiler $InnoSetupCompiler
& (Join-Path $PSScriptRoot 'Prepare-ReleaseAssets.ps1') -ExpectedVersion $version
