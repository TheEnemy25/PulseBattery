using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PulseBattery.App.ViewModels;

namespace PulseBattery.App.Views;

public partial class Dashboard
{
    private const string DeviceDragFormat = "PulseBattery.DeviceId";
    private Border? _dragOrigin;
    private Point _dragStart;
    private bool _isDragging;
    private Border? _dropTarget;
    private Brush? _dropTargetBrush;

    private void CardPointerDown(object sender, MouseButtonEventArgs e)
    {
        for (var source = e.OriginalSource as DependencyObject; source is not null && !ReferenceEquals(source, sender); source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source))
            if (source is ButtonBase) { _dragOrigin = null; return; }
        _dragOrigin = (Border)sender;
        _dragStart = e.GetPosition(this);
    }

    private void CardPointerMove(object sender, MouseEventArgs e)
    {
        if (_isDragging) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _dragOrigin = null; return; }
        if (_dragOrigin is not { DataContext: DeviceViewModel device } origin) return;
        var distance = e.GetPosition(this) - _dragStart;
        if (Math.Abs(distance.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(distance.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _isDragging = true;
        var opacity = origin.Opacity;
        try
        {
            origin.SetCurrentValue(OpacityProperty, 0.6);
            DragDrop.DoDragDrop(origin, new DataObject(DeviceDragFormat, device.Device.Id), DragDropEffects.Move);
        }
        finally
        {
            origin.SetCurrentValue(OpacityProperty, opacity);
            SetDropTarget(null); _dragOrigin = null; _isDragging = false;
        }
    }

    private void CardDragOver(object sender, DragEventArgs e)
    {
        var target = (Border)sender;
        var sourceId = e.Data.GetData(DeviceDragFormat, false) as string;
        var accepted = target.DataContext is DeviceViewModel device && device.Device.Id != sourceId &&
            _viewModel.Devices.Any(item => item.Device.Id == sourceId) && (e.AllowedEffects & DragDropEffects.Move) != 0;
        e.Effects = accepted ? DragDropEffects.Move : DragDropEffects.None;
        SetDropTarget(accepted ? target : null);
        var pointer = e.GetPosition(DeviceScroll);
        if (pointer.Y < 28) DeviceScroll.LineUp();
        else if (pointer.Y > DeviceScroll.ActualHeight - 28) DeviceScroll.LineDown();
        e.Handled = true;
    }

    private void CardDragLeave(object sender, DragEventArgs e)
    {
        var card = (Border)sender;
        if (!new Rect(card.RenderSize).Contains(e.GetPosition(card)) && ReferenceEquals(_dropTarget, card)) SetDropTarget(null);
    }

    private void CardDrop(object sender, DragEventArgs e)
    {
        SetDropTarget(null);
        var sourceId = e.Data.GetData(DeviceDragFormat, false) as string;
        e.Effects = (e.AllowedEffects & DragDropEffects.Move) != 0 && sourceId is not null && ((Border)sender).DataContext is DeviceViewModel target &&
            _viewModel.MoveDevice(sourceId, target.Device.Id) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void SetDropTarget(Border? target)
    {
        if (ReferenceEquals(target, _dropTarget)) return;
        if (_dropTarget is not null) _dropTarget.SetCurrentValue(Border.BorderBrushProperty, _dropTargetBrush);
        _dropTarget = target;
        if (target is null) return;
        _dropTargetBrush = target.BorderBrush;
        target.SetCurrentValue(Border.BorderBrushProperty, FindResource("AccentBrush"));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_isDragging) _dragOrigin = null;
    }
}
