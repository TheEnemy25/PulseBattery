using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PulseBattery.App.ViewModels;
using PulseBattery.Core;

namespace PulseBattery.App.Controls;

internal sealed class DeviceMenuHeader : MenuItem
{
    public DeviceMenuHeader()
    {
        SetResourceReference(StyleProperty, "DeviceMenuHeader");
        IsEnabled = false; IsHitTestVisible = false; Focusable = false;
        Icon = new DeviceGlyph { Kind = DeviceKind.Other };
        Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(this, "DeviceMenuHeader");
    }
    public void SetDevice(DeviceViewModel? device)
    {
        Header = device?.Name ?? "";
        Icon = new DeviceGlyph { Kind = device?.Kind ?? DeviceKind.Other };
        Visibility = device is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(this, device?.Name ?? "");
    }
    public void SetApplication()
    {
        Header = "Pulse"; Icon = new SvgIcon { Asset = "pulse-mark" };
        Visibility = Visibility.Visible;
        AutomationProperties.SetName(this, "Pulse");
    }
}
