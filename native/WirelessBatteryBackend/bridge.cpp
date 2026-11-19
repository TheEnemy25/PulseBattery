// SPDX-License-Identifier: GPL-3.0-or-later
// Pulse's console-only adapter for the Wireless Battery device drivers.
// No upstream window, tray, settings, reporting UI or network upload code is linked.
#include "upstream/src/hid.h"
#include "upstream/src/report.h"
#include <iostream>
#include <sstream>
#include <thread>
#include <cstdio>

// Diagnostic UI is not part of this executable. Required only by the unused dump function.
namespace report { std::wstring DumpExtras() { return L""; } }

static std::string Quote(const std::wstring& value) {
    std::string out = "\"";
    for (unsigned char c : WideToUtf8(value)) {
        if (c == '"' || c == '\\') { out += '\\'; out += c; }
        else if (c < 32) {
            char escaped[7]; snprintf(escaped, sizeof(escaped), "\\u%04x", c); out += escaped;
        } else out += c;
    }
    return out + '"';
}

static const char* Kind(DeviceKind kind) {
    switch (kind) {
        case DeviceKind::Mouse: return "Mouse";
        case DeviceKind::Keyboard: return "Keyboard";
        case DeviceKind::Headset: return "Headset";
        case DeviceKind::Gamepad: return "Gamepad";
        default: return "Other";
    }
}

static std::string Serialize(const std::vector<DeviceStatus>& devices) {
    std::ostringstream out;
    out << "{\"schema\":1,\"devices\":[";
    bool comma = false;
    for (const auto& s : devices) {
        if (s.kind == DeviceKind::Phone) continue;
        if (comma) out << ','; comma = true;
        out << "{\"key\":" << Quote(s.key) << ",\"name\":" << Quote(s.name)
            << ",\"kind\":\"" << Kind(s.kind) << "\",\"percent\":" << s.percent
            << ",\"charging\":" << (s.charging ? "true" : "false")
            << ",\"connected\":" << (s.connected ? "true" : "false")
            << ",\"asleep\":" << (s.asleep ? "true" : "false")
            << ",\"graceSeconds\":" << s.graceSec
            << ",\"detail\":" << Quote(s.detail) << ",\"via\":" << Quote(s.via)
            << ",\"level\":" << Quote(s.levelText) << ",\"vid\":" << s.vid
            << ",\"pid\":" << s.pid << ",\"path\":" << Quote(s.hidPath) << '}';
    }
    return out.str() + "]}";
}

int main(int argc, char** argv) {
    if (argc == 2 && std::string(argv[1]) == "--version") {
        std::cout << "Pulse device backend / Wireless Battery 30419ed4e29ffd1b1337eccf367f2381faebd592 / schema 1\n";
        return 0;
    }
    if (argc == 3 && std::string(argv[1]) == "--parent") {
        HANDLE parent = OpenProcess(SYNCHRONIZE, FALSE, strtoul(argv[2], nullptr, 10));
        if (!parent) return 2;
        std::thread([parent] { WaitForSingleObject(parent, INFINITE); CloseHandle(parent); ExitProcess(0); }).detach();
    } else if (argc != 1) return 2;
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    std::string command;
    while (std::getline(std::cin, command)) {
        if (command != "scan") return 2;
        try { std::cout << Serialize(PollAllDevices()) << std::endl; }
        catch (...) { std::cout << "{\"schema\":1,\"error\":\"Device driver scan failed\",\"devices\":[]}" << std::endl; }
    }
    CoUninitialize();
    return 0;
}
