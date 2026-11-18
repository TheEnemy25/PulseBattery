param(
    [Parameter(Mandatory=$true)][string]$TestInstaller,
    [Parameter(Mandatory=$true)][string]$TestName,
    [ValidateSet('ukrainian','english')][string]$Language = 'ukrainian'
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$reportFolder = Join-Path $workspace 'artifacts/installer-checks'
New-Item -ItemType Directory -Path $reportFolder -Force | Out-Null
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class InstallerCapture {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
'@
$launcher = Start-Process -FilePath $TestInstaller -ArgumentList ('/LANG=' + $Language) -WindowStyle Hidden -PassThru
$window = $null
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)
        $window = $windows | Where-Object { $_.Current.Name.Contains($TestName) } | Select-Object -First 1
        if ($window) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $window) { throw 'The isolated test installer window was not found.' }
    $elements = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    $visible = @($elements | Where-Object { -not $_.Current.IsOffscreen })
    $names = @($visible | ForEach-Object { $_.Current.Name })
    $source = Get-Content -Encoding UTF8 -LiteralPath (Join-Path $workspace 'installer/PulseBattery.iss')
    function Get-Message([string]$Name) {
        $prefix = $Language + '.' + $Name + '='
        return ($source | Where-Object { $_.StartsWith($prefix) }).Substring($prefix.Length)
    }
    if ((Get-Message 'InstallTitle') -notin $names) { throw 'The first page is not the combined installation page.' }
    # Inno's native controls are exposed as panes by the hidden-window UIA provider.
    $checkboxes = @($elements | Where-Object { $_.Current.ClassName -eq 'TNewCheckBox' })
    if ($checkboxes.Count -ne 3) { throw ('Expected all three options on the first page. Found ' + $checkboxes.Count) }
    $startup = $checkboxes | Where-Object { $_.Current.Name -eq (Get-Message 'StartupCheckbox') }
    $launch = $checkboxes | Where-Object { $_.Current.Name -eq (Get-Message 'LaunchCheckbox') }
    if (-not $startup -or -not $launch) { throw 'The startup or launch choice is missing.' }
    $startupHandle = [IntPtr]$startup.Current.NativeWindowHandle
    $launchHandle = [IntPtr]$launch.Current.NativeWindowHandle
    if ([InstallerCapture]::SendMessage($startupHandle, 0xF0, [IntPtr]::Zero, [IntPtr]::Zero).ToInt32() -ne 1 -or
        [InstallerCapture]::SendMessage($launchHandle, 0xF0, [IntPtr]::Zero, [IntPtr]::Zero).ToInt32() -ne 1) {
        throw 'Startup and launch must be checked by default on a new installation.'
    }
    [void][InstallerCapture]::SendMessage($startupHandle, 0xF5, [IntPtr]::Zero, [IntPtr]::Zero)
    if ([InstallerCapture]::SendMessage($startupHandle, 0xF0, [IntPtr]::Zero, [IntPtr]::Zero).ToInt32() -ne 0) { throw 'The user cannot disable startup.' }
    [void][InstallerCapture]::SendMessage($startupHandle, 0xF5, [IntPtr]::Zero, [IntPtr]::Zero)
    $folderEdits = @($elements | Where-Object { $_.Current.Name.StartsWith($env:LOCALAPPDATA + '\Programs\') -and $_.Current.Name.EndsWith($TestName) })
    if ($folderEdits.Count -ne 1) { throw 'The installation folder must be on the same page.' }

    $rect = $window.Current.BoundingRectangle
    $bitmap = New-Object System.Drawing.Bitmap ([int]$rect.Width), ([int]$rect.Height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $dc = $graphics.GetHdc()
        try { [void][InstallerCapture]::PrintWindow([IntPtr]$window.Current.NativeWindowHandle, $dc, 0) }
        finally { $graphics.ReleaseHdc($dc) }
        $bitmap.Save((Join-Path $reportFolder ('installer-options-' + $Language + '.png')), [Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $graphics.Dispose(); $bitmap.Dispose() }
    [ordered]@{ Passed = $true; Language = $Language; FolderAndThreeOptionsOnFirstPage = $true; StartupCheckedByDefault = $true; LaunchCheckedByDefault = $true; UserCanDisableStartup = $true; VisibleText = $names } |
        ConvertTo-Json -Depth 3 | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $reportFolder ('installer-ui-' + $Language + '.json'))
    Write-Output ('PASS: Combined installer page, checked defaults and startup toggle verified in ' + $Language + '.')
}
finally {
    # No installation was started. Close only the uniquely named test wizard we launched.
    if ($window) {
        $wizardProcess = Get-Process -Id $window.Current.ProcessId -ErrorAction SilentlyContinue
        if ($wizardProcess) { $wizardProcess.Kill(); [void]$wizardProcess.WaitForExit(5000) }
    }
    if (-not $launcher.HasExited) { $launcher.Kill(); [void]$launcher.WaitForExit(5000) }
    $launcher.Dispose()
}
