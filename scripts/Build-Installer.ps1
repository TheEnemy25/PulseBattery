param(
    [string]$Executable,
    [string]$InnoSetupCompiler
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable = Join-Path $workspace 'artifacts/portable/win-x64/PulseBattery.exe' }
$Executable = (Resolve-Path -LiteralPath $Executable).Path

if (-not $InnoSetupCompiler) {
    $compilerCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($compilerCommand) { $InnoSetupCompiler = $compilerCommand.Source }
    else {
        $candidates = @(
            (Join-Path $workspace '.local/inno-setup/ISCC.exe'),
            "${env:ProgramFiles}\Inno Setup 7\ISCC.exe",
            "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
        )
        $InnoSetupCompiler = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
}
if (-not $InnoSetupCompiler) {
    throw 'Inno Setup is required to build the installer. Install it from https://jrsoftware.org/isdl.php or pass -InnoSetupCompiler <path to ISCC.exe>.'
}
$InnoSetupCompiler = (Resolve-Path -LiteralPath $InnoSetupCompiler).Path
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($Executable).ProductVersion.Split('+')[0]
if ($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "Invalid application version: $version" }
$outputFolder = Join-Path $workspace 'artifacts/installer'
& $InnoSetupCompiler '/Qp' "/DAppSource=$Executable" "/DAppVersion=$version" "/DInstallerOutput=$outputFolder" (Join-Path $workspace 'installer/PulseBattery.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$installer = Join-Path $outputFolder 'PulseBattery-Setup.exe'
$shareInstaller = Join-Path $workspace 'PulseBattery-Setup.exe'
Copy-Item -LiteralPath $installer -Destination $shareInstaller -Force
Write-Host ('Ready to install or share: ' + $shareInstaller)
