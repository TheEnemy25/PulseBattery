using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PulseBattery.App.Controls;
using PulseBattery.App.Views;
using PulseBattery.App.ViewModels;
using PulseBattery.Core;
using PulseBattery.App.Localization;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace PulseBattery.App.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _main;
    private readonly HashSet<string> _alerted = [];
    private readonly DashboardViewModel _viewModel;
    private TaskbarPanel? _fallbackMenu;
    private readonly Action _open;
    private readonly DispatcherTimer _alertTimer;
    private bool _taskbarAvailable;
    private bool _showingAlert;

    public TrayService(DashboardViewModel viewModel, Action open)
    {
        _viewModel = viewModel; _open = open;
        _main = new() { Text = Strings.Current["WindowTitle"], Visible = false };
        _alertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _alertTimer.Tick += (_, _) => { _alertTimer.Stop(); _showingAlert = false; UpdateVisibility(); };
        Strings.Current.LanguageChanged += OnLanguageChanged;
        _main.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) open();
            else if (e.Button == Forms.MouseButtons.Right)
                (_fallbackMenu ??= new(_viewModel, _open)).OpenApplicationMenuAtPointer();
        };
    }

    public void SetTaskbarAvailable(bool available) { _taskbarAvailable = available; UpdateVisibility(); }
    private void UpdateVisibility()
    {
        var visible = !_taskbarAvailable || _showingAlert;
        if (visible && _main.Icon is null) _main.Icon = CreateIcon();
        _main.Visible = visible;
    }

    public void Update()
    {
        foreach (var device in _viewModel.Devices)
        {
            var reading = device.Device;
            if (reading.IsLow(_viewModel.LowBatteryThreshold))
            {
                if (_viewModel.Notifications && _alerted.Add(reading.Id))
                {
                    // Shell balloon notifications need a registered icon; keep it only for the alert.
                    _showingAlert = true; UpdateVisibility();
                    _main.ShowBalloonTip(7000, Strings.Current["BatteryAlertTitle"], Strings.Current.Format("BatteryAlertBody", device.Name, device.Level), Forms.ToolTipIcon.Warning);
                    _alertTimer.Stop(); _alertTimer.Start();
                }
            }
            else if (reading.State == ReadingState.Available) _alerted.Remove(reading.Id);
        }
        var summary = "Pulse · " + string.Join(" · ", _viewModel.Devices.Select(d => $"{d.Name}: {d.Level}{d.Percent}"));
        _main.Text = summary[..Math.Min(summary.Length, 127)];
        UpdateVisibility();
    }

    private static Drawing.Icon CreateIcon()
    {
        var light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int value && value != 0;
        var glyph = new SvgIcon { Asset = "pulse-mark", Width = 64, Height = 64, Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : light ? Brushes.Black : Brushes.White };
        glyph.Measure(new Size(64, 64)); glyph.Arrange(new Rect(0, 0, 64, 64));
        var rendered = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32); rendered.Render(glyph);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(rendered));
        using var stream = new MemoryStream(); encoder.Save(stream); stream.Position = 0;
        using var bitmap = new Drawing.Bitmap(stream);
        var handle = bitmap.GetHicon();
        try { using var icon = Drawing.Icon.FromHandle(handle); return (Drawing.Icon)icon.Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
    public void Dispose()
    {
        Strings.Current.LanguageChanged -= OnLanguageChanged;
        _alertTimer.Stop(); _fallbackMenu?.Dispose();
        _main.Visible = false; _main.Icon?.Dispose(); _main.Dispose();
    }
    private void OnLanguageChanged()
    {
        Update();
    }
}
