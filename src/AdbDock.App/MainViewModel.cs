using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using AdbDock.Core;

namespace AdbDock.App;

public sealed class DeviceRow
{
    public AdbDevice Device { get; }
    public DeviceRow(AdbDevice device, string? alias = null)
    {
        Device = device;
        Alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
        EdgeLabel = Alias is null ? null : StringInfo.GetNextTextElement(Alias);
    }
    public string Serial => Device.Serial;
    public string? Alias { get; }
    public string Name => Alias ?? ModelName;
    public string ModelName => Device.Name;
    public string? EdgeLabel { get; }
    public string Connection => Device.Connection == "未知" ? "连接方式未识别" : Device.Connection;
    public string Status => Device.StatusLabel;
    public string Marker => Device.Marker;
    public bool IsOnline => Device.IsOnline;
    public bool NeedsAuthorization => Device.State == "unauthorized";
    public string? AuthorizationHint => NeedsAuthorization ? "解锁手机，允许此电脑进行 USB 调试。设备列表会自动更新。" : null;
    public Brush Tone => Device.State switch { "device" => Palette.Green, "unauthorized" => Palette.Amber, _ => Palette.Gray };
    public Brush Tint => Device.State switch { "device" => Palette.GreenTint, "unauthorized" => Palette.AmberTint, _ => Palette.GrayTint };
    public string Tooltip => $"{Name}\n{Status} · {Connection}\n{Serial}" + (Name == ModelName ? "" : $"\n型号 {ModelName}");
}

public static class Palette
{
    private static Brush Brush(string value) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(value)!; brush.Freeze(); return brush; }
    public static readonly Brush Green = Brush("#238C70"), Amber = Brush("#B37D20"), Gray = Brush("#9691A6"), Red = Brush("#CB6275");
    public static readonly Brush GreenTint = Brush("#EDF7F2"), AmberTint = Brush("#FCF5E4"), GrayTint = Brush("#F2F0F6");
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    internal enum DiscoveryState { Connecting, NeedsAdbSetup, Ready }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<DeviceRow> Devices { get; } = new();
    public IReadOnlyList<DeviceRow> EdgeDevices { get; private set; } = Array.Empty<DeviceRow>();
    private DeviceSnapshot snapshot = new(Array.Empty<AdbDevice>(), "正在连接 ADB…");
    private DiscoveryState discoveryState = DiscoveryState.Connecting;
    private IReadOnlyDictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly Dictionary<string, int> order = new(StringComparer.Ordinal);
    private int nextOrder;
    public DeviceRow? Selected { get; private set; }
    public string OnlineCount => snapshot.IsAvailable ? snapshot.OnlineCount.ToString() : "—";
    public string TotalCount => snapshot.IsAvailable ? "/" + snapshot.Devices.Count : "";
    public string Aggregate => discoveryState switch
    {
        DiscoveryState.Connecting => "正在连接",
        DiscoveryState.NeedsAdbSetup => "待配置 ADB",
        _ => !snapshot.IsAvailable ? "ADB 异常" : snapshot.Devices.Count == 0 ? "未连接" : snapshot.OnlineCount == snapshot.Devices.Count ? "全部在线" : "需要处理"
    };
    public string ShortStatus => discoveryState switch
    {
        DiscoveryState.Connecting => "连接中",
        DiscoveryState.NeedsAdbSetup => "待配置",
        _ => !snapshot.IsAvailable ? "异常" : snapshot.Devices.Count == 0 ? "未连接" : snapshot.OnlineCount == snapshot.Devices.Count ? "在线" : "待处理"
    };
    public Brush AggregateTone => discoveryState switch
    {
        DiscoveryState.Connecting => Palette.Gray,
        DiscoveryState.NeedsAdbSetup => Palette.Amber,
        _ => !snapshot.IsAvailable ? Palette.Red : snapshot.Devices.Count == 0 ? Palette.Gray : snapshot.OnlineCount == snapshot.Devices.Count ? Palette.Green : Palette.Amber
    };
    public bool AdbAvailable => snapshot.IsAvailable;
    public bool HasDevices => snapshot.Devices.Count > 0;
    public string EmptyTitle => discoveryState switch
    {
        DiscoveryState.Connecting => "正在连接 ADB",
        DiscoveryState.NeedsAdbSetup => "先配置 ADB",
        _ => snapshot.IsAvailable ? "等待连接 Android 设备" : "ADB 暂时不可用"
    };
    public string EmptyMessage => discoveryState switch
    {
        DiscoveryState.Connecting => "正在检查工具和设备连接。",
        DiscoveryState.NeedsAdbSetup => "下载并解压 Android SDK Platform-Tools，然后在设置中选择 adb.exe 并保存。",
        _ => snapshot.Error ?? "开启手机的 USB 调试，用支持数据传输的 USB 线连接电脑。"
    };
    public string EmptyAction => discoveryState == DiscoveryState.NeedsAdbSetup ? "配置 ADB"
        : AdbAvailable ? "查看连接步骤" : "检查 ADB 设置";
    public bool ShowEmptyAction => discoveryState != DiscoveryState.Connecting;
    public string Footer { get; private set; } = "正在读取真实设备状态…";
    public Brush FooterTone { get; private set; } = Palette.Gray;
    public string? ListenerWarning { get; private set; }
    public string Caption => AdbAvailable && ListenerWarning is not null ? "监听恢复中 · 轮询设备列表" :
        Selected is null ? "你的设备，都在这里" : "已绑定当前设备";
    public void SetListenerWarning(string? warning)
    {
        if (ListenerWarning == warning) return;
        ListenerWarning = warning;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ListenerWarning)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Caption)));
    }
    public void SetDeviceAliases(IReadOnlyDictionary<string, string> value)
    {
        aliases = new Dictionary<string, string>(value, StringComparer.Ordinal);
        Apply(snapshot, discoveryState);
    }
    public void Apply(DeviceSnapshot value) => Apply(value, DiscoveryState.Ready);

    internal void Apply(DeviceSnapshot value, DiscoveryState state)
    {
        snapshot = value;
        discoveryState = state;
        foreach (var device in value.Devices)
            if (!order.ContainsKey(device.Serial)) order[device.Serial] = nextOrder++;
        var rows = value.Devices.OrderBy(d => d.IsOnline ? 0 : d.State == "unauthorized" ? 1 : 2)
            .ThenBy(d => order[d.Serial])
            .Select(d => new DeviceRow(d, aliases.GetValueOrDefault(d.Serial)))
            .ToArray();
        var previousEdge = EdgeDevices.Select(d => d.Serial).ToArray();
        Devices.Clear();
        foreach (var row in rows) Devices.Add(row);
        EdgeDevices = previousEdge.Where(id => rows.Any(d => d.Serial == id)).Select(id => rows.First(d => d.Serial == id)).Concat(rows.Where(d => !previousEdge.Contains(d.Serial))).ToArray();
        if (Selected is not null) Selected = rows.FirstOrDefault(d => d.Serial == Selected.Serial && d.IsOnline);
        Changed();
    }
    public void Select(DeviceRow? device) { Selected = device; Changed(); }
    public void Feedback(string text, bool error = false) => Feedback(text, error ? Palette.Red : Palette.Green);
    public void Feedback(string text, Brush tone) { Footer = text; FooterTone = tone; Changed(); }
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
