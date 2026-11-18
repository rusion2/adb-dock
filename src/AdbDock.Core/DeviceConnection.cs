using System.Globalization;

namespace AdbDock.Core;

internal readonly record struct DeviceTarget(string Serial, ulong? TransportId)
{
    public string[] Arguments(params string[] command) => TransportId is ulong id
        ? ["-t", id.ToString(CultureInfo.InvariantCulture), .. command]
        : ["-s", Serial, .. command];

    public static Dictionary<string, DeviceTarget> Online(DeviceSnapshot snapshot) =>
        Index(snapshot.Devices.Where(device => device.IsOnline).Select(device => new DeviceTarget(device.Serial, device.TransportId)));

    public static Dictionary<string, DeviceTarget> FromSerials(IEnumerable<string> serials)
    {
        ArgumentNullException.ThrowIfNull(serials);
        return Index(serials.Select(serial => new DeviceTarget(serial, null)));
    }

    private static Dictionary<string, DeviceTarget> Index(IEnumerable<DeviceTarget> targets)
    {
        var online = new Dictionary<string, DeviceTarget>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            if (string.IsNullOrWhiteSpace(target.Serial)) throw new ArgumentException("设备序列号不能为空。");
            online.TryAdd(target.Serial, target);
        }
        return online;
    }
}

// The online device owns one reference. An operation or mirror keeps its own
// reference until it has finished using the token, even across a reconnect.
internal sealed class DeviceConnection(DeviceTarget target)
{
    private readonly CancellationTokenSource _lifetime = new();
    private int _references = 1;

    public DeviceTarget Target { get; } = target;
    public CancellationToken Token => _lifetime.Token;

    public DeviceConnection Retain()
    {
        Interlocked.Increment(ref _references);
        return this;
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0) _lifetime.Dispose();
    }

    public void Disconnect()
    {
        try { _lifetime.Cancel(); }
        finally { Release(); }
    }
}
