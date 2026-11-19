# Pulse device backend

This separately executed GPL-3.0 module uses the device drivers of
[Wireless Device Battery Monitor](https://github.com/krisgfx9/Wireless-Device-Battery-Monitor)
by krisgfx9 and its contributors, pinned to commit
`30419ed4e29ffd1b1337eccf367f2381faebd592` (2026-11-19 checkout).
The upstream source files retain their original copyright and license notices.
See `LICENSE.txt` and `upstream/README.md` for upstream attribution and protocol sources.

Pulse communicates with this executable over stdin/stdout. Send `scan\n`; receive one
UTF-8 JSON line with schema 1 and battery device readings. The process stays alive to
preserve driver caches, request rate limits and Bluetooth listeners. EOF terminates it;
`--parent <pid>` also terminates it when its caller exits. `--version` never probes devices.
It has no tray, dashboard, upload/reporting UI or telemetry.

Changes from upstream: added the console JSON bridge and product/path metadata;
disabled Phone Link and wireless microphone polling; disabled the upstream generic
Windows/Bluetooth/HID fallbacks (Pulse already supplies those). XInput and Windows.Gaming.Input
are queried by the backend; Pulse preserves coarse levels as text without invented percentages. No vendor
protocol commands were added or changed. HeadsetControl 4.3.0 is bundled, battery queries only.
Unknown XInput battery types are excluded rather than treated as evidence of a battery.

Build from the Pulse repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Build-DeviceBackend.ps1
```

The script downloads a pinned LLVM-MinGW toolchain and HeadsetControl with SHA256
verification. Compiler version, download URL and hash are in the script. The generated
package includes this entire source directory, that build script, the GPL license,
notices and the exact HeadsetControl corresponding source archive. These remain
available alongside the extracted helper in `%LOCALAPPDATA%/PulseBattery/DeviceBackend`.
