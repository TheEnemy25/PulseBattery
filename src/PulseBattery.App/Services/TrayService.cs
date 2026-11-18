using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
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
    internal bool IsVisible => _main.Visible;

    public TrayService(DashboardViewModel viewModel, Action open)
    {
        _viewModel = viewModel; _open = open;
        _main = new() { Text = Strings.Current["WindowTitle"], Icon = CreateIcon(), Visible = true };
        Strings.Current.LanguageChanged += OnLanguageChanged;
        _main.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) open();
            else if (e.Button == Forms.MouseButtons.Right)
                (_fallbackMenu ??= new(_viewModel, _open)).OpenApplicationMenuAtPointer();
        };
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
                    _main.ShowBalloonTip(7000, Strings.Current["BatteryAlertTitle"], Strings.Current.Format(reading.BatteryPercent.HasValue ? "BatteryAlertBody" : "BatteryLevelAlertBody", device.Name, device.Level), Forms.ToolTipIcon.Warning);
                }
            }
            else if (reading.State == ReadingState.Available) _alerted.Remove(reading.Id);
        }
        var summary = "Pulse · " + string.Join(" · ", _viewModel.Devices.Select(d => $"{d.Name}: {d.Level}{d.Percent}"));
        _main.Text = summary[..Math.Min(summary.Length, 127)];
    }

    private static Drawing.Icon CreateIcon()
    {
        var light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int value && value != 0;
        using var bitmap = new Drawing.Bitmap(32, 32, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.ScaleTransform(32 / 24f, 32 / 24f);
            var color = SystemParameters.HighContrast ? Drawing.SystemColors.WindowText : light ? Drawing.Color.Black : Drawing.Color.White;
            using var pen = new Drawing.Pen(color, 1.65f)
            {
                StartCap = Drawing.Drawing2D.LineCap.Round, EndCap = Drawing.Drawing2D.LineCap.Round,
                LineJoin = Drawing.Drawing2D.LineJoin.Round
            };
            // Same path as Assets/pulse-mark.svg, without loading WPF's bitmap/PNG pipeline.
            graphics.DrawLines(pen, [new(3, 12), new(7, 12), new(10, 4), new(14, 20), new(17, 12), new(21, 12)]);
        }
        var handle = bitmap.GetHicon();
        try { using var icon = Drawing.Icon.FromHandle(handle); return (Drawing.Icon)icon.Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
    public void Dispose()
    {
        Strings.Current.LanguageChanged -= OnLanguageChanged;
        _fallbackMenu?.Dispose();
        _main.Visible = false; _main.Icon?.Dispose(); _main.Dispose();
    }
    private void OnLanguageChanged()
    {
        Update();
    }
}
