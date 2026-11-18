using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace PulseBattery.App.Localization;

public sealed class Strings : INotifyPropertyChanged
{
    public static Strings Current { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LanguageChanged;
    public string Language { get; private set; } = "uk";
    public CultureInfo Culture => CultureInfo.GetCultureInfo(Language == "en" ? "en-US" : "uk-UA");
    public IEnumerable<string> Keys => Entries.Keys;
    public string this[string key] => Entries.TryGetValue(key, out var entry) ? Language == "en" ? entry.En : entry.Uk : $"[{key}]";
    public string Format(string key, params object?[] values) => string.Format(Culture, this[key], values);
    public void SetLanguage(string language)
    {
        var code = language == "en" ? "en" : "uk";
        if (Language == code) return;
        Language = code;
        PropertyChanged?.Invoke(this, new("Item[]"));
        PropertyChanged?.Invoke(this, new(nameof(Language)));
        LanguageChanged?.Invoke();
    }
    public string TranslateDetail(string value)
    {
        if (Language != "en") return value;
        foreach (var entry in Entries.Values)
            if (entry.Uk == value) return entry.En;
        const string savePrefix = "Не вдалося зберегти налаштування: ";
        if (value.StartsWith(savePrefix, StringComparison.Ordinal)) return Format("SaveError", value[savePrefix.Length..]);
        return Details.TryGetValue(value, out var translation) ? translation : value;
    }
    private static readonly Dictionary<string, (string Uk, string En)> Entries = new()
    {
        ["WindowTitle"] = ("Pulse — заряд пристроїв", "Pulse — device batteries"),
        ["Tagline"] = ("МОНІТОР ЗАРЯДУ", "BATTERY MONITOR"),
        ["Live"] = ("● НАЖИВО", "● LIVE"),
        ["Settings"] = ("Налаштування", "Settings"),
        ["Minimize"] = ("Згорнути в трей", "Minimize to tray"),
        ["Hide"] = ("Сховати в трей", "Hide to tray"),
        ["Eyebrow"] = ("ТВІЙ БЕЗДРОТОВИЙ ПРОСТІР", "YOUR WIRELESS SPACE"),
        ["Headline"] = ("Заряд. Без зайвого.", "Battery. At a glance."),
        ["Subtitle"] = ("Bluetooth та USB-пристрої — усі рівні заряду в одному місці.", "Bluetooth and USB devices — every battery in one place."),
        ["ConnectTitle"] = ("Підключи свої пристрої", "Connect your devices"),
        ["ConnectDescription"] = ("Підключи Bluetooth-пристрої або встав USB-приймачі.\nЗаряд читається з Windows, стандартних HID-звітів і підтримуваних протоколів виробників.", "Connect Bluetooth devices or plug in their USB receivers.\nBattery levels come from Windows, standard HID reports and supported vendor protocols."),
        ["Retry"] = ("Спробувати знову", "Try again"),
        ["TaskbarButton"] = ("Панель біля ^", "Panel next to ^"),
        ["BackgroundStatus"] = ("Працює у треї", "Running in the tray"),
        ["Exit"] = ("Вийти", "Exit"),
        ["RefreshSeconds"] = ("Оновлення, секунд", "Refresh interval, seconds"),
        ["LowThreshold"] = ("Низький заряд, %", "Low battery threshold, %"),
        ["Notifications"] = ("Сповіщення про низький заряд", "Low battery notifications"),
        ["TaskbarSetting"] = ("Показувати заряд на таскбарі", "Show battery levels on the taskbar"),
        ["TaskbarSize"] = ("Розмір показників у таскбарі", "Taskbar indicator size"),
        ["TaskbarSizeCompact"] = ("Компактний", "Compact"),
        ["TaskbarSizeStandard"] = ("Стандартний", "Standard"),
        ["TaskbarSizeLarge"] = ("Великий", "Large"),
        ["TaskbarSizeHint"] = ("Іконки, текст і відступи змінюються разом.", "Icons, text and spacing change together."),
        ["PulsePosition"] = ("Розташування іконки Pulse", "Pulse icon position"),
        ["PulsePositionLeft"] = ("Зліва від пристроїв", "Left of devices"),
        ["PulsePositionRight"] = ("Справа від пристроїв", "Right of devices"),
        ["AutoSave"] = ("Налаштування зберігаються автоматично.", "Settings are saved automatically."),
        ["StartWithWindows"] = ("Запускати разом із Windows", "Start with Windows"),
        ["StartupTitle"] = ("Запускати Pulse разом із Windows?", "Start Pulse with Windows?"),
        ["StartupDescription"] = ("Pulse запускатиметься у фоні після входу у Windows й одразу показуватиме заряд пристроїв на панелі задач.", "Pulse will start in the background when you sign in to Windows and show device batteries on the taskbar."),
        ["StartupChoiceHint"] = ("Це можна змінити будь-коли в налаштуваннях.", "You can change this anytime in Settings."),
        ["StartupEnable"] = ("Увімкнути", "Enable"),
        ["StartupDecline"] = ("Ні, дякую", "No, thanks"),
        ["StartupSaveError"] = ("Не вдалося змінити автозапуск: {0}", "Could not change startup: {0}"),
        ["Language"] = ("Мова", "Language"),
        ["Open"] = ("Відкрити Pulse", "Open Pulse"),
        ["RefreshMenu"] = ("Оновити заряд", "Refresh batteries"),
        ["IconMenu"] = ("Іконка пристрою", "Device icon"),
        ["ChangeDeviceIcon"] = ("Змінити іконку пристрою", "Change device icon"),
        ["ShowOnTaskbar"] = ("Показувати на таскбарі", "Show on taskbar"),
        ["HideDevice"] = ("Приховати з таскбара", "Hide from taskbar"),
        ["Automatic"] = ("Автовизначення", "Automatic"),
        ["KindHeadset"] = ("Гарнітура", "Headset"),
        ["KindEarbuds"] = ("Навушники-вкладиші", "Earbuds"),
        ["KindHeadphones"] = ("Навушники", "Headphones"),
        ["KindMouse"] = ("Мишка", "Mouse"),
        ["KindKeyboard"] = ("Клавіатура", "Keyboard"),
        ["KindGamepad"] = ("Геймпад", "Gamepad"),
        ["KindOther"] = ("Пристрій", "Device"),
        ["Charging"] = ("Заряджається", "Charging"),
        ["NoData"] = ("Немає даних", "No battery data"),
        ["LastReading"] = ("Останні дані о {0:HH:mm}", "Last reading at {0:HH:mm}"),
        ["ChargeSoon"] = ("Час зарядити", "Charge soon"),
        ["OnBattery"] = ("На батареї", "On battery"),
        ["Connected"] = ("●  Підключено", "●  Connected"),
        ["Waiting"] = ("◷  Очікування", "◷  Waiting"),
        ["NoResponse"] = ("○  Без відповіді", "○  Not responding"),
        ["Searching"] = ("Шукаємо твої пристрої…", "Looking for your devices…"),
        ["Reading"] = ("Читаємо заряд…", "Reading batteries…"),
        ["RefreshButton"] = ("↻  Оновити", "↻  Refresh"),
        ["UpdatedAt"] = ("Оновлено о {0:HH:mm:ss}", "Updated at {0:HH:mm:ss}"),
        ["DeviceCount"] = ("{0:00} / ПРИСТРОЇ", "{0:00} / DEVICES"),
        ["ReorderHint"] = ("Перетягни картки, щоб змінити порядок", "Drag cards to change their order"),
        ["ReorderDevice"] = ("Перетягни на іншу картку — порядок зміниться й на панелі задач", "Drag onto another card to change the taskbar order too"),
        ["HealthLow"] = ("Час підзарядитися", "Time to recharge"),
        ["HealthGood"] = ("Усе під контролем", "Everything looks good"),
        ["HealthWaiting"] = ("Чекаємо на пристрої", "Waiting for devices"),
        ["RefreshHint"] = ("Автооновлення кожні {0} с", "Refreshes automatically every {0} s"),
        ["Timeout"] = ("Пристрій довго не відповідає. Спробуй оновити ще раз.", "A device is taking too long to respond. Try refreshing again."),
        ["SaveError"] = ("Не вдалося зберегти налаштування: {0}", "Could not save settings: {0}"),
        ["TogglePanel"] = ("Показувати показники заряду", "Show battery levels"),
        ["BatteryAlertTitle"] = ("Час зарядити пристрій", "Time to charge your device"),
        ["BatteryAlertBody"] = ("{0}: залишилося {1}%", "{0}: {1}% battery remaining"),
        ["StartupError"] = ("Pulse — помилка запуску", "Pulse — startup error")
    };
    private static readonly Dictionary<string, string> Details = new()
    {
        ["Bluetooth · дані Windows"] = "Bluetooth · Windows battery report",
        ["Дані заряду Windows"] = "Windows battery report",
        ["Bluetooth · пристрій не передав рівень заряду Windows"] = "Bluetooth · the device has not reported a battery level to Windows",
        ["Стандартний HID Battery Strength"] = "Standard HID Battery Strength",
        ["Контролер не передав стандартний HID-рівень заряду"] = "The controller has not reported a standard HID battery level",
        ["Розбуди мишку або перевір підключення приймача."] = "Wake the mouse or check its receiver connection.",
        ["USB-кабель"] = "USB cable",
        ["LIGHTSPEED · USB-приймач"] = "LIGHTSPEED · USB receiver",
        ["2.4 ГГц · USB-приймач"] = "2.4 GHz · USB receiver",
        ["Увімкни навушники. Приймач знайдено, відповіді про заряд поки немає."] = "Turn on the headset. Its receiver was found but has not returned a battery level."
    };
}

public sealed class TextExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding($"[{key}]") { Source = Strings.Current, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
