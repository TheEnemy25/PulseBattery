using System.Windows;

namespace PulseBattery.App.Services;

internal static class MenuResources
{
    public static void EnsureLoaded()
    {
        var resources = Application.Current.Resources;
        if (resources.Contains("PulseContextMenu")) return;
        resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/PulseBattery;component/Themes/Menus.xaml", UriKind.Relative) });
    }
}
