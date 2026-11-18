param([string]$Executable)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable = Join-Path $workspace 'artifacts/portable/win-x64/PulseBattery.exe' }
$Executable = (Resolve-Path -LiteralPath $Executable).Path
if ([IO.Path]::GetFileName($Executable) -ne 'PulseBattery.exe') { throw 'Expected PulseBattery.exe from a self-contained single-file publish.' }
$packageFolder = Join-Path $workspace 'artifacts/portable-package'
New-Item -ItemType Directory -Path $packageFolder -Force | Out-Null
$readme = Join-Path $packageFolder 'README.txt'
@'
Pulse Battery - portable for Windows x64

Extract the entire ZIP to a folder, then open PulseBattery.exe.
The .NET runtime is included. No installer is required.

This is an unsigned application. ZIP packaging does not remove Smart App Control
or other Application Control restrictions. A blocked executable can remain blocked
after extraction; this archive does not resolve that restriction.

Windows startup is optional and can be changed in the app settings.
To stop monitoring, choose Exit in the system tray menu before removing the folder.
If startup was enabled, disable it in the app settings before removing the EXE.
Portable mode does not add a Control Panel uninstall entry.
Personal settings are stored in %LOCALAPPDATA%\PulseBattery.
'@ | Set-Content -LiteralPath $readme -Encoding UTF8
$archive = Join-Path $workspace 'PulseBattery-Portable-win-x64.zip'
Compress-Archive -LiteralPath @($Executable, $readme) -DestinationPath $archive -CompressionLevel Optimal -Force

# Verify the archive contains the published executable without changing its bytes.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $names = @($zip.Entries | ForEach-Object { $_.FullName })
    if ($names.Count -ne 2 -or 'PulseBattery.exe' -notin $names -or 'README.txt' -notin $names) { throw 'Unexpected portable ZIP contents.' }
    $entry = $zip.GetEntry('PulseBattery.exe')
    $stream = $entry.Open()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $archivedHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
    if ($archivedHash -ne (Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash) { throw 'The archived executable differs from the published executable.' }
}
finally { $zip.Dispose() }
Write-Host ('Portable ZIP ready (contents and SHA256 verified): ' + $archive)
