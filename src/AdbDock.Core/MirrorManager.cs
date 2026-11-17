using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AdbDock.Core;

public sealed class MirrorManager : IAsyncDisposable
{
    private readonly string _scrcpyPath;
    private readonly string _adbPath;
    private readonly object _sync = new();
    private readonly object _notifications = new();
    private readonly Dictionary<string, DeviceSessions> _devices = new(StringComparer.Ordinal);
    private readonly HashSet<Session> _sessions = [];
    private readonly List<CancellationTokenSource> _generations = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _startsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeStarts;
    private bool _disposed;
    private Task? _disposeTask;

    public MirrorManager(string scrcpyPath, string adbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scrcpyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(adbPath);
        _scrcpyPath = scrcpyPath;
        _adbPath = adbPath;
    }

    public event Action<string, string>? StatusChanged;

    public async Task StartOrShowAsync(string serial, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        cancellationToken.ThrowIfCancellationRequested();
        DeviceSessions device;
        CancellationTokenSource generation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_devices.TryGetValue(serial, out device!) || device.Generation is null)
                throw new InvalidOperationException("设备不在线，无法启动投屏。");
            generation = device.Generation;
            device.ActiveStarts++;
            _activeStarts++;
        }

        var acquired = false;
        Session? created = null;
        try
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generation.Token, _shutdown.Token);
            await device.Gate.WaitAsync(request.Token).ConfigureAwait(false);
            acquired = true;
            request.Token.ThrowIfCancellationRequested();
            Session? existing;
            lock (_sync) existing = device.Current;
            if (existing is not null)
            {
                if (existing.Generation == generation && IsRunning(existing.Process))
                {
                    await ShowWhenReadyAsync(existing, request.Token).ConfigureAwait(false);
                    return;
                }
                // An old generation must fully stop before a new explicit request may launch a process.
                await existing.Finished.WaitAsync(request.Token).ConfigureAwait(false);
            }

            request.Token.ThrowIfCancellationRequested();
            Notify(serial, "投屏启动中…", () => device.Generation == generation && !generation.IsCancellationRequested);
            var process = ProcessRunner.Start(_scrcpyPath,
                ["-s", serial, "--window-title", $"ADB Dock · {serial}"],
                new Dictionary<string, string> { ["ADB"] = _adbPath });
            created = new Session(serial, device, generation, process, _shutdown.Token);
            lock (_sync)
            {
                device.Current = created;
                _sessions.Add(created);
            }
            created.Finished = MonitorSessionAsync(created);
            request.Token.ThrowIfCancellationRequested();
            await ShowWhenReadyAsync(created, request.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (created is not null)
            {
                created.Stop("投屏启动已取消。");
                await created.Finished.ConfigureAwait(false);
            }
            throw;
        }
        catch (Exception exception)
        {
            if (created is not null)
            {
                created.Stop(exception.Message);
                await created.Finished.ConfigureAwait(false);
            }
            Notify(serial, $"投屏启动失败：{exception.Message}",
                () => device.Generation == generation && !generation.IsCancellationRequested);
            throw;
        }
        finally
        {
            if (acquired) device.Gate.Release();
            lock (_sync)
            {
                device.ActiveStarts--;
                if (--_activeStarts == 0 && _disposed) _startsDrained.TrySetResult();
            }
        }
    }

    public void UpdateOnlineDevices(IEnumerable<string> serials)
    {
        ArgumentNullException.ThrowIfNull(serials);
        var online = serials.ToHashSet(StringComparer.Ordinal);
        if (online.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("设备序列号不能为空。", nameof(serials));
        List<CancellationTokenSource> disconnected = [];
        List<(string Serial, DeviceSessions Device)> pendingStarts = [];
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var (serial, device) in _devices)
            {
                if (device.Generation is null || online.Contains(serial)) continue;
                disconnected.Add(device.Generation);
                device.Generation = null;
                if (device.Current is { } current)
                    current.SetStopReason("设备已断开，投屏会话已停止。");
                else if (device.ActiveStarts > 0)
                    pendingStarts.Add((serial, device));
            }
            foreach (var serial in online)
            {
                if (!_devices.TryGetValue(serial, out var device))
                    _devices.Add(serial, device = new DeviceSessions());
                if (device.Generation is null)
                {
                    device.Generation = new CancellationTokenSource();
                    _generations.Add(device.Generation);
                }
            }
        }
        foreach (var source in disconnected) CancelSafely(source);
        foreach (var (serial, device) in pendingStarts)
            Notify(serial, "设备已断开，投屏启动已取消。", () => device.Generation is null);
    }

    private async Task ShowWhenReadyAsync(Session session, CancellationToken cancellationToken)
    {
        bool ready;
        try
        {
            ready = await session.Ready.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            var output = session.GetOutput();
            throw new TimeoutException("投屏启动超时，未检测到窗口或 scrcpy 渲染器。" +
                (output.Length == 0 ? "" : $"\n{output}"), exception);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!ready || !IsRunning(session.Process))
            throw new InvalidOperationException(session.Failure ?? "scrcpy 已退出，未能打开投屏窗口。");

        var status = "投屏运行中（已检测到渲染器，尚未定位到可激活窗口）。";
        if (TryGetWindow(session.Process, out var window))
        {
            // Foreground activation is best effort: Windows can reject focus-stealing requests.
            ShowWindowAsync(window, 9); // SW_RESTORE
            status = SetForegroundWindow(window)
                ? "投屏运行中。"
                : "投屏运行中（窗口已存在，Windows 未允许切换到前台）。";
        }
        NotifySession(session, status, requireOnline: true);
    }

    private async Task MonitorSessionAsync(Session session)
    {
        var token = session.Cancellation.Token;
        using var readers = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var stopRegistration = token.Register(() => TryKillClient(session.Process));
        var output = ReadOutputAsync(session.Process.StandardOutput, session, readers.Token);
        var error = ReadOutputAsync(session.Process.StandardError, session, readers.Token);
        var wasReady = false;
        try
        {
            var exit = session.Process.WaitForExitAsync(token);
            while (!exit.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
                if (!wasReady && (TryGetWindow(session.Process, out _) || session.RendererDetected))
                {
                    wasReady = true;
                    session.Ready.TrySetResult(true);
                    NotifySession(session, "投屏运行中。", requireOnline: true);
                }
                await Task.WhenAny(exit, Task.Delay(100, token)).ConfigureAwait(false);
            }
            await exit.ConfigureAwait(false);
            // An exited client may leave a descendant holding its redirected handles. Keep the
            // final output, but do not let missing EOF block session cleanup or the next start.
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(1), token).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                CancelSafely(readers);
                await ProcessRunner.ObserveAsync(output, error).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            var exitCode = session.Process.ExitCode;
            var reason = session.GetOutput();
            session.Failure = exitCode == 0 && wasReady
                ? "投屏已结束。"
                : $"scrcpy 已退出（退出码 {exitCode}）" + (wasReady ? "。" : "，未能打开投屏窗口。") +
                    (reason.Length == 0 ? "" : $"\n{reason}");
            session.Ready.TrySetResult(false);
            NotifySession(session, session.Failure);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            session.Failure = session.StopReason ?? "投屏会话已停止。";
            session.Ready.TrySetResult(false);
            NotifySession(session, session.Failure);
        }
        catch (Exception exception)
        {
            var reason = session.GetOutput();
            session.Failure = $"投屏失败：{exception.Message}" + (reason.Length == 0 ? "" : $"\n{reason}");
            session.Ready.TrySetResult(false);
            NotifySession(session, session.Failure);
        }
        finally
        {
            CancelSafely(readers);
            CancelSafely(session.Cancellation);
            await ProcessRunner.StopClientAsync(session.Process).ConfigureAwait(false);
            await ProcessRunner.ObserveAsync(output, error).ConfigureAwait(false);
            session.Ready.TrySetResult(false);
            stopRegistration.Dispose();
            session.Process.Dispose();
            session.Cancellation.Dispose();
            lock (_sync)
            {
                if (session.Device.Current == session) session.Device.Current = null;
                _sessions.Remove(session);
            }
        }
    }

    private static async Task ReadOutputAsync(StreamReader reader, Session session, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                session.AppendOutput(line);
        }
        catch
        {
            TryKillClient(session.Process);
            throw;
        }
    }

    private void NotifySession(Session session, string status, bool requireOnline = false) =>
        Notify(session.Serial, status, () => session.Device.Current == session &&
            (!requireOnline || (session.Device.Generation == session.Generation && !session.Cancellation.IsCancellationRequested)));

    private void Notify(string serial, string status, Func<bool> isCurrent)
    {
        // Serialize notifications without holding the state lock while calling UI subscribers.
        lock (_notifications)
        {
            Action<string, string>? handlers;
            lock (_sync)
            {
                if (_disposed || !isCurrent()) return;
                handlers = StatusChanged;
            }
            if (handlers is null) return;
            foreach (Action<string, string> handler in handlers.GetInvocationList())
            {
                try { handler(serial, status); }
                catch { /* An observer must not prevent process cleanup or notifications to other observers. */ }
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
            if (_activeStarts == 0) _startsDrained.TrySetResult();
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
            await _startsDrained.Task.ConfigureAwait(false);
            Task[] sessions;
            lock (_sync) sessions = _sessions.Select(session => session.Finished).ToArray();
            await Task.WhenAll(sessions).ConfigureAwait(false);
            foreach (var source in _generations) source.Dispose();
            foreach (var device in _devices.Values) device.Gate.Dispose();
            _shutdown.Dispose();
            completion.TrySetResult();
        }
        catch (Exception exception) { completion.TrySetException(exception); }
    }

    private static bool IsRunning(Process process)
    {
        try { return !process.HasExited; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return false; }
    }

    private static bool TryGetWindow(Process process, out nint window)
    {
        window = 0;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            process.Refresh();
            window = process.MainWindowHandle;
            return window != 0 && IsWindow(window);
        }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return false; }
    }

    private static void TryKillClient(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: false); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private static void CancelSafely(CancellationTokenSource source)
    {
        try { source.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    private sealed class DeviceSessions
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public CancellationTokenSource? Generation { get; set; }
        public Session? Current { get; set; }
        public int ActiveStarts { get; set; }
    }

    private sealed class Session
    {
        private readonly object _outputSync = new();
        private readonly StringBuilder _output = new();
        private string? _stopReason;
        private volatile bool _rendererDetected;

        public Session(string serial, DeviceSessions device, CancellationTokenSource generation,
            Process process, CancellationToken shutdown)
        {
            Serial = serial;
            Device = device;
            Generation = generation;
            Process = process;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(generation.Token, shutdown);
        }

        public string Serial { get; }
        public DeviceSessions Device { get; }
        public CancellationTokenSource Generation { get; }
        public Process Process { get; }
        public CancellationTokenSource Cancellation { get; }
        public TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Finished { get; set; } = Task.CompletedTask;
        public string? Failure { get; set; }
        public bool RendererDetected => _rendererDetected;
        public string? StopReason { get { lock (_outputSync) return _stopReason; } }

        public void AppendOutput(string line)
        {
            lock (_outputSync)
            {
                _output.AppendLine(line);
                if (_output.Length > 8192) _output.Remove(0, _output.Length - 8192);
            }
            if (line.Contains("INFO: Renderer:", StringComparison.OrdinalIgnoreCase)) _rendererDetected = true;
        }

        public string GetOutput()
        {
            lock (_outputSync) return _output.ToString().Trim();
        }

        public void SetStopReason(string reason)
        {
            lock (_outputSync) _stopReason ??= reason;
        }

        public void Stop(string reason)
        {
            SetStopReason(reason);
            CancelSafely(Cancellation);
        }
    }
}
