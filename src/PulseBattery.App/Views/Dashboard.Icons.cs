using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PulseBattery.App.Services;
using PulseBattery.App.ViewModels;
using PulseBattery.Core;
using PulseBattery.App.Controls;

namespace PulseBattery.App.Views;

public partial class Dashboard
{
    private void OpenDeviceIconMenu(object sender, RoutedEventArgs e)
    {
        if (PrepareDeviceIconMenu((Button)sender) is { } menu) menu.IsOpen = true;
        e.Handled = true;
    }

    private void DeviceIconContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (PrepareDeviceIconMenu((Button)sender) is null) e.Handled = true;
    }

    private ContextMenu? PrepareDeviceIconMenu(Button button)
    {
        _dragOrigin = null;
        if (button.DataContext is not DeviceViewModel device || !_viewModel.Devices.Any(item => item.Device.Id == device.Device.Id)) return null;
        var deviceId = device.Device.Id;
        var menu = button.ContextMenu!;
        menu.Items.Clear();
        var header = new DeviceMenuHeader(); header.SetDevice(device);
        menu.Items.Add(header); menu.Items.Add(new Separator());
        menu.PlacementTarget = button; menu.Placement = PlacementMode.Bottom; menu.VerticalOffset = 5;
        var choices = new DeviceKindMenu(menu, kind => _viewModel.SetDeviceKind(deviceId, kind));
        choices.SelectCurrent(_viewModel.Settings.DeviceKinds.TryGetValue(deviceId, out var configured) ? configured : null);
        return menu;
    }
}
