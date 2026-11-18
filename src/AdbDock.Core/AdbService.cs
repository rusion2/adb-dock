using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace AdbDock.Core;

public sealed class AdbService : IAsyncDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private readonly object _stateLock = new();
    private readonly object _notifications = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Channel<byte> _refreshRequests = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private Task? _backgroundTask;
    private Task? _disposeTask;
    private DeviceSnapshot? _lastSnapshot;
    private bool _started;
    private bool _disposed;

    public string ExecutablePath { get; }
    public event Action<DeviceSnapshot>? SnapshotChanged;
    public event Action<string?>? ListenerWarningChanged;

    public AdbService(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ExecutablePath = executablePath;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        using var operation = CreateOperation(cancellationToken);
        await _startGate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            if (_started) return;
            try
            {
                var version = await RunAdbAsync(["version"], operation.Token).ConfigureAwait(false);
                version.EnsureSuccess();
                await RefreshSerializedAsync(operation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                operation.Token.ThrowIfCancellationRequested();
                PublishFailure(exception.Message);
            }

            operation.Token.ThrowIfCancellationRequested();
            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _started = true;
                _backgroundTask = Task.Run(() => RunBackgroundAsync(_lifetime.Token));
            }
        }
        finally { _startGate.Release(); }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        using var operation = CreateOperation(cancellationToken);
        await RefreshSerializedAsync(operation.Token).ConfigureAwait(false);
    }

    private async Task RefreshSerializedAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var result = await RunAdbAsync(["devices", "-l"], cancellationToken).ConfigureAwait(false);
                result.EnsureSuccess();
                cancellationToken.ThrowIfCancellationRequested();
                Publish(new DeviceSnapshot(ParseDevices(result.Output)));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PublishFailure(exception.Message);
            }
        }
        finally { _refreshGate.Release(); }
    }

    // Process creation and all callbacks stay off the UI thread, even for an uncontended explicit refresh.
    private Task<ProcessResult> RunAdbAsync(string[] arguments, CancellationToken cancellationToken) =>
        Task.Run(() => ProcessRunner.RunAsync(ExecutablePath, arguments, CommandTimeout, cancellationToken), cancellationToken);

    private CancellationTokenSource CreateOperation(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        }
    }

    private async Task RunBackgroundAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(TrackLoopAsync(cancellationToken), PollLoopAsync(cancellationToken),
                RefreshLoopAsync(cancellationToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            _refreshRequests.Writer.TryWrite(0);
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        while (await _refreshRequests.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Coalesce an entire track-devices output block, including a zero-device handshake.
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            while (_refreshRequests.Reader.TryRead(out _)) { }
            await RefreshSerializedAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TrackLoopAsync(CancellationToken cancellationToken)
    {
        var retry = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            Process? process = null;
            using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task output = Task.CompletedTask;
            Task<string> error = Task.FromResult("");
            string failure;
            try
            {
                process = ProcessRunner.Start(ExecutablePath, ["track-devices"]);
                output = ReadNotificationsAsync(process.StandardOutput, session.Token);
                error = ReadErrorTailAsync(process.StandardError, session.Token);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                _refreshRequests.Writer.TryWrite(0);
                // A descendant retaining inherited pipe handles must not prevent listener recovery.
                try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false); }
                catch (TimeoutException) { session.Cancel(); }
                failure = error.IsCompletedSuccessfully ? error.Result.Trim() : "";
                failure = string.IsNullOrWhiteSpace(failure)
                    ? $"ADB 设备监听已退出（{process.ExitCode}），正在恢复。"
                    : $"ADB 设备监听已中断，正在恢复：{failure}";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                _refreshRequests.Writer.TryWrite(0);
                failure = $"ADB 设备监听不可用，正在恢复：{exception.Message}";
            }
            finally
            {
                session.Cancel();
                if (process is not null)
                {
                    await ProcessRunner.StopClientAsync(process).ConfigureAwait(false);
                    await ProcessRunner.ObserveAsync(output, error).ConfigureAwait(false);
                    process.Dispose();
                }
            }

            if (cancellationToken.IsCancellationRequested) break;
            Notify(ListenerWarningChanged, failure);
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(10)) retry = TimeSpan.FromSeconds(1);
            await Task.Delay(retry, cancellationToken).ConfigureAwait(false);
            retry = TimeSpan.FromSeconds(Math.Min(retry.TotalSeconds * 2, 8));
        }
    }

    private async Task ReadNotificationsAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var firstBlock = true;
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) != 0)
        {
            // CLI versions differ in framing (hex length, blank lines, or headers). Any stdout block
            // invalidates the list; only devices -l is ever used as the authoritative snapshot.
            _refreshRequests.Writer.TryWrite(0);
            if (firstBlock)
            {
                firstBlock = false;
                Notify(ListenerWarningChanged, null);
            }
        }
    }

    private static async Task<string> ReadErrorTailAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var tail = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > 4096) tail.Remove(0, tail.Length - 4096);
        }
        return tail.ToString();
    }

    private void PublishFailure(string message) => Publish(new DeviceSnapshot(Array.Empty<AdbDevice>(),
        string.IsNullOrWhiteSpace(message) ? "ADB 不可用，请检查程序路径或本地服务。" : message));

    private void Publish(DeviceSnapshot snapshot)
    {
        lock (_notifications)
        {
            Action<DeviceSnapshot>? handlers;
            lock (_stateLock)
            {
                if (_disposed) return;
                if (_lastSnapshot is not null && _lastSnapshot.Error == snapshot.Error &&
                    _lastSnapshot.Devices.SequenceEqual(snapshot.Devices)) return;
                _lastSnapshot = snapshot;
                handlers = SnapshotChanged;
            }
            Notify(handlers, snapshot);
        }
    }

    private static void Notify<T>(Action<T>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try { handler(value); }
            catch { /* A view subscriber must not stop discovery or turn it into an ADB error. */ }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                _disposeTask = Task.Run(DisposeCoreAsync);
            }
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        _lifetime.Cancel();
        await _startGate.WaitAsync().ConfigureAwait(false);
        Task? background;
        try { lock (_stateLock) background = _backgroundTask; }
        finally { _startGate.Release(); }
        if (background is not null) await background.ConfigureAwait(false);
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        _refreshGate.Release();
        _lifetime.Dispose();
        // The managed-only gates are deliberately left undisposed: already-cancelled callers may
        // still be unwinding a queued WaitAsync and must never release a disposed semaphore.
    }

    public static IReadOnlyList<AdbDevice> ParseDevices(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var devices = new List<AdbDevice>();
        var serials = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim().TrimStart((char)0xFEFF);
            if (line.Length == 0 || line.StartsWith('*') ||
                line.StartsWith("List of devices attached", StringComparison.OrdinalIgnoreCase)) continue;
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || fields[0].EndsWith(':')) continue;
            var state = fields[1];
            var metadataStart = 2;
            if (state == "no" && fields.Length >= 3 && fields[2] == "permissions")
            {
                state = "no permissions";
                metadataStart = fields.Length;
            }
            else
            {
                if (!state.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_')) continue;
                if (fields.Skip(metadataStart).Any(field => !field.Contains(':'))) continue;
            }
            if (!serials.Add(fields[0])) continue;
            var model = fields.Skip(2).FirstOrDefault(field => field.StartsWith("model:", StringComparison.Ordinal));
            var name = model is { Length: > 6 } ? model[6..].Replace('_', ' ') : fields[0];
            var connection = IsEmulator(fields[0]) ? "模拟器" :
                fields.Skip(2).Any(field => field.StartsWith("usb:", StringComparison.Ordinal) && field.Length > 4) ? "USB" :
                IsNetworkSerial(fields[0]) ? "网络" : "未知";
            devices.Add(new AdbDevice(fields[0], name, state, connection));
        }
        return devices.AsReadOnly();
    }

    private static bool IsEmulator(string serial) => serial.StartsWith("emulator-", StringComparison.Ordinal) &&
        ushort.TryParse(serial.AsSpan(9), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port != 0;

    private static bool IsNetworkSerial(string serial)
    {
        var colon = serial.LastIndexOf(':');
        if (colon <= 0 || !ushort.TryParse(serial.AsSpan(colon + 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out var port) || port == 0) return false;
        var host = serial[..colon];
        if (host.StartsWith('[') && host.EndsWith(']'))
            return IPAddress.TryParse(host[1..^1], out var ipv6) && ipv6.AddressFamily == AddressFamily.InterNetworkV6;
        var octets = host.Split('.');
        return octets.Length == 4 && octets.All(octet => byte.TryParse(octet, NumberStyles.None,
            CultureInfo.InvariantCulture, out _)) &&
            IPAddress.TryParse(host, out var ipv4) && ipv4.AddressFamily == AddressFamily.InterNetwork;
    }
}
