using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PulseBattery.App.Controls;
using PulseBattery.App.ViewModels;
using PulseBattery.App.Views;
using PulseBattery.Core;
using PulseBattery.App.Localization;
using System.Windows.Controls;
using System.Windows.Automation;

namespace PulseBattery.App.Services;

internal static class PresentationChecks
{
    public static async Task<bool> RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var deviceOrderPassed = await DeviceOrderChecks.RunAsync(folder);
        var startupConsentPassed = await StartupConsentChecks.RunAsync(folder);
        var taskbarVisibilityPassed = await TaskbarVisibilityChecks.RunAsync(folder);
        // Explicit test fixtures, isolated from hardware reads and the user's saved settings.
        var fixtures = new FixtureProvider();
        var settings = new MemorySettings();
        var monitor = new BatteryMonitor([fixtures]);
        var viewModel = new DashboardViewModel(monitor, settings, default);
        await viewModel.RefreshAsync();
        var panel = new TaskbarPanel(viewModel, () => { });
        using var host = new HwndSource(new HwndSourceParameters("Pulse presentation checks")
        {
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080,
            PositionX = -32000, PositionY = -32000, Width = 420, Height = 48, UsesPerPixelOpacity = true
        });
        host.CompositionTarget.BackgroundColor = Colors.Transparent;
        host.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
        host.RootVisual = panel;
        panel.Width = 420; panel.Height = 48;
        panel.ApplySystemTheme(false);
        panel.Measure(new Size(420, 48)); panel.Arrange(new Rect(0, 0, 420, 48)); panel.UpdateLayout();
        await Task.Delay(240);
        var dark = Render(panel, 420, 48, Path.Combine(folder, "dark-frame-1.png"));
        await Task.Delay(480);
        var animated = Render(panel, 420, 48, Path.Combine(folder, "dark-frame-2.png"));
        var animationChanged = !dark.AsSpan().SequenceEqual(animated);
        var hitArea = new[] { (2, 2), (200, 2), (417, 45), (120, 45) }.All(p => dark[(p.Item2 * 420 + p.Item1) * 4 + 3] > 0 && panel.InputHitTest(new Point(p.Item1, p.Item2)) is not null);
        var darkForeground = panel.Foreground.ToString() == "#FFFFFFFF" && HasForeground(dark, false);
        panel.ApplySystemTheme(true);
        await Task.Delay(200);
        var light = Render(panel, 420, 48, Path.Combine(folder, "light.png"));
        var lightForeground = panel.Foreground.ToString() == "#FF000000" && HasForeground(light, true);
        var glyphHashes = new Dictionary<DeviceKind, string>();
        foreach (var kind in new[] { DeviceKind.Headset, DeviceKind.Earbuds, DeviceKind.Headphones, DeviceKind.Mouse, DeviceKind.Keyboard, DeviceKind.Gamepad })
        {
            var glyph = new DeviceGlyph { Kind = kind, Foreground = Brushes.White, Width = 32, Height = 32 };
            glyph.Measure(new Size(32, 32)); glyph.Arrange(new Rect(0, 0, 32, 32));
            glyphHashes[kind] = Convert.ToHexString(SHA256.HashData(Render(glyph, 32, 32, Path.Combine(folder, $"icon-{kind}.png"))));
        }
        var distinctIcons = glyphHashes.Values.Distinct().Count() == glyphHashes.Count;
        viewModel.SetDeviceKind("test:gamepad", DeviceKind.Headphones);
        var gamepadOverrideWorks = viewModel.Devices.First(d => d.Device.Id == "test:gamepad").Kind == DeviceKind.Headphones;
        viewModel.SetDeviceKind("test:gamepad", null);
        gamepadOverrideWorks &= viewModel.Devices.First(d => d.Device.Id == "test:gamepad").Kind == DeviceKind.Gamepad;
        viewModel.SetDeviceKind("test:headset", DeviceKind.Earbuds);
        var overrideWorks = viewModel.Devices.First(d => d.Device.Id == "test:headset").Kind == DeviceKind.Earbuds;
        fixtures.Connected = false;
        await viewModel.RefreshAsync();
        var restarted = new DashboardViewModel(new BatteryMonitor([fixtures]), settings, default);
        await restarted.RefreshAsync();
        var retainedOffline = restarted.Devices.Count == 0 && restarted.Settings.DeviceKinds["test:headset"] == DeviceKind.Earbuds;
        fixtures.Connected = true; fixtures.HeadsetName = "Renamed audio device";
        await restarted.RefreshAsync();
        var restoredAfterReconnect = restarted.Devices.First(d => d.Device.Id == "test:headset") is { Kind: DeviceKind.Earbuds, Name: "Renamed audio device" };
        restarted.SetDeviceKind("test:headset", null);
        var automaticRestored = restarted.Devices.First(d => d.Device.Id == "test:headset").Kind == DeviceKind.Headset;
        await viewModel.RefreshAsync();
        var menuItems = Descendants(panel.ContextMenu!.Items).ToArray();
        var uniformMenuIcons = menuItems.All(item => item.Icon is SvgIcon { Width: SvgIcon.MenuSize, Height: SvgIcon.MenuSize });
        var menuIconsRender = true;
        foreach (var (item, index) in menuItems.Select((item, index) => (item, index)))
        {
            var original = (SvgIcon)item.Icon;
            var icon = new SvgIcon { Asset = original.Asset, Foreground = Brushes.White };
            icon.Measure(new Size(18, 18)); icon.Arrange(new Rect(0, 0, 18, 18));
            var pixels = Render(icon, 18, 18, Path.Combine(folder, $"menu-icon-{index}.png"));
            menuIconsRender &= Enumerable.Range(0, 18 * 18).Count(i => pixels[i * 4 + 3] > 20) >= 12;
        }
        var originalLanguage = viewModel.Language;
        viewModel.Language = "en";
        await Task.Delay(100);
        var openMenuItem = panel.ContextMenu!.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetAutomationId(item) == "Taskbar.Open");
        var englishLive = openMenuItem.Header?.ToString() == "Open Pulse" && viewModel.Devices[0].Status == "Charging" && viewModel.Settings.Language == "en";
        var englishComplete = Strings.Current.Keys.All(k => !string.IsNullOrWhiteSpace(Strings.Current[k]) && !Strings.Current[k].StartsWith('['));
        var englishGamepad = viewModel.Devices.First(d => d.Device.Id == "test:gamepad").Category == "GAMEPAD" && Strings.Current["KindGamepad"] == "Gamepad";
        var dashboard = new Dashboard(viewModel);
        viewModel.SettingsOpen = true;
        var dashboardContent = (FrameworkElement)dashboard.Content;
        dashboardContent.Measure(new Size(960, 690)); dashboardContent.Arrange(new Rect(0, 0, 960, 690)); dashboardContent.UpdateLayout();
        await Task.Delay(100);
        Render(dashboardContent, 960, 690, Path.Combine(folder, "dashboard-english.png"));
        viewModel.Language = "uk";
        await Task.Delay(100);
        var ukrainianLive = openMenuItem.Header?.ToString() == "Відкрити Pulse" && viewModel.Devices[0].Status == "Заряджається";
        var ukrainianComplete = Strings.Current.Keys.All(k => !string.IsNullOrWhiteSpace(Strings.Current[k]) && !Strings.Current[k].StartsWith('['));
        var formalEarbudName = Strings.Current["KindEarbuds"] == "Навушники-вкладиші";
        var ukrainianGamepad = viewModel.Devices.First(d => d.Device.Id == "test:gamepad").Category == "ГЕЙМПАД" && Strings.Current["KindGamepad"] == "Геймпад";
        var passed = taskbarVisibilityPassed && startupConsentPassed && deviceOrderPassed && animationChanged && hitArea && darkForeground && lightForeground && distinctIcons && overrideWorks && gamepadOverrideWorks && englishGamepad && ukrainianGamepad && retainedOffline && restoredAfterReconnect && automaticRestored && uniformMenuIcons && menuIconsRender && formalEarbudName && englishLive && ukrainianLive && englishComplete && ukrainianComplete;
        viewModel.Language = originalLanguage;
        File.WriteAllText(Path.Combine(folder, "presentation-checks.json"), JsonSerializer.Serialize(new
        {
            TestFixtures = true, Passed = passed, ChargingAnimationChanges = animationChanged,
            DeviceOrderPersistsAndMatchesTaskbar = deviceOrderPassed,
            StartupRequiresConsentAndCanBeChanged = startupConsentPassed,
            PerDeviceTaskbarVisibilityPersists = taskbarVisibilityPassed,
            EntireRectangleReceivesInput = hitArea, DarkThemeWhite = darkForeground, LightThemeBlack = lightForeground,
            SeparateDeviceIcons = distinctIcons, ManualIconOverride = overrideWorks,
            SeparateGamepadIcon = glyphHashes.ContainsKey(DeviceKind.Gamepad) && distinctIcons,
            GamepadManualOverrideAndAutomatic = gamepadOverrideWorks, GamepadTranslatedBothLanguages = englishGamepad && ukrainianGamepad,
            ChoiceRetainedWhileOffline = retainedOffline, ChoiceRestoredAfterRestartAndRename = restoredAfterReconnect,
            AutomaticSelectionRemovesOverride = automaticRestored, MenuIcons18By18 = uniformMenuIcons,
            AllMenuSvgIconsRender = menuIconsRender, FormalEarbudName = formalEarbudName,
            EnglishSwitchesLive = englishLive, UkrainianSwitchesLive = ukrainianLive,
            BothLanguagesComplete = englishComplete && ukrainianComplete
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }
    private static IEnumerable<MenuItem> Descendants(ItemCollection items)
    {
        foreach (var item in items.OfType<MenuItem>())
        {
            yield return item;
            foreach (var child in Descendants(item.Items)) yield return child;
        }
    }
    private static byte[] Render(Visual visual, int width, int height, string path)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
        return pixels;
    }
    private static bool HasForeground(byte[] pixels, bool light)
    {
        var count = 0;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index + 3] < 200) continue;
            var sum = pixels[index] + pixels[index + 1] + pixels[index + 2];
            if (light ? sum <= 60 : sum >= 600) count++;
        }
        return count > 20;
    }
    private sealed class FixtureProvider : IBatteryProvider
    {
        public bool Connected { get; set; } = true;
        public string HeadsetName { get; set; } = "Test headset";
        public Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<BatteryDevice>>(!Connected ? [] : [
            new("test:mouse", "Test mouse", DeviceKind.Mouse, 64, true, ReadingState.Available, "test", "test"),
            new("test:headset", HeadsetName, DeviceKind.Headset, 83, false, ReadingState.Available, "test", "test"),
            new("test:earbuds", "Test earbuds", DeviceKind.Earbuds, 46, null, ReadingState.Available, "test", "test"),
            new("test:keyboard", "Test keyboard", DeviceKind.Keyboard, 72, false, ReadingState.Available, "test", "test"),
            new("test:gamepad", "Test controller", DeviceKind.Gamepad, 64, false, ReadingState.Available, "test", "test")
        ]);
    }
    private sealed class MemorySettings : ISettingsStore
    {
        private AppSettings _settings = new();
        public AppSettings Load() => _settings;
        public void Save(AppSettings settings) => _settings = settings;
    }
}
