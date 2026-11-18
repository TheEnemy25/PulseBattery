using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PulseBattery.App.Localization;
using PulseBattery.App.ViewModels;
using PulseBattery.App.Views;
using PulseBattery.Core;
using PulseBattery.Infrastructure;

namespace PulseBattery.App.Services;

internal static class StartupConsentChecks
{
    public static async Task<bool> RunAsync(string folder)
    {
        // All registrations below are fakes. Verification must never enable real Windows startup.
        var language = Strings.Current.Language;
        var path = Path.Combine(folder, "isolated-startup-settings.json");
        var store = new JsonSettingsStore(path);
        store.Save(new AppSettings { Language = language });
        var registration = new FakeRegistration();
        DashboardViewModel Create(ISettingsStore settings, IStartupRegistration startup) => new(new BatteryMonitor([]), settings, default, startup);
        var model = Create(store, registration);
        var asksBeforeConsent = model.NeedsStartupChoice && !model.StartWithWindows && registration.Calls == 0;
        var dismissed = new StartupPrompt(model);
        var safeDefault = dismissed.DeclineButton.IsDefault && !dismissed.EnableButton.IsDefault;
        dismissed.Close();
        var dismissKeepsUndecided = model.NeedsStartupChoice && store.Load().StartWithWindows is null && registration.Calls == 0;
        var enablesOnConsent = model.SetStartupPreference(true) && registration.Enabled && store.Load().StartWithWindows == true && !model.NeedsStartupChoice;
        model = Create(new JsonSettingsStore(path), registration);
        var noRepeatAfterYes = model.StartWithWindows && !model.NeedsStartupChoice;
        var disablesOnChoice = model.SetStartupPreference(false) && !registration.Enabled && store.Load().StartWithWindows == false;
        model = Create(new JsonSettingsStore(path), registration);
        var noRepeatAfterNo = !model.StartWithWindows && !model.NeedsStartupChoice;

        var rejectedRegistration = new FakeRegistration { RejectChanges = true };
        var blocked = Create(new MemoryStore(language), rejectedRegistration);
        var failureKeepsChoice = !blocked.SetStartupPreference(true) && !blocked.StartWithWindows && blocked.NeedsStartupChoice && blocked.HasError;
        var rollbackRegistration = new FakeRegistration();
        var failedSave = Create(new MemoryStore(language) { RejectSave = true }, rollbackRegistration);
        var rollsBackOnSaveFailure = !failedSave.SetStartupPreference(true) && !rollbackRegistration.Enabled && rollbackRegistration.Calls == 2 && failedSave.NeedsStartupChoice;

        var prompt = new StartupPrompt(model);
        Strings.Current.SetLanguage("uk");
        await Task.Delay(50);
        var uk = prompt.Title == Strings.Current["StartupTitle"] && prompt.EnableButton.Content?.ToString() == "Увімкнути" && prompt.DeclineButton.Content?.ToString() == "Ні, дякую";
        Render(prompt, Path.Combine(folder, "startup-prompt-uk.png"));
        Strings.Current.SetLanguage("en");
        await Task.Delay(50);
        var en = prompt.Title == "Start Pulse with Windows?" && prompt.EnableButton.Content?.ToString() == "Enable" && prompt.DeclineButton.Content?.ToString() == "No, thanks";
        Render(prompt, Path.Combine(folder, "startup-prompt-en.png"));
        prompt.Close();
        Strings.Current.SetLanguage(language);
        var passed = asksBeforeConsent && safeDefault && dismissKeepsUndecided && enablesOnConsent && noRepeatAfterYes && disablesOnChoice && noRepeatAfterNo && failureKeepsChoice && rollsBackOnSaveFailure && uk && en;
        File.WriteAllText(Path.Combine(folder, "startup-consent-checks.json"), JsonSerializer.Serialize(new
        {
            TestFixtures = true, RealStartupRegistrationTouched = false, Passed = passed,
            AsksBeforeConsent = asksBeforeConsent, DeclineIsDefault = safeDefault,
            DismissKeepsUndecided = dismissKeepsUndecided, EnablesOnlyOnConsent = enablesOnConsent,
            NoRepeatedPromptAfterYes = noRepeatAfterYes, CanDisableLater = disablesOnChoice,
            NoRepeatedPromptAfterNo = noRepeatAfterNo, FailedRegistrationDoesNotSaveConsent = failureKeepsChoice,
            FailedSaveRollsBackRegistration = rollsBackOnSaveFailure, UkrainianPrompt = uk, EnglishPrompt = en
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }

    private static void Render(StartupPrompt window, string path)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(460, double.PositiveInfinity));
        var height = Math.Max(1, (int)Math.Ceiling(content.DesiredSize.Height));
        content.Arrange(new Rect(0, 0, 460, height)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(460, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private sealed class FakeRegistration : IStartupRegistration
    {
        public bool Enabled { get; private set; }
        public int Calls { get; private set; }
        public bool RejectChanges { get; init; }
        public void SetEnabled(bool enabled)
        {
            Calls++;
            if (RejectChanges) throw new UnauthorizedAccessException("Simulated startup access failure.");
            Enabled = enabled;
        }
    }

    private sealed class MemoryStore(string language) : ISettingsStore
    {
        private AppSettings _settings = new() { Language = language };
        public bool RejectSave { get; init; }
        public AppSettings Load() => _settings;
        public void Save(AppSettings settings)
        {
            if (RejectSave) throw new IOException("Simulated settings write failure.");
            _settings = settings;
        }
    }
}
