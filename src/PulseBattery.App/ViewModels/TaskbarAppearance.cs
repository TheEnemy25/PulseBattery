using System.Windows;
using PulseBattery.Core;

namespace PulseBattery.App.ViewModels;

public sealed record TaskbarAppearance(double IconSize, double FontSize, double SidePadding, double AnchorWidth, double DividerHeight)
{
    private static readonly TaskbarAppearance Compact = new(16, 11, 5, 28, 18);
    private static readonly TaskbarAppearance Standard = new(19, 13, 7, 33, 21);
    private static readonly TaskbarAppearance Large = new(22, 15, 8, 38, 24);
    public double PercentFontSize => FontSize - 1;
    public double ChargingSize => IconSize - 5;
    public Thickness DevicePadding => new(SidePadding, 0, SidePadding, 0);
    public static TaskbarAppearance For(TaskbarIndicatorSize size) => size switch
    {
        TaskbarIndicatorSize.Standard => Standard,
        TaskbarIndicatorSize.Large => Large,
        _ => Compact
    };
}
