namespace AdbDock.Core;

public sealed partial class AdbOperations
{
    public Task<DevicePerformanceSample> ReadPerformanceAsync(string serial, CancellationToken cancellationToken = default) =>
        WithDeviceAsync(serial, async token =>
        {
            var cpu = await ReadMetricAsync(["shell", "cat", "/proc/stat"], PerformanceParser.ParseCpu, token).ConfigureAwait(false);
            var memory = await ReadMetricAsync(["shell", "cat", "/proc/meminfo"], PerformanceParser.ParseMemory, token).ConfigureAwait(false);
            var battery = await ReadMetricAsync(["shell", "dumpsys", "battery"], PerformanceParser.ParseBattery, token).ConfigureAwait(false);
            return new DevicePerformanceSample(DateTimeOffset.Now, cpu.Value, memory.Value, battery.Value, cpu.Error, memory.Error, battery.Error);

            async Task<(T? Value, string? Error)> ReadMetricAsync<T>(string[] arguments, Func<string, T> parse, CancellationToken operation) where T : class
            {
                try
                {
                    var result = await ProcessRunner.RunAsync(_adbPath, new[] { "-s", serial }.Concat(arguments),
                        TimeSpan.FromSeconds(5), operation).ConfigureAwait(false);
                    result.EnsureSuccess();
                    operation.ThrowIfCancellationRequested();
                    return (parse(result.Output), null);
                }
                catch (OperationCanceledException) when (operation.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    operation.ThrowIfCancellationRequested();
                    return (null, exception.Message);
                }
            }
        }, queued: true, cancellationToken);

    public Task MonitorPerformanceAsync(string serial, Action<DevicePerformanceSample> onSample, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onSample);
        // The outer scope pins the device generation even between samples. Reconnecting the same
        // serial cannot revive this monitor; starting again always requires an explicit user action.
        return WithDeviceAsync<bool>(serial, async token =>
        {
            while (true)
            {
                var sample = await ReadPerformanceAsync(serial, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                onSample(sample);
                await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            }
        }, queued: false, cancellationToken);
    }
}
