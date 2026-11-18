param(
    [string]$Executable,
    [string]$InnoSetupCompiler
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable = Join-Path $workspace 'artifacts/portable/win-x64/PulseBattery.exe' }
if (-not $InnoSetupCompiler) { $InnoSetupCompiler = Join-Path $workspace '.local/inno-setup/ISCC.exe' }
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$InnoSetupCompiler = (Resolve-Path -LiteralPath $InnoSetupCompiler).Path

# A separate product identity and startup value keep the real installation untouched.
$testId = [Guid]::NewGuid().ToString('B').ToUpperInvariant()
$testName = 'Pulse Battery Installer Check ' + $testId.Substring(1, 8)
$startupName = 'PulseBattery.InstallerCheck.' + $testId.Substring(1, 8)
$testMutex = 'Local\' + $startupName
$checkRoot = Join-Path $workspace ('.local/installer-check/' + $testId.Trim('{}'))
$reportFolder = Join-Path $workspace 'artifacts/installer-checks'
$installFolder = Join-Path $checkRoot ('custom folder ' + [char]0x0422 + [char]0x0435 + [char]0x0441 + [char]0x0442)
$uninstallRegistryPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $testId + '_is1'
$runRegistryPath = 'Software\Microsoft\Windows\CurrentVersion\Run'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) ($testName + '\' + $testName + '.lnk')
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) ($testName + '.lnk')
$settingsPath = Join-Path $env:LOCALAPPDATA 'PulseBattery/settings.json'
$settingsHash = if (Test-Path -LiteralPath $settingsPath) { (Get-FileHash -LiteralPath $settingsPath).Hash } else { $null }
New-Item -ItemType Directory -Path $installFolder, $reportFolder -Force | Out-Null
$sourceFolder = Join-Path $checkRoot 'source'
New-Item -ItemType Directory -Path $sourceFolder -Force | Out-Null
$sourceSnapshot = Join-Path $sourceFolder 'PulseBattery.exe'
Copy-Item -LiteralPath $Executable -Destination $sourceSnapshot
$expectedExecutableHash = (Get-FileHash -LiteralPath $sourceSnapshot).Hash
$markerPath = Join-Path $installFolder 'unrelated-user-file.txt'
Set-Content -LiteralPath $markerPath -Value 'Preserve files not owned by the installer.'

function Invoke-SetupProcess([string]$Path, [string[]]$Arguments, [int]$ExpectedExitCode = 0) {
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    try {
        if (-not $process.WaitForExit(60000)) { $process.Kill(); throw ('Installer process timed out: ' + $Path) }
        $process.Refresh()
        if ($process.ExitCode -ne $ExpectedExitCode) { throw ('Installer process failed with code ' + $process.ExitCode + ': ' + $Path) }
    }
    finally { $process.Dispose() }
}
function Assert-Installed {
    if (-not (Test-Path -LiteralPath (Join-Path $installFolder 'PulseBattery.exe'))) { throw 'The chosen installation path was not used.' }
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($uninstallRegistryPath)
    try {
        if (-not $key -or $key.GetValue('DisplayName') -ne $testName) { throw 'The Control Panel uninstall entry is missing.' }
        if ($key.GetValue('InstallLocation').TrimEnd('\') -ne $installFolder) { throw 'Incorrect registered installation path.' }
        if ($key.GetValue('UninstallString') -ne ('"' + (Join-Path $installFolder 'unins000.exe') + '"')) { throw 'The uninstall command must quote paths containing spaces.' }
    }
    finally { if ($key) { $key.Dispose() } }
    if (-not (Test-Path -LiteralPath $shortcut)) { throw 'The Start menu shortcut is missing.' }
}
function Assert-Uninstalled {
    # Inno's original uninstaller exits before its temporary clone finishes cleanup.
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ((Test-Path -LiteralPath (Join-Path $installFolder 'unins000.exe')) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }
    if (Test-Path -LiteralPath (Join-Path $installFolder 'PulseBattery.exe')) { throw 'The installed application was not removed.' }
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($uninstallRegistryPath)
    if ($key) { $key.Dispose(); throw 'The Control Panel uninstall entry was not removed.' }
    if (Test-Path -LiteralPath $shortcut) { throw 'The Start menu shortcut was not removed.' }
    if (Test-Path -LiteralPath $desktopShortcut) { throw 'The desktop shortcut was not removed.' }
    if (-not (Test-Path -LiteralPath $markerPath)) { throw 'Uninstall removed an unrelated user file.' }
}
function Assert-StartupChoice([bool]$Enabled) {
    $exePath = Join-Path $installFolder 'PulseBattery.exe'
    $runKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runRegistryPath)
    try {
        $command = if ($runKey) { $runKey.GetValue($startupName) } else { $null }
        if ($Enabled -and $command -ne ('"' + $exePath + '" --autostart')) { throw 'Setup did not enable the selected Windows startup command.' }
        if (-not $Enabled -and $null -ne $command) { throw 'Setup did not disable Windows startup.' }
    }
    finally { if ($runKey) { $runKey.Dispose() } }
    $checks = Join-Path $workspace 'tests/PulseBattery.Checks/bin/Release/net10.0-windows/PulseBattery.Checks.dll'
    & dotnet $checks '--installer-startup' $exePath (Join-Path $checkRoot 'isolated-settings.json') $Enabled.ToString()
    if ($LASTEXITCODE -ne 0) { throw 'The app did not consume and save the installer startup choice correctly.' }
}

& $InnoSetupCompiler '/Q' "/DAppId=$testId" "/DAppName=$testName" "/DStartupValueName=$startupName" "/DAppMutex=$testMutex" "/DAppSource=$sourceSnapshot" "/DInstallerOutput=$checkRoot" '/DOutputName=InstallerCheck' (Join-Path $workspace 'installer/PulseBattery.iss')
if ($LASTEXITCODE -ne 0) { throw 'Test installer compilation failed.' }
$setupPath = Join-Path $checkRoot 'InstallerCheck.exe'
$uninstaller = Join-Path $installFolder 'unins000.exe'
$silentArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/LANG=english')
$startupKey = $null
try {
    Write-Host 'Checking the running-application installation guard...'
    $runningApp = New-Object System.Threading.Mutex($false, $testMutex)
    try { Invoke-SetupProcess $setupPath ($silentArguments + @(('/DIR="' + $installFolder + '"'))) 1 }
    finally { $runningApp.Dispose() }
    if (Test-Path -LiteralPath $uninstaller) { throw 'Setup must not install while the application mutex exists.' }
    Write-Host 'Checking installation to a custom Unicode path...'
    Invoke-SetupProcess $setupPath ($silentArguments + @('/DESKTOP=yes', ('/DIR="' + $installFolder + '"'), ('/LOG="' + (Join-Path $checkRoot 'install.log') + '"')))
    Assert-Installed
    Assert-StartupChoice $true
    if (-not (Test-Path -LiteralPath $desktopShortcut)) { throw 'The selected desktop shortcut was not created.' }
    # Omit /DIR during an upgrade: the existing custom path must be remembered.
    Write-Host 'Checking upgrade and remembered installation path...'
    Invoke-SetupProcess $setupPath ($silentArguments + @('/STARTUP=no', ('/LOG="' + (Join-Path $checkRoot 'upgrade.log') + '"')))
    Assert-Installed
    Assert-StartupChoice $false
    if (-not (Test-Path -LiteralPath $desktopShortcut)) { throw 'Upgrade did not preserve the desktop shortcut choice.' }
    if ((Get-FileHash -LiteralPath (Join-Path $installFolder 'PulseBattery.exe')).Hash -ne $expectedExecutableHash) { throw 'Installed executable differs from the published executable.' }

    $startupKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($runRegistryPath)
    $startupKey.SetValue($startupName, ('"' + (Join-Path $installFolder 'PulseBattery.exe') + '" --autostart'))
    $runningApp = New-Object System.Threading.Mutex($false, $testMutex)
    Write-Host 'Checking the running-application uninstall guard...'
    try { Invoke-SetupProcess $uninstaller $silentArguments 1 }
    finally { $runningApp.Dispose() }
    Assert-Installed
    Write-Host 'Checking uninstall and startup cleanup...'
    Invoke-SetupProcess $uninstaller ($silentArguments + @('/LOG="' + (Join-Path $checkRoot 'uninstall.log') + '"'))
    Assert-Uninstalled
    # The clone performs its post-uninstall registry cleanup after removing the EXE.
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ($null -ne $startupKey.GetValue($startupName) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if ($null -ne $startupKey.GetValue($startupName)) { throw 'Uninstall left its startup entry behind.' }

    Write-Host 'Checking preservation of another copy''s startup entry...'
    Invoke-SetupProcess $setupPath ($silentArguments + @('/STARTUP=no', ('/DIR="' + $installFolder + '"')))
    Assert-Installed
    Assert-StartupChoice $false
    if (Test-Path -LiteralPath $desktopShortcut) { throw 'An unselected desktop shortcut was created.' }
    $otherStartupCommand = '"C:\Other folder\PulseBattery.exe" --autostart'
    $startupKey.SetValue($startupName, $otherStartupCommand)
    Invoke-SetupProcess $uninstaller $silentArguments
    Assert-Uninstalled
    if ($startupKey.GetValue($startupName) -ne $otherStartupCommand) { throw 'Uninstall removed another copy''s startup entry.' }
    $currentSettingsHash = if (Test-Path -LiteralPath $settingsPath) { (Get-FileHash -LiteralPath $settingsPath).Hash } else { $null }
    if ($settingsHash -ne $currentSettingsHash) { throw 'Installation or uninstall modified the user settings.' }

    $report = [ordered]@{
        Passed = $true
        IsolatedProductId = $testId
        CustomPathWithSpacesAndUnicode = $installFolder
        ControlPanelRegistrationVerified = $true
        StartMenuShortcutVerified = $true
        RunningApplicationBlocksSetup = $true
        RunningApplicationBlocksUninstall = $true
        UpgradeReusedCustomPath = $true
        InstalledExecutableMatchesPublishedFile = $true
        ExecutableSHA256 = $expectedExecutableHash
        UninstallRemovedApplicationAndRegistration = $true
        OwnedStartupEntryRemoved = $true
        OtherCopyStartupEntryPreserved = $true
        UnrelatedFilePreserved = $true
        UserSettingsUnchanged = $true
        InstallerCanEnableAndDisableStartup = $true
        StartupEnabledByDefaultOnNewInstall = $true
        DesktopShortcutChoiceVerified = $true
        StartupChoiceSavedOnceWithoutResettingOtherPreferences = $true
        Logs = $checkRoot
    }
    $report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $reportFolder 'installer-checks.json') -Encoding UTF8
    $report | ConvertTo-Json -Depth 4 | Write-Output
}
catch {
    Write-Host $_.ScriptStackTrace
    throw
}
finally {
    try {
        if ((Test-Path -LiteralPath $uninstaller) -and (Test-Path -LiteralPath (Join-Path $installFolder 'PulseBattery.exe'))) {
            Invoke-SetupProcess $uninstaller $silentArguments
            Assert-Uninstalled
        }
    }
    finally {
        if ($startupKey) { $startupKey.DeleteValue($startupName, $false); $startupKey.Dispose() }
    }
}
