using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace PulseBattery.App.Services;

internal sealed class TaskbarMenuPlacement(FrameworkElement anchor)
{
    private Func<double>? _horizontalAnchor;
    public void Configure(ContextMenu menu, Func<double> horizontalAnchor)
    {
        _horizontalAnchor = horizontalAnchor;
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = (size, target, offset) => [new(new Point(horizontalAnchor() - size.Width / 2, -size.Height - 7), PopupPrimaryAxis.Horizontal)];
        menu.Opened += (_, _) => Clamp(menu);
    }
    public void Clamp(FrameworkElement content)
    {
        if (PresentationSource.FromVisual(anchor) is null) return;
        if (PresentationSource.FromVisual(content) is not HwndSource popup || popup.Handle == 0) return;
        var screenAnchor = anchor.PointToScreen(new Point());
        var monitor = MonitorFromWindow(popup.Handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info) || !GetWindowRect(popup.Handle, out var bounds)) return;
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        var bottom = Math.Min(info.Work.Bottom, (int)Math.Floor(screenAnchor.Y) - 6);
        // ContextMenuService can place a mouse-opened menu at the pointer even with
        // custom placement. Keep its actual popup centered over the selected anchor.
        var desiredLeft = content is ContextMenu && _horizontalAnchor is not null
            ? (int)Math.Round(anchor.PointToScreen(new Point(_horizontalAnchor(), 0)).X - width / 2d) : bounds.Left;
        var desiredTop = content is ContextMenu ? bottom - height : bounds.Top;
        var left = Math.Clamp(desiredLeft, info.Work.Left + 4, Math.Max(info.Work.Left + 4, info.Work.Right - width - 4));
        var top = Math.Clamp(desiredTop, info.Work.Top + 4, Math.Max(info.Work.Top + 4, bottom - height));
        SetWindowPos(popup.Handle, -1, left, top, width, height, 0x0010); // Only the open menu is topmost.
    }
    [StructLayout(LayoutKind.Sequential)] internal struct RectNative { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public RectNative Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint hwnd, out RectNative rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
