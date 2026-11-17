using AdbDock.Core;

public static partial class OperationsChecks
{
    private const string PerformanceMemory = "MemTotal: 8000 kB\nMemAvailable: 3000 kB\n";
    private const string PerformanceBattery = "present: true\nlevel: 75\nscale: 100\ntemperature: 315\n";

    private static async Task PerformanceParsersAsync(FakeTool fake, string directory)
    {
        var before = PerformanceParser.ParseCpu("cpu0 999 0 0 999\ncpu 100 10 50 700 40 5 15 0 9000 99\n");
        var after = PerformanceParser.ParseCpu("  cpu 130 10 70 840 50 5 15 0 99999 500\ncpu0 1 2 3 4\n");
        Check(PerformanceParser.CpuUsagePercent(before, after) is { } usage && Math.Abs(usage - 25) < .0001,
            "CPU uses aggregate first eight counters, excludes guest counters and includes iowait as idle.");
        Check(PerformanceParser.CpuUsagePercent(null, after) is null, "CPU must warm up with two valid samples.");
        Check(PerformanceParser.CpuUsagePercent(before, before) is null, "No elapsed CPU ticks is unavailable, not zero.");
        Check(PerformanceParser.CpuUsagePercent(after, before) is null, "CPU counter reset must clear the interval.");
        Check(PerformanceParser.CpuUsagePercent(before, after with { User = 99 }) is null, "Any counter regression invalidates an interval.");
        await ExpectAsync<InvalidDataException>(() => Task.FromResult(PerformanceParser.ParseCpu("cpu0 1 2 3 4")));
        await ExpectAsync<InvalidDataException>(() => Task.FromResult(PerformanceParser.ParseCpu("cpu 1 bad 3 4")));
        var memory = PerformanceParser.ParseMemory(PerformanceMemory);
        Check(memory.TotalBytes == 8000L * 1024 && memory.UsedBytes == 5000L * 1024 && memory.UsedPercent == 62.5,
            "Memory kB converts to bytes and uses MemAvailable.");
        var fallback = PerformanceParser.ParseMemory("MemTotal: 8000 kB\nMemFree: 1000 kB\nBuffers: 200 kB\nCached: 1500 kB\nSReclaimable: 400 kB\nShmem: 100 kB\n");
        Check(fallback == memory, "Memory fallback counts reclaimable cache and subtracts shared memory.");
        await ExpectAsync<InvalidDataException>(() => Task.FromResult(PerformanceParser.ParseMemory("MemTotal: 0 kB\nMemAvailable: 0 kB")));
        await ExpectAsync<InvalidDataException>(() => Task.FromResult(PerformanceParser.ParseMemory("MemTotal: 10 kB\nMemAvailable: 20 kB")));
        await ExpectAsync<InvalidDataException>(() => Task.FromResult(PerformanceParser.ParseMemory("MemTotal: 10 kB\nMemFree: 5 kB")));
        var battery = PerformanceParser.ParseBattery(PerformanceBattery);
        Check(battery.LevelPercent == 75 && battery.TemperatureCelsius == 31.5 && battery.LevelError is null && battery.TemperatureError is null,
            "Battery level uses scale and battery temperature is in tenths of a degree.");
        var partial = PerformanceParser.ParseBattery("level: 3\nscale: 4\ntemperature: unknown\n");
        Check(partial.LevelPercent == 75 && partial.TemperatureCelsius is null && partial.TemperatureError is not null,
            "Missing battery temperature must not hide valid battery level or invent zero degrees.");
        Check(PerformanceParser.ParseBattery("level: 0\nscale: 0\ntemperature: -15").LevelPercent is null,
            "Invalid battery scale is unavailable.");
        Check(PerformanceParser.ParseBattery("level: 0\nscale: 0\ntemperature: -15").TemperatureCelsius == -1.5,
            "Negative reported battery temperatures remain valid.");
        var absent = PerformanceParser.ParseBattery("present: false\nlevel: 0\nscale: 100\ntemperature: 0");
        Check(absent.LevelPercent is null && absent.TemperatureCelsius is null, "Devices without batteries never report synthetic zero metrics.");
    }

    private static async Task PerformanceSamplingAsync(FakeTool fake, string directory)
    {
        const string serial = "performance-selected 中文";
        await using var operations = new AdbOperations(fake.Executable);
        operations.UpdateOnlineDevices([serial, "not-selected"]);
        var task = operations.ReadPerformanceAsync(serial);
        var cpu = await fake.NextAsync();
        EqualArguments(cpu, "-s", serial, "shell", "cat", "/proc/stat");
        await cpu.SendAsync(error: "proc stat permission denied", exitCode: 1);
        var memory = await fake.NextAsync();
        EqualArguments(memory, "-s", serial, "shell", "cat", "/proc/meminfo");
        await memory.SendAsync(output: PerformanceMemory, exitCode: 0);
        var battery = await fake.NextAsync();
        EqualArguments(battery, "-s", serial, "shell", "dumpsys", "battery");
        await battery.SendAsync(output: "level: 75\nscale: 100\ntemperature: unavailable\n", exitCode: 0);
        var sample = await task.WaitAsync(Deadline);
        Check(sample.Cpu is null && sample.CpuError?.Contains("permission denied", StringComparison.Ordinal) == true,
            "Denied CPU reading is unavailable with its actual reason.");
        Check(sample.Memory?.UsedPercent == 62.5 && sample.Battery?.LevelPercent == 75 && sample.Battery.TemperatureCelsius is null,
            "Partial failures preserve independent memory and battery readings.");
        Check(sample.Errors.Count == 2, "Every unavailable metric exposes a visible reason.");
        Check(fake.Count == 3, "Only three fixed read-only requests target the selected serial.");
    }

    private static async Task PerformanceTimeoutAsync(FakeTool fake, string directory)
    {
        const string serial = "performance-timeout";
        await using var operations = new AdbOperations(fake.Executable);
        operations.UpdateOnlineDevices([serial]);
        var task = operations.ReadPerformanceAsync(serial);
        var timedOut = await fake.NextAsync();
        EqualArguments(timedOut, "-s", serial, "shell", "cat", "/proc/stat");
        var memory = await fake.NextAsync();
        await timedOut.WaitForExitAsync();
        EqualArguments(memory, "-s", serial, "shell", "cat", "/proc/meminfo");
        await memory.SendAsync(output: PerformanceMemory, exitCode: 0);
        var battery = await fake.NextAsync();
        EqualArguments(battery, "-s", serial, "shell", "dumpsys", "battery");
        await battery.SendAsync(output: PerformanceBattery, exitCode: 0);
        var sample = await task.WaitAsync(Deadline);
        Check(sample.Cpu is null && sample.CpuError?.Contains("超时", StringComparison.Ordinal) == true &&
            sample.Memory is not null && sample.Battery?.TemperatureCelsius == 31.5,
            "A five-second command timeout terminates the owned client and still reads remaining metrics.");
    }

    private static async Task PerformanceQueueAsync(FakeTool fake, string directory)
    {
        const string serial = "performance-queue";
        await using var operations = new AdbOperations(fake.Executable);
        operations.UpdateOnlineDevices([serial]);
        using var cancellation = new CancellationTokenSource();
        var sample = operations.ReadPerformanceAsync(serial, cancellation.Token);
        var running = await fake.NextAsync();
        var key = operations.SendKeyAsync(serial, "Home");
        Check(!key.IsCompleted && fake.Count == 1, "Performance reads hold the existing same-device queue.");
        cancellation.Cancel();
        await ExpectAsync<OperationCanceledException>(() => sample);
        await running.WaitForExitAsync();
        var next = await fake.NextAsync();
        EqualArguments(next, "-s", serial, "shell", "input", "keyevent", "3");
        await next.SendAsync(exitCode: 0);
        await key.WaitAsync(Deadline);
        Check(fake.Count == 2, "Cancelling a partial sample never starts its remaining metric requests.");
    }

    private static async Task PerformanceMonitorAsync(FakeTool fake, string directory)
    {
        const string serial = "performance-monitor";
        await using var operations = new AdbOperations(fake.Executable);
        operations.UpdateOnlineDevices([serial]);
        var firstSample = new TaskCompletionSource<DevicePerformanceSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = operations.MonitorPerformanceAsync(serial, sample => firstSample.TrySetResult(sample));
        var cpu = await fake.NextAsync();
        EqualArguments(cpu, "-s", serial, "shell", "cat", "/proc/stat");
        await cpu.SendAsync(output: "cpu 100 10 50 700 40 5 15 0 99 99\n", exitCode: 0);
        var memory = await fake.NextAsync();
        await memory.SendAsync(output: PerformanceMemory, exitCode: 0);
        var battery = await fake.NextAsync();
        await battery.SendAsync(output: PerformanceBattery, exitCode: 0);
        await firstSample.Task.WaitAsync(Deadline);
        Check(!monitor.IsCompleted, "Monitoring waits for the next refresh after a completed sample.");
        operations.UpdateOnlineDevices([]);
        operations.UpdateOnlineDevices([serial]);
        await ExpectAsync<OperationCanceledException>(() => monitor);
        Check(fake.Count == 3, "Disconnect during the refresh delay stops the old generation without replay on reconnect.");
        using var cancellation = new CancellationTokenSource();
        var explicitMonitor = operations.MonitorPerformanceAsync(serial, _ => { }, cancellation.Token);
        var explicitClient = await fake.NextAsync();
        EqualArguments(explicitClient, "-s", serial, "shell", "cat", "/proc/stat");
        cancellation.Cancel();
        await ExpectAsync<OperationCanceledException>(() => explicitMonitor);
        await explicitClient.WaitForExitAsync();
        Check(fake.Count == 4, "Explicit restart creates one owned client, and pausing cancels it without extra queries.");
    }
}
