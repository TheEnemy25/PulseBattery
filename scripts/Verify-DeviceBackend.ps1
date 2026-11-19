param([string]$Compiler)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
if (-not $Compiler) { $Compiler = Join-Path $workspace '.local/llvm-mingw-20261117-ucrt-x86_64/bin/clang++.exe' }
$upstream = Join-Path $workspace 'native/WirelessBatteryBackend/upstream'
$output = Join-Path $workspace 'artifacts/device-backend-checks'
New-Item -ItemType Directory -Path $output -Force | Out-Null
# These are upstream's protocol/driver tests with simulated HID transports, not hardware writes.
$cases = @(
    @('astro_driver_test','astro'), @('steelseries_mouse_test','steelseries'), @('steelseries_driver_test','steelseries'),
    @('rapoo_protocol_test',''), @('rapoo_driver_test','rapoo'), @('ninjutso_driver_test','ninjutso'),
    @('ninjutso_cable_test','ninjutso'), @('cherry_driver_test','cherry'), @('machenike_driver_test','machenike'),
    @('niceboy_driver_test','niceboy'), @('alienware_driver_test','alienware'), @('mchose_modern_driver_test','mchose_modern'),
    @('vaxee_driver_test','vaxee'), @('finalmouse_driver_test','finalmouse'), @('keychron_driver_test','keychron'),
    @('hyperx_haste_driver_test','hyperx'), @('galaxy_buds_protocol_test',''), @('logitech_g435_0adf_test',''),
    @('xenta_protocol_test','xenta_protocol'), @('arbiter_driver_test','arbiter','lamzu'), @('dareu_driver_test','dareu'), @('semico_driver_test','semico')
)
$results = foreach ($case in $cases) {
    $name = $case[0]
    $exe = Join-Path $output "$name.exe"
    $arguments = @('-std=c++17','-O2','-static','-s','-D_WIN32_WINNT=0x0A00','-DNOMINMAX','-Wno-missing-field-initializers','-Wno-c++20-extensions',
        (Join-Path $upstream "tests/$name.cpp"))
    foreach ($driver in ($case | Select-Object -Skip 1)) {
        if ($driver) { $arguments += Join-Path $upstream ("src/drivers/{0}.cpp" -f $driver) }
    }
    $arguments += @('-o',$exe)
    & $Compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw "Device driver test compilation failed: $name" }
    & $exe
    if ($LASTEXITCODE -ne 0) { throw "Device driver test failed: $name" }
    [PSCustomObject]@{Test=$name;Passed=$true}
}
[PSCustomObject]@{Passed=$true;UpstreamCommit='30419ed4e29ffd1b1337eccf367f2381faebd592';Suites=$results} |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'device-driver-checks.json') -Encoding UTF8
Write-Host ("{0} native device driver suites passed." -f $cases.Count)
