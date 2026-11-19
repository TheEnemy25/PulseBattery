param()
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$native = Join-Path $workspace 'native/WirelessBatteryBackend'
$output = Join-Path $workspace 'artifacts/device-backend'
$downloads = Join-Path $workspace '.local/downloads'
New-Item -ItemType Directory -Path $output, $downloads -Force | Out-Null

# Rebuild when any driver, bridge or build instruction changes.
$sourceFiles = @(Get-ChildItem -LiteralPath $native -File -Recurse | Sort-Object FullName)
$verifyScript = Join-Path $PSScriptRoot 'Verify-DeviceBackend.ps1'
$fingerprintText = ((@($sourceFiles.FullName) + $PSCommandPath + $verifyScript) | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }) -join ''
$sha = [Security.Cryptography.SHA256]::Create()
try { $fingerprint = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($fingerprintText))).Replace('-', '') }
finally { $sha.Dispose() }
$marker = Join-Path $output 'build.sha256'
$package = Join-Path $output 'DeviceBackend.zip'
if ((Test-Path -LiteralPath $marker) -and (Test-Path -LiteralPath $package) -and (Get-Content -Raw -LiteralPath $marker).Trim() -eq $fingerprint) { return }

function Get-VerifiedFile([string]$Url, [string]$Path, [string]$Hash) {
    if (-not (Test-Path -LiteralPath $Path)) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $Url -OutFile $Path -UseBasicParsing
    }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Hash) { throw "Pinned SHA256 mismatch: $Path" }
}

$toolName = 'llvm-mingw-20261117-ucrt-x86_64'
$toolZip = Join-Path $downloads "$toolName.zip"
$compiler = Join-Path $workspace ".local/$toolName/bin/clang++.exe"
if (-not (Test-Path -LiteralPath $compiler)) {
    Get-VerifiedFile "https://github.com/mstorsjo/llvm-mingw/releases/download/20261117/$toolName.zip" $toolZip '4D002E330A0F703625AA5CCBC37E96F8F824413E163BFC7336774AABD2C3A412'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $toolArchive = [IO.Compression.ZipFile]::OpenRead($toolZip)
    try {
        $toolRoot = [IO.Path]::GetFullPath((Join-Path $workspace '.local')) + [IO.Path]::DirectorySeparatorChar
        foreach ($entry in $toolArchive.Entries) {
            $target = [IO.Path]::GetFullPath((Join-Path $toolRoot $entry.FullName))
            if (-not $target.StartsWith($toolRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid compiler archive path.' }
            if ($entry.Name.Length -eq 0) { [IO.Directory]::CreateDirectory($target) | Out-Null }
            else {
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
            }
        }
    } finally { $toolArchive.Dispose() }
}
$headsetExe = Join-Path $output 'headsetcontrol.exe'
Get-VerifiedFile 'https://github.com/Sapd/HeadsetControl/releases/download/4.3.0/headsetcontrol-windows-x86_64.exe' $headsetExe 'E371DA043710DDB8FC70AF77F2CCAD1CD03F8C6F49BA423E08764AD7A94A3C40'
$headsetSource = Join-Path $output 'headsetcontrol-4.3.0.tar.gz'
Get-VerifiedFile 'https://github.com/Sapd/HeadsetControl/releases/download/4.3.0/headsetcontrol-4.3.0.tar.gz' $headsetSource '918DCACF8AEF347744B36B2D259D76880FA64B3869F0F8EF26B6576B873D28A7'

$backendExe = Join-Path $output 'PulseBattery.DeviceBackend.exe'
# Deliberately exclude upstream GUI, reporting/upload code and fallback drivers.
$excluded = @('phonelink.cpp','generic.cpp','maono.cpp')
$drivers = @(Get-ChildItem -LiteralPath (Join-Path $native 'upstream/src/drivers') -Filter '*.cpp' -File | Where-Object Name -NotIn $excluded | Sort-Object Name)
$sources = @((Join-Path $native 'bridge.cpp'), (Join-Path $native 'upstream/src/devices.cpp'), (Join-Path $native 'upstream/src/hid.cpp')) + $drivers.FullName
$arguments = @('-std=c++17','-O2','-static','-s','-ffunction-sections','-fdata-sections','-Wl,--gc-sections',
    '-D_WIN32_WINNT=0x0A00','-DWINVER=0x0A00','-DUNICODE','-D_UNICODE','-DNOMINMAX','-Wall','-Wno-missing-field-initializers','-Wno-c++20-extensions') +
    $sources + @('-o', $backendExe, '-lhid','-lsetupapi','-lcfgmgr32','-lole32','-loleaut32','-lruntimeobject','-lws2_32','-ladvapi32','-lbthprops')
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Native device backend compilation failed.' }
& $backendExe --version
if ($LASTEXITCODE -ne 0) { throw 'Native device backend startup check failed.' }
& $verifyScript -Compiler $compiler

@'
Pulse Device Backend -- third-party notices

Wireless Device Battery Monitor by krisgfx9 and contributors
https://github.com/krisgfx9/Wireless-Device-Battery-Monitor
Commit: 30419ed4e29ffd1b1337eccf367f2381faebd592
License: GNU GPL v3 (see LICENSE.txt and corresponding source).
Pulse's bridge and modifications are supplied under GPL-3.0-or-later.
Protocol-specific attributions remain in the corresponding source files and upstream README.

HeadsetControl 4.3.0 by Sapd and contributors
https://github.com/Sapd/HeadsetControl
License: GNU GPL v3. Exact corresponding source: headsetcontrol-4.3.0.tar.gz inside
PulseBattery-DeviceBackend-Sources.zip. Its source archive contains dependency notices.

The separately executed backend and HeadsetControl communicate with Pulse by local JSON.
No upstream dashboard, tray, phone polling, reporting UI or upload code is included.
Complete corresponding source and build instructions are bundled in
PulseBattery-DeviceBackend-Sources.zip alongside these executables, also in GitHub Releases.

Built using LLVM-MinGW 20261117 (UCRT x86_64):
https://github.com/mstorsjo/llvm-mingw/releases/tag/20261117
LLVM, mingw-w64 and compiler runtime licenses: see TOOLCHAIN-LICENSE.txt.
'@ | Set-Content -LiteralPath (Join-Path $output 'THIRD_PARTY_NOTICES.txt') -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $native 'LICENSE.txt') -Destination (Join-Path $output 'LICENSE.txt') -Force
$toolLicense = Join-Path $workspace ".local/$toolName/LICENSE.TXT"
if (-not (Test-Path -LiteralPath $toolLicense)) { $toolLicense = Join-Path $workspace ".local/$toolName/LICENSE.txt" }
Copy-Item -LiteralPath $toolLicense -Destination (Join-Path $output 'TOOLCHAIN-LICENSE.txt') -Force

# Ship complete corresponding source, not just a link or a promise of future access.
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$sourceZip = Join-Path $output 'PulseBattery-DeviceBackend-Sources.zip'
if (Test-Path -LiteralPath $sourceZip) { Remove-Item -LiteralPath $sourceZip }
$archive = [IO.Compression.ZipFile]::Open($sourceZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $sourceFiles) {
        $relative = $file.FullName.Substring($workspace.Length + 1).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $PSCommandPath, 'scripts/Build-DeviceBackend.ps1') | Out-Null
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $verifyScript, 'scripts/Verify-DeviceBackend.ps1') | Out-Null
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $headsetSource, 'headsetcontrol-4.3.0.tar.gz') | Out-Null
} finally { $archive.Dispose() }
Compress-Archive -LiteralPath @($backendExe, $headsetExe, $sourceZip, (Join-Path $output 'LICENSE.txt'),
    (Join-Path $output 'THIRD_PARTY_NOTICES.txt'), (Join-Path $output 'TOOLCHAIN-LICENSE.txt')) -DestinationPath $package -Force
$fingerprint | Set-Content -LiteralPath $marker -Encoding ASCII
Write-Host 'Battery protocol backend, licenses and corresponding source packaged.'
