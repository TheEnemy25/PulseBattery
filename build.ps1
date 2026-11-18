param([switch]$SelfContained, [switch]$SingleFile, [switch]$Installer, [string]$InnoSetupCompiler)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.local'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

dotnet build PulseBattery.slnx -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
dotnet run --project tests/PulseBattery.Checks -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }
dotnet run --project tests/PulseBattery.Checks -c Release --no-build -- --simulate-timeout
if ($LASTEXITCODE -ne 2) { throw 'Timeout handling regression: expected a normal exit with code 2.' }
$presentationApp = Join-Path $PSScriptRoot 'src/PulseBattery.App/bin/Release/net10.0-windows/PulseBattery.exe'
$presentation = Start-Process -FilePath $presentationApp -ArgumentList '--check-presentation artifacts/presentation-checks' -WindowStyle Hidden -PassThru
if (-not $presentation.WaitForExit(30000)) { $presentation.Kill(); throw 'Presentation checks timed out.' }
$presentation.Refresh()
if ($presentation.ExitCode -ne 0) { throw 'Presentation checks failed. See artifacts/presentation-checks/presentation-checks.json.' }
$background = Start-Process -FilePath $presentationApp -ArgumentList '--check-background artifacts/background-checks' -WindowStyle Hidden -PassThru
if (-not $background.WaitForExit(30000)) { $background.Kill(); throw 'Background checks timed out.' }
$background.Refresh()
if ($background.ExitCode -ne 0) { throw 'Background checks failed. See artifacts/background-checks/background-checks.json.' }
if ($SingleFile -or $Installer) {
    $env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.local/nuget/packages'
    $portableOutput = Join-Path $PSScriptRoot 'artifacts/portable/win-x64'
    # Keep embedded assemblies memory-mappable instead of decompressing them into private RAM.
    dotnet publish src/PulseBattery.App -c Release -r win-x64 --self-contained true -o $portableOutput --nologo `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=false -p:PublishTrimmed=false -p:DebugType=embedded
    if ($LASTEXITCODE -ne 0) { throw 'Single-file publish failed.' }
    $publishedFiles = @(Get-ChildItem -LiteralPath $portableOutput -File)
    if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Name -ne 'PulseBattery.exe') { throw 'Expected exactly one portable executable.' }
    $portableExe = $publishedFiles[0].FullName
    if (-not $Installer) {
        $portableExe = Join-Path $PSScriptRoot 'PulseBattery.exe'
        Copy-Item -LiteralPath $publishedFiles[0].FullName -Destination $portableExe -Force
    }
    & (Join-Path $PSScriptRoot 'scripts/Verify-Portable.ps1') -Executable $portableExe
    & (Join-Path $PSScriptRoot 'scripts/Build-PortableZip.ps1') -Executable $portableExe
    if ($Installer) {
        & (Join-Path $PSScriptRoot 'scripts/Build-Installer.ps1') -Executable $portableExe -InnoSetupCompiler $InnoSetupCompiler
    }
    else { Write-Host ('Ready to share: ' + $portableExe) }
    return
}
$publishArguments = @('publish', 'src/PulseBattery.App', '-c', 'Release', '-o', 'artifacts/PulseBattery', '--nologo')
if ($SelfContained) { $publishArguments += @('-r', 'win-x64', '--self-contained', 'true') }
else { $publishArguments += @('--self-contained', 'false') }
dotnet @publishArguments
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath README.md -Destination artifacts/PulseBattery/README.md -Force
$packageFiles = Get-ChildItem -LiteralPath artifacts/PulseBattery -File | Where-Object { $_.Extension -in '.exe', '.dll', '.pdb', '.json', '.md' }
Compress-Archive -LiteralPath $packageFiles.FullName -DestinationPath artifacts/PulseBattery-win-x64.zip -Force
Write-Host 'Ready: artifacts/PulseBattery/PulseBattery.exe'
