using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Media;
using AdbDock.Core;

namespace AdbDock.App;

public sealed class DeviceRow
{
    public AdbDevice Device { get; }
    public DeviceRow(AdbDevice device) => Device = device;
    public string Serial => Device.Serial;
    public string Name => Device.Name;
    public string Connection => Device.Connection;
    public string Status => Device.StatusLabel;
    public string Marker => Device.Marker;
    public bool IsOnline => Device.IsOnline;
    public Brush Tone => Device.State switch { "device" => Palette.Green, "unauthorized" => Palette.Amber, _ => Palette.Gray };
    public Brush Tint => Device.State switch { "device" => Palette.GreenTint, "unauthorized" => Palette.AmberTint, _ => Palette.GrayTint };
    public string Tooltip => $"{Name}\n{Status} · {Connection}\n{Serial}";
}

public static class Palette
{
    private static Brush Brush(string value) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(value)!; brush.Freeze(); return brush; }
    public static readonly Brush Green = Brush("#238C70"), Amber = Brush("#B37D20"), Gray = Brush("#9691A6"), Red = Brush("#CB6275");
    public static readonly Brush GreenTint = Brush("#EDF7F2"), AmberTint = Brush("#FCF5E4"), GrayTint = Brush("#F2F0F6");
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<DeviceRow> Devices { get; } = new();
    public IReadOnlyList<DeviceRow> EdgeDevices { get; private set; } = Array.Empty<DeviceRow>();
    private DeviceSnapshot snapshot = new(Array.Empty<AdbDevice>(), "正在连接 ADB…");
    private readonly Dictionary<string, int> order = new(StringComparer.Ordinal);
    private int nextOrder;
    public DeviceRow? Selected { get; private set; }
    public string OnlineCount => snapshot.IsAvailable ? snapshot.OnlineCount.ToString() : "—";
    public string TotalCount => snapshot.IsAvailable ? "/" + snapshot.Devices.Count : "";
    public string Aggregate => !snapshot.IsAvailable ? "ADB 异常" : snapshot.Devices.Count == 0 ? "未连接" : snapshot.OnlineCount == snapshot.Devices.Count ? "全部在线" : "需要处理";
    public string ShortStatus => !snapshot.IsAvailable ? "异常" : snapshot.Devices.Count == 0 ? "未连接" : snapshot.OnlineCount == snapshot.Devices.Count ? "在线" : "待处理";
    public Brush AggregateTone => !snapshot.IsAvailable ? Palette.Red : snapshot.Devices.Count == 0 ? Palette.Gray : snapshot.OnlineCount == snapshot.Devices.Count ? Palette.Green : Palette.Amber;
    public bool AdbAvailable => snapshot.IsAvailable;
    public bool HasDevices => snapshot.Devices.Count > 0;
    public string EmptyTitle => snapshot.IsAvailable ? "等待一位新朋友" : "ADB 暂时不可用";
    public string EmptyMessage => snapshot.Error ?? "连接 Android 设备并开启 USB 调试，\n就会在这里看到它。";
    public string Footer { get; private set; } = "正在读取真实设备状态…";
    public Brush FooterTone { get; private set; } = Palette.Gray;
    public string Caption => Selected is null ? "你的设备，都在这里" : "已绑定当前设备";
    public void Apply(DeviceSnapshot value)
    {
        snapshot = value;
        foreach (var device in value.Devices)
            if (!order.ContainsKey(device.Serial)) order[device.Serial] = nextOrder++;
        var rows = value.Devices.OrderBy(d => d.IsOnline ? 0 : d.State == "unauthorized" ? 1 : 2).ThenBy(d => order[d.Serial]).Select(d => new DeviceRow(d)).ToArray();
        var previousEdge = EdgeDevices.Select(d => d.Serial).ToArray();
        Devices.Clear();
        foreach (var row in rows) Devices.Add(row);
        EdgeDevices = previousEdge.Where(id => rows.Any(d => d.Serial == id)).Select(id => rows.First(d => d.Serial == id)).Concat(rows.Where(d => !previousEdge.Contains(d.Serial))).ToArray();
        if (Selected is not null) Selected = rows.FirstOrDefault(d => d.Serial == Selected.Serial && d.IsOnline);
        Changed();
    }
    public void Select(DeviceRow? device) { Selected = device; Changed(); }
    public void Feedback(string text, bool error = false) { Footer = text; FooterTone = error ? Palette.Red : Palette.Green; Changed(); }
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
