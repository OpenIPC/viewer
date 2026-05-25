using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;

namespace OpenIPC.Viewer.App.Services;

// Tiny ICU-free localizer. Static singleton because XAML's {Binding [Key],
// Source={x:Static svc:Localizer.Instance}} pattern needs a static reference.
// PropertyChanged on "Item[]" tells all indexed bindings to re-evaluate when
// SetLanguage flips the active dict — no relaunch needed.
//
// EN/RU only for now; adding a third language = one more dict + one more
// LangCode value. Missing keys fall back to the key itself, so adding a new
// string in XAML before translating shows up as the key (loud failure mode).
public sealed class Localizer : INotifyPropertyChanged
{
    public static Localizer Instance { get; } = new();

    private IReadOnlyDictionary<string, string> _current = English;
    private LangCode _active = LangCode.English;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => _current.TryGetValue(key, out var v) ? v : key;
    public LangCode Active => _active;

    public void SetLanguage(LangCode code)
    {
        var resolved = code == LangCode.System ? DetectSystem() : code;
        if (resolved == _active) return;

        _current = resolved switch
        {
            LangCode.Russian => Russian,
            _ => English,
        };
        _active = resolved;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    private static LangCode DetectSystem()
    {
        var name = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return name.Equals("ru", System.StringComparison.OrdinalIgnoreCase)
            ? LangCode.Russian
            : LangCode.English;
    }

    private static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>
    {
        ["Nav.Live"] = "Live",
        ["Nav.Library"] = "Library",
        ["Nav.Recordings"] = "Recordings",
        ["Nav.RecordingsShort"] = "Records",
        ["Nav.Events"] = "Events",
        ["Nav.Settings"] = "Settings",

        ["Common.Cancel"] = "Cancel",
        ["Common.Delete"] = "Delete",
        ["Common.Save"] = "Save",
        ["Common.Back"] = "← Back",
        ["Common.Refresh"] = "⟲ Refresh",
        ["Common.Discover"] = "🔍 Discover",
        ["Common.AddCamera"] = "+ Add camera",

        ["Library.Title"] = "Cameras",
        ["Library.EmptyTitle"] = "No cameras yet",
        ["Library.RowEdit"] = "Edit",
        ["Library.RowDelete"] = "Delete",
        ["Library.RowShowInGrid"] = "Show in grid",
        ["Library.Offline"] = "OFFLINE",

        ["Welcome.Title"] = "OpenIPC Viewer",
        ["Welcome.Tagline"] = "Cross-platform viewer for OpenIPC IP cameras",
        ["Welcome.Lead"] = "Add your first camera to get started.",
        ["Welcome.Discover"] = "Scan local network (WS-Discovery)",
        ["Welcome.AddManually"] = "Add manually (host + RTSP path)",
        ["Welcome.Skip"] = "Skip — I'll add later",
        ["Welcome.WindowTitle"] = "Welcome to OpenIPC Viewer",

        ["Settings.Title"] = "Settings",
        ["Settings.Appearance"] = "Appearance",
        ["Settings.Appearance.Language"] = "Language",
        ["Settings.Video"] = "Video",
        ["Settings.Recording"] = "Recording",
        ["Settings.Discovery"] = "Discovery",
        ["Settings.Advanced"] = "Advanced",
        ["Settings.About"] = "About",

        ["Settings.Video.TelemetryOverlay"] = "Show telemetry overlay on live view",
        ["Settings.Video.MaxGridSessions"] = "Max concurrent grid sessions",
        ["Settings.Video.RtspTransport"] = "Default RTSP transport",

        ["Settings.Recording.Directory"] = "Recordings directory",
        ["Settings.Recording.PickFolder"] = "Pick folder…",
        ["Settings.Recording.Reset"] = "Reset to default",
        ["Settings.Recording.Open"] = "Open",

        ["Settings.Discovery.AutoScan"] = "Run WS-Discovery on launch",

        ["Settings.Advanced.VerboseLogging"] = "Verbose logging (debug level)",
        ["Settings.Advanced.OpenAppData"] = "Open app data folder",

        ["Settings.About.Repository"] = "GitHub repository →",

        ["Lang.System"] = "System",
        ["Lang.English"] = "English",
        ["Lang.Russian"] = "Русский",
    };

    private static readonly IReadOnlyDictionary<string, string> Russian = new Dictionary<string, string>
    {
        ["Nav.Live"] = "Эфир",
        ["Nav.Library"] = "Камеры",
        ["Nav.Recordings"] = "Записи",
        ["Nav.RecordingsShort"] = "Записи",
        ["Nav.Events"] = "События",
        ["Nav.Settings"] = "Настройки",

        ["Common.Cancel"] = "Отмена",
        ["Common.Delete"] = "Удалить",
        ["Common.Save"] = "Сохранить",
        ["Common.Back"] = "← Назад",
        ["Common.Refresh"] = "⟲ Обновить",
        ["Common.Discover"] = "🔍 Поиск",
        ["Common.AddCamera"] = "+ Добавить камеру",

        ["Library.Title"] = "Камеры",
        ["Library.EmptyTitle"] = "Камер пока нет",
        ["Library.RowEdit"] = "Изменить",
        ["Library.RowDelete"] = "Удалить",
        ["Library.RowShowInGrid"] = "В гриде",
        ["Library.Offline"] = "ОФЛАЙН",

        ["Welcome.Title"] = "OpenIPC Viewer",
        ["Welcome.Tagline"] = "Кросс-платформенный просмотрщик IP-камер OpenIPC",
        ["Welcome.Lead"] = "Добавьте первую камеру, чтобы начать.",
        ["Welcome.Discover"] = "Найти в локальной сети (WS-Discovery)",
        ["Welcome.AddManually"] = "Ввести вручную (host + RTSP)",
        ["Welcome.Skip"] = "Пропустить — добавлю позже",
        ["Welcome.WindowTitle"] = "Добро пожаловать в OpenIPC Viewer",

        ["Settings.Title"] = "Настройки",
        ["Settings.Appearance"] = "Внешний вид",
        ["Settings.Appearance.Language"] = "Язык",
        ["Settings.Video"] = "Видео",
        ["Settings.Recording"] = "Запись",
        ["Settings.Discovery"] = "Поиск",
        ["Settings.Advanced"] = "Дополнительно",
        ["Settings.About"] = "О приложении",

        ["Settings.Video.TelemetryOverlay"] = "Показывать телеметрию на видео",
        ["Settings.Video.MaxGridSessions"] = "Максимум потоков в гриде",
        ["Settings.Video.RtspTransport"] = "RTSP-транспорт по умолчанию",

        ["Settings.Recording.Directory"] = "Папка записей",
        ["Settings.Recording.PickFolder"] = "Выбрать…",
        ["Settings.Recording.Reset"] = "Сбросить",
        ["Settings.Recording.Open"] = "Открыть",

        ["Settings.Discovery.AutoScan"] = "Запускать WS-Discovery при старте",

        ["Settings.Advanced.VerboseLogging"] = "Подробные логи (debug)",
        ["Settings.Advanced.OpenAppData"] = "Открыть папку данных",

        ["Settings.About.Repository"] = "Репозиторий на GitHub →",

        ["Lang.System"] = "Системный",
        ["Lang.English"] = "English",
        ["Lang.Russian"] = "Русский",
    };
}

public enum LangCode
{
    System = 0,
    English,
    Russian,
}
