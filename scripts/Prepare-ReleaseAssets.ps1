param([string]$ExpectedVersion)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
[xml]$project = Get-Content -Raw -LiteralPath (Join-Path $workspace 'src/PulseBattery.App/PulseBattery.App.csproj')
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'The project must specify a stable major.minor.patch version.' }
if ($ExpectedVersion -and $ExpectedVersion.TrimStart('v') -ne $version) { throw "Release version $ExpectedVersion does not match application version $version." }
$executable = Join-Path $workspace 'artifacts/portable/win-x64/PulseBattery.exe'
$installer = Join-Path $workspace 'PulseBattery-Setup.exe'
$portableZip = Join-Path $workspace 'PulseBattery-Portable-win-x64.zip'
foreach ($path in @($executable, $installer)) {
    $builtVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductVersion.Split('+')[0].Trim()
    if ($builtVersion -ne $version) { throw "The built file $path has version $builtVersion, expected $version. Rebuild the release first." }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($portableZip)
try {
    $entry = $archive.GetEntry('PulseBattery.exe')
    if (-not $entry) { throw 'The portable ZIP does not contain PulseBattery.exe.' }
    $stream = $entry.Open()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $zipHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
    if ($zipHash -ne (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash) { throw 'The portable ZIP contains a different executable. Rebuild the release first.' }
}
finally { $archive.Dispose() }

$outputFolder = Join-Path $workspace 'artifacts/release'
New-Item -ItemType Directory -Path $outputFolder -Force | Out-Null
$backendSource = Join-Path $workspace 'artifacts/device-backend/PulseBattery-DeviceBackend-Sources.zip'
if (-not (Test-Path -LiteralPath $backendSource)) { throw 'The corresponding source archive for the device backend is missing.' }
$files = @($executable, $installer, $portableZip, $backendSource)
foreach ($path in $files) { Copy-Item -LiteralPath $path -Destination (Join-Path $outputFolder ([IO.Path]::GetFileName($path))) -Force }
$hashes = foreach ($path in $files) {
    $name = [IO.Path]::GetFileName($path)
    $hash = (Get-FileHash -LiteralPath (Join-Path $outputFolder $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name"
}
$hashes | Set-Content -LiteralPath (Join-Path $outputFolder 'SHA256SUMS.txt') -Encoding ASCII
Write-Host ("Release v{0} ready: {1}" -f $version, $outputFolder)
