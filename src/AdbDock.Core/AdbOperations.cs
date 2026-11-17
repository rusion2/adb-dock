using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AdbDock.Core;

public sealed partial class AdbOperations : IAsyncDisposable
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(20);
    private readonly string _adbPath;
    private readonly object _sync = new();
    private readonly Dictionary<string, DeviceQueue> _devices = new(StringComparer.Ordinal);
    private readonly List<CancellationTokenSource> _generations = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeOperations;
    private bool _disposed;
    private Task? _disposeTask;

    public AdbOperations(string adbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adbPath);
        _adbPath = adbPath;
    }

    public Task ScreenshotAsync(string serial, string destination, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        return RunShortAsync(serial, token => ProcessRunner.RunBinaryToFileAsync(_adbPath,
            ["-s", serial, "exec-out", "screencap", "-p"], destination, ShortTimeout, token), cancellationToken);
    }

    public Task InstallAsync(string serial, string apkPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apkPath);
        var fullPath = Path.GetFullPath(apkPath);
        if (!string.Equals(Path.GetExtension(fullPath), ".apk", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择 APK 文件。", nameof(apkPath));
        if (!File.Exists(fullPath)) throw new FileNotFoundException("找不到 APK 文件。", fullPath);

        return RunShortAsync(serial, async token =>
        {
            var result = await ProcessRunner.RunAsync(_adbPath, ["-s", serial, "install", "-r", fullPath],
                TimeSpan.FromMinutes(2), token).ConfigureAwait(false);
            result.EnsureSuccess();
            var text = string.Concat(result.Output, "\n", result.Error);
            var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Any(line => line.Contains("Failure", StringComparison.OrdinalIgnoreCase)) ||
                !lines.Any(line => string.Equals(line, "Success", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(text)
                    ? "安装未返回 Success，无法确认安装成功。"
                    : $"安装未成功：{text.Trim()}");
        }, cancellationToken);
    }

    public Task SendKeyAsync(string serial, string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var keyCode = key.ToUpperInvariant() switch
        {
            "HOME" => "3",
            "BACK" => "4",
            "RECENT" => "187",
            "WAKE" => "224",
            _ => throw new ArgumentException("仅支持 Home、Back、Recent、Wake。", nameof(key))
        };
        return RunShortAsync(serial, async token =>
        {
            // All remote shell arguments are fixed tokens, never user-provided shell fragments.
            var result = await ProcessRunner.RunAsync(_adbPath,
                ["-s", serial, "shell", "input", "keyevent", keyCode], ShortTimeout, token).ConfigureAwait(false);
            result.EnsureSuccess();
        }, cancellationToken);
    }

    public Task<string> ReadInfoAsync(string serial, CancellationToken cancellationToken = default) =>
        WithDeviceAsync(serial, async token =>
        {
            (string Label, string Property)[] properties =
            [
                ("型号", "ro.product.model"),
                ("厂家", "ro.product.manufacturer"),
                ("Android 版本", "ro.build.version.release"),
                ("SDK", "ro.build.version.sdk")
            ];
            var info = new StringBuilder().Append("序列号：").AppendLine(serial);
            foreach (var (label, property) in properties)
            {
                var result = await ProcessRunner.RunAsync(_adbPath,
                    ["-s", serial, "shell", "getprop", property], ShortTimeout, token).ConfigureAwait(false);
                result.EnsureSuccess();
                info.Append(label).Append('：').AppendLine(string.IsNullOrWhiteSpace(result.Output) ? "未知" : result.Output.Trim());
            }
            return info.ToString().TrimEnd();
        }, queued: true, cancellationToken);

    public void UpdateOnlineDevices(IEnumerable<string> serials)
    {
        ArgumentNullException.ThrowIfNull(serials);
        var online = serials.ToHashSet(StringComparer.Ordinal);
        if (online.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("设备序列号不能为空。", nameof(serials));
        List<CancellationTokenSource> disconnected = [];
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var (serial, device) in _devices)
            {
                if (device.Generation is not null && !online.Contains(serial))
                {
                    disconnected.Add(device.Generation);
                    device.Generation = null;
                }
            }
            foreach (var serial in online)
            {
                if (!_devices.TryGetValue(serial, out var device))
                    _devices.Add(serial, device = new DeviceQueue());
                if (device.Generation is null)
                {
                    device.Generation = new CancellationTokenSource();
                    _generations.Add(device.Generation);
                }
            }
        }
        foreach (var source in disconnected) CancelSafely(source);
    }

    public Task ReadLogcatAsync(string serial, Action<string> onLine, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onLine);
        // Logcat has its own process and cancellation scope; it never holds the short-command semaphore.
        return WithDeviceAsync<bool>(serial, async token =>
        {
            using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var readers = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
            session.Token.ThrowIfCancellationRequested();
            using var process = ProcessRunner.Start(_adbPath, ["-s", serial, "logcat", "-v", "time"]);
            var errors = new StringBuilder();
            var outputTask = PumpAsync(process.StandardOutput, false);
            var errorTask = PumpAsync(process.StandardError, true);
            try
            {
                await process.WaitForExitAsync(session.Token).ConfigureAwait(false);
                // Only the owned client defines this session's lifetime. A descendant retaining
                // inherited pipes must not keep Logcat alive after that client exits.
                try { await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(1), session.Token).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    CancelSafely(readers);
                    await ProcessRunner.ObserveAsync(outputTask, errorTask).ConfigureAwait(false);
                }
                session.Token.ThrowIfCancellationRequested();
                var reason = errors.ToString().Trim();
                throw new InvalidOperationException($"Logcat 已意外结束（退出码 {process.ExitCode}）。" +
                    (reason.Length == 0 ? "" : $"\n{reason}"));
            }
            finally
            {
                // Stop only our adb client, not a shared adb server or other clients.
                CancelSafely(readers);
                CancelSafely(session);
                await ProcessRunner.StopClientAsync(process).ConfigureAwait(false);
                await ProcessRunner.ObserveAsync(outputTask, errorTask).ConfigureAwait(false);
            }

            async Task PumpAsync(StreamReader reader, bool isError)
            {
                try
                {
                    while (await reader.ReadLineAsync(readers.Token).ConfigureAwait(false) is { } line)
                    {
                        if (isError)
                        {
                            errors.AppendLine(line);
                            if (errors.Length > 8192) errors.Remove(0, errors.Length - 8192);
                        }
                        onLine(isError ? $"[stderr] {line}" : line);
                    }
                }
                catch
                {
                    TryKillClient(process);
                    throw;
                }
            }
        }, queued: false, cancellationToken);
    }

    private Task RunShortAsync(string serial, Func<CancellationToken, Task> action, CancellationToken cancellationToken) =>
        WithDeviceAsync(serial, async token =>
        {
            await action(token).ConfigureAwait(false);
            return true;
        }, queued: true, cancellationToken);

    private async Task<T> WithDeviceAsync<T>(string serial, Func<CancellationToken, Task<T>> action,
        bool queued, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        DeviceQueue device;
        CancellationToken generation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_devices.TryGetValue(serial, out device!) || device.Generation is null)
                throw new InvalidOperationException("设备不在线，请等待设备连接并授权后再操作。");
            generation = device.Generation.Token;
            _activeOperations++;
        }
        var acquired = false;
        try
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generation, _shutdown.Token);
            if (queued)
            {
                await device.Gate.WaitAsync(operation.Token).ConfigureAwait(false);
                acquired = true;
            }
            operation.Token.ThrowIfCancellationRequested();
            var result = await action(operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            if (acquired) device.Gate.Release();
            lock (_sync)
            {
                if (--_activeOperations == 0 && _disposed) _drained.TrySetResult();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_sync)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _disposed = true;
            if (_activeOperations == 0) _drained.TrySetResult();
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }
        _ = DisposeCoreAsync(completion);
        return new ValueTask(completion.Task);
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            CancelSafely(_shutdown);
            await _drained.Task.ConfigureAwait(false);
            foreach (var source in _generations) source.Dispose();
            foreach (var device in _devices.Values) device.Gate.Dispose();
            _shutdown.Dispose();
            completion.TrySetResult();
        }
        catch (Exception exception) { completion.TrySetException(exception); }
    }

    private static void CancelSafely(CancellationTokenSource source)
    {
        try { source.Cancel(); }
        catch (ObjectDisposedException) { /* A concurrent DisposeAsync has already drained all users. */ }
    }

    private static void TryKillClient(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: false); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private sealed class DeviceQueue
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public CancellationTokenSource? Generation { get; set; }
    }
}
