using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using PulseBattery.App.Controls;
using PulseBattery.App.Localization;
using PulseBattery.Core;

namespace PulseBattery.App.Services;

internal sealed class DeviceKindMenu
{
    private readonly Dictionary<MenuItem, DeviceKind?> _choices = [];

    public DeviceKindMenu(ItemsControl host, Action<DeviceKind?> select)
    {
        foreach (var (key, kind) in new (string Key, DeviceKind? Kind)[]
        {
            ("Automatic", null), ("KindHeadset", DeviceKind.Headset), ("KindEarbuds", DeviceKind.Earbuds),
            ("KindHeadphones", DeviceKind.Headphones), ("KindMouse", DeviceKind.Mouse),
            ("KindKeyboard", DeviceKind.Keyboard), ("KindGamepad", DeviceKind.Gamepad)
        })
        {
            var item = new ImmediateMenuItem { IsCheckable = true, Tag = kind };
            item.SetBinding(HeaderedItemsControl.HeaderProperty, new Binding($"[{key}]") { Source = Strings.Current });
            item.Icon = kind.HasValue ? new DeviceGlyph { Kind = kind.Value } : new SvgIcon { Asset = "menu-automatic" };
            AutomationProperties.SetAutomationId(item, "DeviceKind." + key);
            item.Click += (_, _) => select(kind);
            host.Items.Add(item); _choices[item] = kind;
        }
    }

    public void SelectCurrent(DeviceKind? kind)
    {
        foreach (var (item, value) in _choices) item.IsChecked = value == kind;
    }
}
