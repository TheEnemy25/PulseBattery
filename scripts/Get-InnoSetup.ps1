param()
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$compilerFolder = Join-Path $workspace '.local/inno-setup'
$compiler = Join-Path $compilerFolder 'ISCC.exe'
if (Test-Path -LiteralPath $compiler) { Write-Output $compiler; return }

# Pinned official distribution, also used for local release builds.
$version = '7.1.1'
$expectedHash = '8A5A6711C3766A6E5FB74F280BA0E3551E719CE94538D901FDBD1F8F9920B3F0'
$downloadFolder = Join-Path $workspace '.local/downloads'
New-Item -ItemType Directory -Path $downloadFolder -Force | Out-Null
$download = Join-Path $downloadFolder "innosetup-$version-x64.exe"
if (-not (Test-Path -LiteralPath $download)) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri "https://github.com/jrsoftware/issrc/releases/download/is-7_1_1/innosetup-$version-x64.exe" -OutFile $download -UseBasicParsing
}
if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $expectedHash) { throw 'The Inno Setup download does not match the pinned SHA256.' }
$process = Start-Process -FilePath $download -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/PORTABLE=1', ('/DIR="' + $compilerFolder + '"')) -WindowStyle Hidden -PassThru
try {
    if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Inno Setup extraction timed out.' }
    $process.Refresh()
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $compiler)) { throw 'Inno Setup extraction failed.' }
}
finally { $process.Dispose() }
Write-Output $compiler
