using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml.Linq;

namespace PulseBattery.App.Controls;

public class SvgIcon : FrameworkElement
{
    public const double MenuSize = 18;
    private static readonly Dictionary<string, Geometry> Geometries = [];
    public static readonly DependencyProperty AssetProperty = DependencyProperty.Register(nameof(Asset), typeof(string), typeof(SvgIcon), new FrameworkPropertyMetadata("generic-device", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(SvgIcon), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public string Asset { get => (string)GetValue(AssetProperty); set => SetValue(AssetProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public SvgIcon() { Width = MenuSize; Height = MenuSize; }
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!Geometries.TryGetValue(Asset, out var geometry))
        {
            using var stream = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{Asset}.svg"))!.Stream;
            var xml = XDocument.Load(stream);
            var group = new GeometryGroup();
            foreach (var path in xml.Descendants().Where(element => element.Name.LocalName == "path"))
                group.Children.Add(Geometry.Parse(path.Attribute("d")!.Value));
            group.Freeze(); Geometries[Asset] = geometry = group;
        }
        var scale = Math.Min(ActualWidth, ActualHeight) / 24;
        drawingContext.PushTransform(new TranslateTransform((ActualWidth - scale * 24) / 2, (ActualHeight - scale * 24) / 2));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        drawingContext.DrawGeometry(null, new Pen(Foreground, 1.65) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geometry);
        drawingContext.Pop(); drawingContext.Pop();
    }
}
