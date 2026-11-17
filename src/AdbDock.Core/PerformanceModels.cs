using System.Globalization;

namespace AdbDock.Core;

public sealed record CpuTimes(ulong User, ulong Nice, ulong System, ulong Idle, ulong IoWait, ulong Irq, ulong SoftIrq, ulong Steal)
{
    internal ulong[] Counters => [User, Nice, System, Idle, IoWait, Irq, SoftIrq, Steal];
}

public sealed record MemoryUsage(long TotalBytes, long AvailableBytes)
{
    public long UsedBytes => TotalBytes - AvailableBytes;
    public double UsedPercent => 100d * UsedBytes / TotalBytes;
}

public sealed record BatteryStatus(double? LevelPercent, double? TemperatureCelsius, string? LevelError, string? TemperatureError);

public sealed record DevicePerformanceSample(DateTimeOffset CollectedAt, CpuTimes? Cpu, MemoryUsage? Memory,
    BatteryStatus? Battery, string? CpuError, string? MemoryError, string? BatteryError)
{
    public IReadOnlyList<string> Errors => new[]
    {
        CpuError is null ? null : "CPU：" + CpuError,
        MemoryError is null ? null : "内存：" + MemoryError,
        BatteryError is null ? null : "电池：" + BatteryError,
        Battery?.LevelError is null ? null : "电量：" + Battery.LevelError,
        Battery?.TemperatureError is null ? null : "电池温度：" + Battery.TemperatureError
    }.OfType<string>().ToArray();
}

public static class PerformanceParser
{
    public static CpuTimes ParseCpu(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var line in text.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0 || fields[0] != "cpu") continue;
            if (fields.Length < 5) break;
            var counters = new ulong[8];
            for (var index = 0; index < Math.Min(8, fields.Length - 1); index++)
                if (!ulong.TryParse(fields[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out counters[index]))
                    throw new InvalidDataException("/proc/stat 的 CPU 计数格式无效。");
            return new(counters[0], counters[1], counters[2], counters[3], counters[4], counters[5], counters[6], counters[7]);
        }
        throw new InvalidDataException("未读取到 /proc/stat 的整机 CPU 计数，设备可能限制了访问。");
    }

    public static double? CpuUsagePercent(CpuTimes? previous, CpuTimes? current)
    {
        if (previous is null || current is null) return null;
        var before = previous.Counters;
        var after = current.Counters;
        decimal total = 0;
        for (var index = 0; index < before.Length; index++)
        {
            if (after[index] < before[index]) return null;
            total += after[index] - before[index];
        }
        if (total == 0) return null;
        var idle = (decimal)(current.Idle - previous.Idle) + (current.IoWait - previous.IoWait);
        return (double)((total - idle) * 100 / total);
    }

    public static MemoryUsage ParseMemory(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || fields[2] != "kB" || !fields[0].EndsWith(':')) continue;
            if (long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                values[fields[0][..^1]] = value;
        }
        if (!values.TryGetValue("MemTotal", out var total) || total <= 0)
            throw new InvalidDataException("未读取到有效的 MemTotal。");
        if (!values.TryGetValue("MemAvailable", out var available))
        {
            if (!values.TryGetValue("MemFree", out var free) || !values.TryGetValue("Buffers", out var buffers) ||
                !values.TryGetValue("Cached", out var cached))
                throw new InvalidDataException("缺少 MemAvailable，且无法根据空闲内存与缓存估算可用内存。");
            try { available = checked(free + buffers + cached + values.GetValueOrDefault("SReclaimable") - values.GetValueOrDefault("Shmem")); }
            catch (OverflowException ex) { throw new InvalidDataException("内存计数超出可用范围。", ex); }
        }
        if (available < 0 || available > total) throw new InvalidDataException("内存可用量不在总内存范围内。");
        try { return new(checked(total * 1024), checked(available * 1024)); }
        catch (OverflowException ex) { throw new InvalidDataException("内存计数超出可用范围。", ex); }
    }

    public static BatteryStatus ParseBattery(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator > 0) values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        if (values.GetValueOrDefault("present")?.Equals("false", StringComparison.OrdinalIgnoreCase) == true)
            return new(null, null, "设备未报告电池。", "设备未报告电池。");
        double? levelPercent = null;
        if (long.TryParse(values.GetValueOrDefault("level"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) &&
            long.TryParse(values.GetValueOrDefault("scale"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var scale) &&
            scale > 0 && level >= 0 && level <= scale) levelPercent = 100d * level / scale;
        double? temperature = null;
        if (int.TryParse(values.GetValueOrDefault("temperature"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tenths))
            temperature = tenths / 10d;
        return new(levelPercent, temperature, levelPercent is null ? "缺少有效的电量 level/scale。" : null,
            temperature is null ? "设备未报告有效的电池温度。" : null);
    }
}
