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
        ["Common.Apply"] = "Apply",
        ["Common.Unknown"] = "(unknown)",
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
        ["Library.AllGroups"] = "All groups",
        ["Library.ManageGroups"] = "Groups…",
        ["Library.Dialog.DeleteTitle"] = "Delete camera",
        ["Library.Dialog.DeleteMessage"] = "Delete '{0}'? This cannot be undone.",
        ["Library.Dialog.NewCamerasTitle"] = "New cameras found",
        ["Library.Dialog.NewCamerasMessage"] = "Auto-scan found {0} new camera(s). Open Discovery?",
        ["Library.Dialog.OpenDiscovery"] = "Open Discovery",
        ["Library.Dialog.NotNow"] = "Not now",

        ["Groups.WindowTitle"] = "Manage groups",
        ["Groups.Title"] = "Camera groups",
        ["Groups.Empty"] = "No groups yet",
        ["Groups.NewName"] = "New group name",
        ["Groups.Add"] = "Add",

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
        ["Settings.Advanced.RawConfigEditor"] = "Allow raw Majestic config editing (advanced)",
        ["Settings.Advanced.OpenAppData"] = "Open app data folder",

        ["Settings.About.Repository"] = "GitHub repository →",

        ["Snapshot.Saved"] = "Saved →",
        ["Snapshot.Open"] = "Open",
        ["Snapshot.Copy"] = "Copy",
        ["Snapshot.SaveAs"] = "Save as…",

        ["CameraEditor.Title.Add"] = "Add camera",
        ["CameraEditor.Title.Edit"] = "Edit camera",
        ["CameraEditor.Label.Name"] = "Name",
        ["CameraEditor.Label.Host"] = "Host",
        ["CameraEditor.Label.HttpPort"] = "HTTP port",
        ["CameraEditor.Label.OnvifPort"] = "ONVIF port (optional)",
        ["CameraEditor.Label.RtspMain"] = "RTSP main URI",
        ["CameraEditor.Label.RtspSub"] = "RTSP sub URI (optional)",
        ["CameraEditor.Label.Username"] = "Username",
        ["CameraEditor.Label.Password"] = "Password",
        ["CameraEditor.Label.Group"] = "Group",
        ["CameraEditor.Placeholder.Name"] = "Front door",
        ["CameraEditor.Placeholder.Host"] = "192.168.1.10",
        ["CameraEditor.Placeholder.OnvifPort"] = "8899",
        ["CameraEditor.Placeholder.RtspMain"] = "rtsp://192.168.1.10/",
        ["CameraEditor.Placeholder.RtspSub"] = "rtsp://192.168.1.10/stream1",
        ["CameraEditor.Placeholder.Username"] = "admin",
        ["CameraEditor.NoGroup"] = "(no group)",
        ["CameraEditor.Button.AutoDeriveRtsp"] = "Auto from host",
        ["CameraEditor.Button.TestConnection"] = "Test connection",
        ["CameraEditor.Status.Connecting"] = "Connecting…",
        ["CameraEditor.Status.OkFormat"] = "OK — {0}x{1}",
        ["CameraEditor.Status.Timeout"] = "Timeout (8s)",
        ["CameraEditor.Status.FailedFormat"] = "Failed: {0}",
        ["CameraEditor.Error.NameRequired"] = "Name is required.",
        ["CameraEditor.Error.HostRequired"] = "Host is required.",
        ["CameraEditor.Error.RtspMainInvalid"] = "RTSP main URI is not a valid absolute URI.",
        ["CameraEditor.Error.RtspSubInvalid"] = "RTSP sub URI is not a valid absolute URI.",
        ["CameraEditor.Error.OnvifPortInvalid"] = "ONVIF port must be between 1 and 65535.",
        ["CameraEditor.Error.HttpPortInvalid"] = "HTTP port must be between 1 and 65535.",

        ["Discovery.Title"] = "Discover cameras",
        ["Discovery.Header"] = "Discover ONVIF cameras",
        ["Discovery.Button.Scan"] = "Scan",
        ["Discovery.Button.AddSelected"] = "Add selected",
        ["Discovery.Status.Initial"] = "Click Scan to find ONVIF cameras on the LAN.",
        ["Discovery.Status.Scanning"] = "Scanning…",
        ["Discovery.Status.NoResponse"] = "No cameras responded. Check multicast / firewall.",
        ["Discovery.Status.FoundFormat"] = "Found {0} camera(s).",
        ["Discovery.Status.Cancelled"] = "Scan cancelled.",
        ["Discovery.Status.ScanFailedFormat"] = "Scan failed: {0}",
        ["Discovery.Status.ProbingFormat"] = "Probing {0}…",
        ["Discovery.Status.ProbeOkFormat"] = "OK — {0} {1}",
        ["Discovery.Status.ProbeFailedFormat"] = "Probe failed: {0}",

        ["RawConfigEditor.Title"] = "Edit raw Majestic config",
        ["RawConfigEditor.InvalidJsonFormat"] = "Invalid JSON: {0}",

        ["Recordings.Dialog.DeleteTitle"] = "Delete recording",
        ["Recordings.Dialog.DeleteMessageFormat"] = "Delete {0}? The MP4 file will be removed.",
        ["Recordings.Empty"] = "No recordings yet. Hit ● REC on a camera page to start.",
        ["Recordings.Live"] = "live",

        ["Events.Button.SimulateMotion"] = "Simulate motion",
        ["Events.Empty"] = "No events yet. Hit Simulate motion to push one through the pipeline.",
        ["Events.AllCameras"] = "All cameras",
        ["Events.Open"] = "open",

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
        ["Common.Apply"] = "Применить",
        ["Common.Unknown"] = "(неизвестно)",
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
        ["Library.AllGroups"] = "Все группы",
        ["Library.ManageGroups"] = "Группы…",
        ["Library.Dialog.DeleteTitle"] = "Удалить камеру",
        ["Library.Dialog.DeleteMessage"] = "Удалить «{0}»? Это нельзя отменить.",
        ["Library.Dialog.NewCamerasTitle"] = "Найдены новые камеры",
        ["Library.Dialog.NewCamerasMessage"] = "Автопоиск нашёл новых камер: {0}. Открыть Discovery?",
        ["Library.Dialog.OpenDiscovery"] = "Открыть Discovery",
        ["Library.Dialog.NotNow"] = "Не сейчас",

        ["Groups.WindowTitle"] = "Управление группами",
        ["Groups.Title"] = "Группы камер",
        ["Groups.Empty"] = "Групп пока нет",
        ["Groups.NewName"] = "Название новой группы",
        ["Groups.Add"] = "Добавить",

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
        ["Settings.Advanced.RawConfigEditor"] = "Разрешить правку raw-конфига Majestic (продвинутое)",
        ["Settings.Advanced.OpenAppData"] = "Открыть папку данных",

        ["Settings.About.Repository"] = "Репозиторий на GitHub →",

        ["Snapshot.Saved"] = "Сохранено →",
        ["Snapshot.Open"] = "Открыть",
        ["Snapshot.Copy"] = "Копировать",
        ["Snapshot.SaveAs"] = "Сохранить как…",

        ["CameraEditor.Title.Add"] = "Добавить камеру",
        ["CameraEditor.Title.Edit"] = "Редактировать камеру",
        ["CameraEditor.Label.Name"] = "Имя",
        ["CameraEditor.Label.Host"] = "Хост",
        ["CameraEditor.Label.HttpPort"] = "HTTP-порт",
        ["CameraEditor.Label.OnvifPort"] = "ONVIF-порт (необязательно)",
        ["CameraEditor.Label.RtspMain"] = "RTSP main URI",
        ["CameraEditor.Label.RtspSub"] = "RTSP sub URI (необязательно)",
        ["CameraEditor.Label.Username"] = "Логин",
        ["CameraEditor.Label.Password"] = "Пароль",
        ["CameraEditor.Label.Group"] = "Группа",
        ["CameraEditor.Placeholder.Name"] = "Входная",
        ["CameraEditor.Placeholder.Host"] = "192.168.1.10",
        ["CameraEditor.Placeholder.OnvifPort"] = "8899",
        ["CameraEditor.Placeholder.RtspMain"] = "rtsp://192.168.1.10/",
        ["CameraEditor.Placeholder.RtspSub"] = "rtsp://192.168.1.10/stream1",
        ["CameraEditor.Placeholder.Username"] = "admin",
        ["CameraEditor.NoGroup"] = "(без группы)",
        ["CameraEditor.Button.AutoDeriveRtsp"] = "Подставить из хоста",
        ["CameraEditor.Button.TestConnection"] = "Проверить",
        ["CameraEditor.Status.Connecting"] = "Подключение…",
        ["CameraEditor.Status.OkFormat"] = "OK — {0}x{1}",
        ["CameraEditor.Status.Timeout"] = "Таймаут (8 с)",
        ["CameraEditor.Status.FailedFormat"] = "Ошибка: {0}",
        ["CameraEditor.Error.NameRequired"] = "Имя обязательно.",
        ["CameraEditor.Error.HostRequired"] = "Хост обязателен.",
        ["CameraEditor.Error.RtspMainInvalid"] = "RTSP main URI должен быть абсолютным URI.",
        ["CameraEditor.Error.RtspSubInvalid"] = "RTSP sub URI должен быть абсолютным URI.",
        ["CameraEditor.Error.OnvifPortInvalid"] = "ONVIF-порт должен быть от 1 до 65535.",
        ["CameraEditor.Error.HttpPortInvalid"] = "HTTP-порт должен быть от 1 до 65535.",

        ["Discovery.Title"] = "Поиск камер",
        ["Discovery.Header"] = "Поиск ONVIF-камер",
        ["Discovery.Button.Scan"] = "Сканировать",
        ["Discovery.Button.AddSelected"] = "Добавить выбранные",
        ["Discovery.Status.Initial"] = "Нажмите «Сканировать», чтобы найти ONVIF-камеры в локальной сети.",
        ["Discovery.Status.Scanning"] = "Сканирование…",
        ["Discovery.Status.NoResponse"] = "Камеры не ответили. Проверьте multicast / фаервол.",
        ["Discovery.Status.FoundFormat"] = "Найдено камер: {0}.",
        ["Discovery.Status.Cancelled"] = "Сканирование отменено.",
        ["Discovery.Status.ScanFailedFormat"] = "Сканирование не удалось: {0}",
        ["Discovery.Status.ProbingFormat"] = "Опрос {0}…",
        ["Discovery.Status.ProbeOkFormat"] = "OK — {0} {1}",
        ["Discovery.Status.ProbeFailedFormat"] = "Опрос не удался: {0}",

        ["RawConfigEditor.Title"] = "Правка raw-конфига Majestic",
        ["RawConfigEditor.InvalidJsonFormat"] = "Невалидный JSON: {0}",

        ["Recordings.Dialog.DeleteTitle"] = "Удалить запись",
        ["Recordings.Dialog.DeleteMessageFormat"] = "Удалить {0}? MP4-файл будет удалён.",
        ["Recordings.Empty"] = "Записей пока нет. Нажмите ● REC на странице камеры, чтобы начать.",
        ["Recordings.Live"] = "идёт",

        ["Events.Button.SimulateMotion"] = "Симулировать движение",
        ["Events.Empty"] = "Событий пока нет. Нажмите «Симулировать движение», чтобы прогнать одно через конвейер.",
        ["Events.AllCameras"] = "Все камеры",
        ["Events.Open"] = "открыто",

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
