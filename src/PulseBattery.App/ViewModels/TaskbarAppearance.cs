using System.Windows;

namespace PulseBattery.App.ViewModels;

public sealed record TaskbarAppearance(double IconSize, double FontSize, double Spacing, double DividerHeight)
{
    public double PercentFontSize => Math.Max(8, FontSize - 1);
    public double ChargingSize => IconSize - 5;
    public Thickness DevicePadding => new(Spacing / 2, 0, Spacing / 2, 0);
    public double StackedFontSize => Math.Max(11, FontSize);
    public double StackedPercentFontSize => Math.Max(10, StackedFontSize - 1);
    public double StackedIconSize => Math.Clamp(IconSize, 14, 20);
    public double StackedChargingSize => Math.Max(13, ChargingSize);
    public double StackedDividerHeight => StackedFontSize + StackedIconSize + 3;
    public static TaskbarAppearance For(int scalePercent, int spacing)
    {
        var scale = Math.Clamp(scalePercent, 80, 150) / 100d;
        return new(16 * scale, 11 * scale, Math.Clamp(spacing, 0, 24), 18 * scale);
    }
}
