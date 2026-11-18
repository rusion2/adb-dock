using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdbDock.Core;

public sealed class SettingsStore
{
    private readonly object gate = new();
    private readonly JsonSerializerOptions options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public string DirectoryPath { get; }
    public string? LoadError { get; private set; }
    public SettingsStore(string? directory = null)
    {
        DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdbDock");
    }
    public AppSettings Load()
    {
        var path = Path.Combine(DirectoryPath, "settings.json");
        if (!File.Exists(path)) return new();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var settings = root.Deserialize<AppSettings>(options) ?? new();
            if (root.ValueKind == JsonValueKind.Object &&
                !root.TryGetProperty(nameof(AppSettings.ShowFloatingTab), out _) &&
                !root.TryGetProperty(nameof(AppSettings.AutoCollapseDelaySeconds), out _))
            {
                // Old versions ignored AutoCollapse and used the header's Pinned preference.
                // New saves contain the new fields and omit Pinned, so migration runs only once.
                var wasPinned = root.TryGetProperty("Pinned", out var pinned) && pinned.ValueKind == JsonValueKind.True;
                settings.AutoCollapse = !wasPinned;
            }
            settings.AdbPath ??= "";
            settings.ScrcpyPath ??= "";
            if (string.IsNullOrWhiteSpace(settings.ScreenshotDirectory)) settings.ScreenshotDirectory = new AppSettings().ScreenshotDirectory;
            if (!Enum.IsDefined(settings.Side)) settings.Side = DockSide.None;
            if (settings.AutoCollapseDelaySeconds is < AppSettings.MinimumAutoCollapseDelaySeconds or > AppSettings.MaximumAutoCollapseDelaySeconds)
                settings.AutoCollapseDelaySeconds = AppSettings.DefaultAutoCollapseDelaySeconds;
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoadError = "配置读取失败，已使用默认值：" + ex.Message;
            Log(LoadError);
            return new();
        }
    }
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.AutoCollapseDelaySeconds is < AppSettings.MinimumAutoCollapseDelaySeconds or > AppSettings.MaximumAutoCollapseDelaySeconds)
            throw new ArgumentOutOfRangeException(nameof(settings), "自动收起等待时间必须为 1 至 300 秒。");
        lock (gate)
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = Path.Combine(DirectoryPath, "settings.json");
            if (LoadError is not null && File.Exists(path))
            {
                File.Copy(path, Path.Combine(DirectoryPath, $"settings.invalid-{DateTime.Now:yyyyMMddHHmmssfff}.json"), false);
                LoadError = null;
            }
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, options));
            File.Move(temporary, path, true);
        }
    }
    public void Log(string message)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                var file = Path.Combine(DirectoryPath, "errors.log");
                if (File.Exists(file) && new FileInfo(file).Length > 1_000_000) File.Move(file, file + ".1", true);
                File.AppendAllText(file, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
