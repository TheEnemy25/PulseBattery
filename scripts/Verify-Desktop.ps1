param([string]$AppPath, [string]$OutputFolder)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
if (-not $AppPath) { $AppPath = Join-Path $workspace 'src/PulseBattery.App/bin/Release/net10.0-windows/PulseBattery.exe' }
if (-not $OutputFolder) { $OutputFolder = Join-Path $workspace 'artifacts/desktop-checks' }
$AppPath = (Resolve-Path -LiteralPath $AppPath).Path
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class PulseDesktopChecks {
    public delegate bool EnumProc(IntPtr h,IntPtr p);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p,EnumProc c,IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c,string n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out Rect r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key,byte scan,uint flags,IntPtr extra);
    public static void WinD() { keybd_event(0x5B,0,0,IntPtr.Zero); keybd_event(0x44,0,0,IntPtr.Zero); keybd_event(0x44,0,2,IntPtr.Zero); keybd_event(0x5B,0,2,IntPtr.Zero); }
    public static IntPtr Panel(uint pid,IntPtr bar) {
        IntPtr result=IntPtr.Zero;
        EnumChildWindows(bar,(h,p)=> { uint id; GetWindowThreadProcessId(h,out id); if(id==pid && IsWindowVisible(h)){ result=h; return false; } return true; },IntPtr.Zero);
        return result;
    }
    public static int Contrast(Bitmap bitmap,bool light) {
        var data=bitmap.LockBits(new Rectangle(0,0,bitmap.Width,bitmap.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try { var pixels=new byte[data.Stride*bitmap.Height]; Marshal.Copy(data.Scan0,pixels,0,pixels.Length); int count=0;
            for(int i=0;i<pixels.Length;i+=4) { int total=pixels[i]+pixels[i+1]+pixels[i+2]; if(light ? total<210 : total>510) count++; }
            return count;
        } finally { bitmap.UnlockBits(data); }
    }
}
'@
$bar = [PulseDesktopChecks]::FindWindow('Shell_TrayWnd', $null)
if ($bar -eq [IntPtr]::Zero) { throw 'Run this check on the interactive Explorer desktop.' }
$app = Start-Process -FilePath $AppPath -ArgumentList '--tray' -WindowStyle Hidden -PassThru
try {
    $panel = [IntPtr]::Zero
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        $panel = [PulseDesktopChecks]::Panel([uint32]$app.Id, $bar)
        if ($panel -ne [IntPtr]::Zero) { break }
        if ($app.HasExited) { throw ('App exited before attaching to taskbar: ' + $app.ExitCode) }
        Start-Sleep -Milliseconds 100
    }
    if ($panel -eq [IntPtr]::Zero) { throw 'No visible taskbar child was found.' }
    Start-Sleep -Milliseconds 250
    $rect = New-Object PulseDesktopChecks+Rect
    [void][PulseDesktopChecks]::GetWindowRect($panel, [ref]$rect)
    $bitmap = New-Object System.Drawing.Bitmap ($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $light = (Get-ItemPropertyValue -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Name SystemUsesLightTheme) -ne 0
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
    $baseline = [PulseDesktopChecks]::Contrast($bitmap, $light)
    if ($baseline -lt 20) { throw 'Battery glyphs were not rendered before the check.' }
    $threshold = [Math]::Max(5, [int]($baseline * 0.1))
    function Measure-PanelFrames([string]$label) {
        $missing = 0
        $minimum = [int]::MaxValue
        for ($frame = 0; $frame -lt 100; $frame++) {
            [void][PulseDesktopChecks]::GetWindowRect($panel, [ref]$rect)
            $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
            $contrast = [PulseDesktopChecks]::Contrast($bitmap, $light)
            $minimum = [Math]::Min($minimum, $contrast)
            if (-not [PulseDesktopChecks]::IsWindowVisible($panel) -or $contrast -lt $threshold) { $missing++ }
            Start-Sleep -Milliseconds 8
        }
        return [PSCustomObject]@{ Transition = $label; Samples = 100; MissingFrames = $missing; MinimumGlyphPixels = $minimum }
    }
    $results = @()
    $shell = New-Object -ComObject Shell.Application
    foreach ($method in @('Win+D', 'Show desktop')) {
        $toggled = $false
        try {
            if ($method -eq 'Win+D') { [PulseDesktopChecks]::WinD() } else { $shell.ToggleDesktop() }
            $toggled = $true
            $results += Measure-PanelFrames ($method + ': desktop')
        }
        finally {
            if ($toggled) {
                if ($method -eq 'Win+D') { [PulseDesktopChecks]::WinD() } else { $shell.ToggleDesktop() }
            }
        }
        $results += Measure-PanelFrames ($method + ': restored')
    }
    $passed = @($results | Where-Object { $_.MissingFrames -ne 0 }).Count -eq 0
    New-Item -ItemType Directory -Path $OutputFolder -Force | Out-Null
    $report = [PSCustomObject]@{ Passed = $passed; NativeTaskbarChild = $true; BaselineGlyphPixels = $baseline; SamplingDelayMilliseconds = 8; Results = $results }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputFolder 'desktop-checks.json') -Encoding UTF8
    $report | ConvertTo-Json -Depth 5 | Write-Output
    $graphics.Dispose(); $bitmap.Dispose()
    if (-not $passed) { throw 'The panel disappeared during a desktop transition.' }
}
finally {
    if (-not $app.HasExited) { $app.Kill(); [void]$app.WaitForExit(5000) }
}
