using System.Windows;
using PulseBattery.Core;

namespace PulseBattery.App.Controls;

public sealed class DeviceGlyph : SvgIcon
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(DeviceKind), typeof(DeviceGlyph), new FrameworkPropertyMetadata(DeviceKind.Headset));
    public DeviceKind Kind { get => (DeviceKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public DeviceGlyph() => UpdateAsset();
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == KindProperty) UpdateAsset();
    }
    private void UpdateAsset() => Asset = Kind switch { DeviceKind.Mouse => "mouse-alt-2-svgrepo-com", DeviceKind.Keyboard => "keyboard-alt-1-svgrepo-com", DeviceKind.Headphones => "headset-svgrepo-com", DeviceKind.Earbuds => "earbuds", DeviceKind.Headset => "headset-mic-1-svgrepo-com", DeviceKind.Gamepad => "gamepad", _ => "generic-device" };
}
