namespace PulseBattery.Core;

public static class DeviceClassification
{
    // General descriptions only: no vendor names or per-model catalogue.
    // A Bluetooth audio role cannot reliably distinguish an earbud from a full-size headset.
    private static readonly (DeviceKind Kind, string[] Words)[] NameHints =
    [
        (DeviceKind.Gamepad, ["gamepad", "joystick", "game controller", "wireless controller", "геймпад", "джойстик", "ігровий контролер"]),
        (DeviceKind.Earbuds, ["earbud", "earphone", "buds", "in-ear", "in ear", "tws", "вкладиш", "внутрішньоканаль"]),
        (DeviceKind.Keyboard, ["keyboard", "клавіат"]),
        (DeviceKind.Mouse, ["mouse", "миша", "мишка"]),
        (DeviceKind.Headset, ["headset", "гарнітур"]),
        (DeviceKind.Headphones, ["headphone", "навуш"])
    ];

    public static DeviceKind FromName(string name, uint bluetoothClass = 0)
    {
        var major = (bluetoothClass >> 8) & 0x1F;
        if (major == 2) return DeviceKind.Other; // A phone is not an audio accessory.
        if (major == 5)
        {
            if (((bluetoothClass >> 2) & 0x0F) is 1 or 2) return DeviceKind.Gamepad;
            if ((bluetoothClass & 0x80) != 0) return DeviceKind.Mouse;
            if ((bluetoothClass & 0x40) != 0) return DeviceKind.Keyboard;
        }
        var text = name.ToLowerInvariant();
        foreach (var (kind, words) in NameHints)
            if (words.Any(text.Contains)) return kind;
        if (major == 4) return ((bluetoothClass >> 2) & 0x3F) is 1 or 2 ? DeviceKind.Headset : DeviceKind.Headphones;
        return DeviceKind.Other;
    }

    public static DeviceKind FromHid(string name, ushort usagePage, ushort usage) => usagePage == 1
        ? usage switch { 2 => DeviceKind.Mouse, 4 or 5 => DeviceKind.Gamepad, 6 => DeviceKind.Keyboard, _ => FromName(name) }
        : FromName(name);
}
