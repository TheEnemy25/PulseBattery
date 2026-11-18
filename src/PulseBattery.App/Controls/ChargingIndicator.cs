using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace PulseBattery.App.Controls;

public sealed class ChargingIndicator : FrameworkElement
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private int _frame;
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(ChargingIndicator), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public ChargingIndicator()
    {
        _timer.Tick += (_, _) => { _frame = (_frame + 1) % 12; InvalidateVisual(); };
        Loaded += (_, _) => UpdateTimer();
        IsVisibleChanged += (_, _) => UpdateTimer();
        Unloaded += (_, _) => _timer.Stop();
    }
    private void UpdateTimer() { if (IsLoaded && IsVisible) _timer.Start(); else _timer.Stop(); }
    protected override void OnRender(DrawingContext drawingContext)
    {
        var pen = new Pen(Foreground, 1);
        var y = Math.Max(0, (ActualHeight - 7) / 2);
        drawingContext.DrawRoundedRectangle(null, pen, new Rect(0.5, y, 10, 7), 1.2, 1.2);
        drawingContext.DrawRectangle(Foreground, null, new Rect(11, y + 2, 1.4, 3));
        var progress = (_frame + 1) / 12d;
        drawingContext.DrawRectangle(Foreground, null, new Rect(2, y + 1.5, 7 * progress, 4));
    }
}
