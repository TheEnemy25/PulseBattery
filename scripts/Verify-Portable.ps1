param([string]$Executable = (Join-Path (Split-Path $PSScriptRoot) 'PulseBattery.exe'))
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot
$checkRoot = Join-Path $workspace '.local/portable-check'
$applicationFolder = Join-Path $checkRoot 'application'
$reportFolder = Join-Path $workspace 'artifacts/portable-checks'
$bundleFolder = Join-Path $checkRoot 'bundle-cache'
New-Item -ItemType Directory -Path $applicationFolder, $reportFolder, $bundleFolder -Force | Out-Null
$isolatedExe = Join-Path $applicationFolder 'PulseBattery.exe'
Copy-Item -LiteralPath $Executable -Destination $isolatedExe -Force
$applicationFiles = @(Get-ChildItem -LiteralPath $applicationFolder -File)
if ($applicationFiles.Count -ne 1 -or $applicationFiles[0].Name -ne 'PulseBattery.exe') { throw 'The isolated test folder must contain only the executable.' }

function Start-PortableCheck([string]$Mode, [string]$OutputFolder) {
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $isolatedExe
    $startInfo.WorkingDirectory = $applicationFolder
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.Arguments = $Mode + ' "' + $OutputFolder + '"'
    # Make installed .NET unavailable to this child without changing the user's environment.
    $missingRuntime = Join-Path $checkRoot 'no-installed-dotnet'
    $startInfo.EnvironmentVariables['DOTNET_ROOT'] = $missingRuntime
    $startInfo.EnvironmentVariables['DOTNET_ROOT_X64'] = $missingRuntime
    $startInfo.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $startInfo.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $bundleFolder
    return [System.Diagnostics.Process]::Start($startInfo)
}

$presentationFolder = Join-Path $reportFolder 'presentation'
$presentation = Start-PortableCheck '--check-presentation' $presentationFolder
$runtimeModules = @()
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 100
        $presentation.Refresh()
        if ($presentation.HasExited) { break }
        $runtimeModules = @($presentation.Modules | Where-Object { $_.ModuleName -in @('PulseBattery.exe', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'wpfgfx_cor3.dll', 'PresentationNative_cor3.dll') } | Select-Object ModuleName, FileName)
    } while ((@($runtimeModules | Where-Object { $_.ModuleName -in @('wpfgfx_cor3.dll', 'PresentationNative_cor3.dll') }).Count -lt 2) -and [DateTime]::UtcNow -lt $deadline)
    if (-not $presentation.WaitForExit(30000)) { throw 'Portable presentation checks timed out.' }
    $presentation.Refresh()
    if ($presentation.ExitCode -ne 0) { throw 'Portable WPF/SVG presentation checks failed.' }
}
finally { if (-not $presentation.HasExited) { $presentation.Kill(); [void]$presentation.WaitForExit(5000) } }
# Single-file hosts can link CoreCLR into the EXE instead of extracting coreclr.dll.
# Every observed runtime component must therefore come from the EXE or its bundle cache.
$runtimeFromBundle = $runtimeModules.Count -ge 3 -and @($runtimeModules | Where-Object {
    $_.FileName -ne $isolatedExe -and -not $_.FileName.StartsWith($bundleFolder + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}).Count -eq 0
if (-not $runtimeFromBundle) { throw 'The test did not confirm the bundled runtime components were loaded.' }

$hardwareFolder = Join-Path $reportFolder 'hardware'
$hardware = Start-PortableCheck '--smoke-test' $hardwareFolder
try {
    if (-not $hardware.WaitForExit(30000)) { throw 'Portable hardware checks timed out.' }
    $hardware.Refresh()
    if ($hardware.ExitCode -ne 0) { throw 'Portable hardware checks failed.' }
}
finally { if (-not $hardware.HasExited) { $hardware.Kill(); [void]$hardware.WaitForExit(5000) } }
$report = [ordered]@{
    Passed = $true
    Executable = (Resolve-Path -LiteralPath $Executable).Path
    Bytes = (Get-Item -LiteralPath $Executable).Length
    SHA256 = (Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash
    IsolatedApplicationFiles = @($applicationFiles.Name)
    InstalledRuntimePathsDisabledForTest = $true
    RuntimeLoadedFromBundle = $runtimeFromBundle
    RuntimeModules = $runtimeModules
    WpfAndSvgChecksPassed = $true
    RealHardwareChecksPassed = $true
}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $reportFolder 'portable-checks.json') -Encoding UTF8
Write-Host ('Portable executable verified: {0:N1} MB' -f ($report.Bytes / 1MB))
