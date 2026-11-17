namespace AdbDock.Core;

public sealed record AdbDevice(string Serial, string Name, string State, string Connection)
{
    public bool IsOnline => State == "device";
    public string StatusLabel => State switch
    {
        "device" => "在线",
        "unauthorized" => "未授权",
        "offline" => "离线",
        _ => State
    };
    public string Marker => State switch { "device" => "✓", "unauthorized" => "!", _ => "−" };
}

public sealed record DeviceSnapshot(IReadOnlyList<AdbDevice> Devices, string? Error = null)
{
    public bool IsAvailable => Error is null;
    public int OnlineCount => Devices.Count(d => d.IsOnline);
}

public sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    public string Failure => string.IsNullOrWhiteSpace(Error) ? Output.Trim() : Error.Trim();
    public void EnsureSuccess()
    {
        if (ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(Failure) ? $"进程退出码：{ExitCode}" : Failure);
    }
}

public enum DockSide { None, Left, Right, Top, Bottom }

public sealed class AppSettings
{
    public string AdbPath { get; set; } = "";
    public string ScrcpyPath { get; set; } = "";
    public string ScreenshotDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "AdbDock");
    public bool Topmost { get; set; } = true;
    public bool AutoCollapse { get; set; } = true;
    public bool Pinned { get; set; }
    public bool AutoStart { get; set; }
    public DockSide Side { get; set; }
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
}
