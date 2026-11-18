using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PulseBattery.App.Controls;

public sealed class NumericSlider : Slider
{
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(NumericSlider), new PropertyMetadata(""));
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    private TextBox? _editor;
    private FrameworkElement? _editorHost;
    internal TextBox? ValueEditor => _editor;

    public override void OnApplyTemplate()
    {
        if (_editor is not null)
        {
            _editor.PreviewKeyDown -= EditorKeyDown;
            _editor.PreviewTextInput -= EditorTextInput;
            _editor.PreviewMouseLeftButtonDown -= EditorMouseDown;
            _editor.LostKeyboardFocus -= EditorLostFocus;
            DataObject.RemovePastingHandler(_editor, EditorPaste);
        }
        base.OnApplyTemplate();
        _editor = GetTemplateChild("PART_ValueEditor") as TextBox;
        _editorHost = GetTemplateChild("PART_EditorHost") as FrameworkElement;
        if (_editor is null) return;
        _editor.PreviewKeyDown += EditorKeyDown;
        _editor.PreviewTextInput += EditorTextInput;
        _editor.PreviewMouseLeftButtonDown += EditorMouseDown;
        _editor.LostKeyboardFocus += EditorLostFocus;
        DataObject.AddPastingHandler(_editor, EditorPaste);
        AutomationProperties.SetAutomationId(_editor, AutomationProperties.GetAutomationId(this) + ".Value");
        AutomationProperties.SetName(_editor, AutomationProperties.GetName(this));
        ResetEditor();
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        if (_editor?.IsKeyboardFocusWithin != true) ResetEditor();
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // Slider's built-in move-to-point behavior must not handle clicks on the editor.
        if (IsWithin(e.OriginalSource as DependencyObject, _editorHost))
        {
            if (!IsWithin(e.OriginalSource as DependencyObject, _editor) && _editor is not null)
            {
                _editor.Focus(); _editor.SelectAll(); e.Handled = true;
            }
            return;
        }
        if (_editor?.IsKeyboardFocusWithin == true) { CommitText(); Focus(); }
        base.OnPreviewMouseLeftButtonDown(e);
    }

    internal bool CommitText()
    {
        var valid = int.TryParse(_editor?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value);
        if (valid) SetCurrentValue(ValueProperty, Math.Clamp((double)value, Minimum, Maximum));
        ResetEditor();
        return valid;
    }

    private void ResetEditor()
    {
        if (_editor is not null) _editor.Text = Math.Round(Value).ToString(CultureInfo.InvariantCulture);
    }
    private void EditorLostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitText();
    private void EditorTextInput(object sender, TextCompositionEventArgs e) => e.Handled = e.Text.Any(c => c is < '0' or > '9');
    private void EditorMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_editor is not null && !_editor.IsKeyboardFocusWithin) { _editor.Focus(); _editor.SelectAll(); e.Handled = true; }
    }
    private void EditorPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) e.CancelCommand();
    }
    private void EditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitText(); Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { ResetEditor(); Focus(); e.Handled = true; }
        else if (e.Key is Key.Up or Key.Down)
        {
            CommitText();
            SetCurrentValue(ValueProperty, Math.Clamp(Value + (e.Key == Key.Up ? SmallChange : -SmallChange), Minimum, Maximum));
            ResetEditor(); e.Handled = true;
        }
    }
    private static bool IsWithin(DependencyObject? item, DependencyObject? ancestor)
    {
        if (ancestor is null) return false;
        while (item is not null)
        {
            if (ReferenceEquals(item, ancestor)) return true;
            item = item is Visual ? VisualTreeHelper.GetParent(item) : (item as FrameworkContentElement)?.Parent;
        }
        return false;
    }
}
