using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AdbDock.Core;

public static partial class OperationsChecks
{
    private const string PipeEnvironment = "ADB_DOCK_OPERATIONS_CHECK_PIPE";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(12);

    public static async Task<int?> HandleFakeToolAsync(string[] args)
    {
        var isDescendant = args.Length == 1 && args[0] == "--fake-operation-descendant";
        if ((!isDescendant && (args.Length < 2 || args[0] is not ("-s" or "-t"))) ||
            Environment.GetEnvironmentVariable(PipeEnvironment) is not { Length: > 0 } pipeName)
            return null;

        // The SDK apphost is the only executable used by these tests. No real adb/scrcpy is resolved.
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var connectionTimeout = new CancellationTokenSource(Deadline);
        await pipe.ConnectAsync(connectionTimeout.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(new Invocation(Environment.ProcessId, args,
            Environment.GetEnvironmentVariable("ADB"))));
        while (await reader.ReadLineAsync() is { } line)
        {
            var reply = JsonSerializer.Deserialize<Reply>(line) ?? throw new InvalidDataException("Missing fake-tool reply.");
            await Task.WhenAll(Console.Out.WriteAsync(reply.Output ?? ""), Console.Error.WriteAsync(reply.Error ?? ""));
            await Task.WhenAll(Console.Out.FlushAsync(), Console.Error.FlushAsync());
            if (reply.Binary is not null)
            {
                var stdout = Console.OpenStandardOutput();
                await stdout.WriteAsync(Convert.FromBase64String(reply.Binary));
                await stdout.FlushAsync();
            }
            if (reply.KeepPipesOpen)
            {
                // No redirection here: this child inherits the fake client's redirected handles.
                // Disposing Process only releases our handle; it must not stop the descendant.
                // Redirecting stdin makes .NET explicitly pass the parent's stdout/stderr handles
                // on Windows; CreateNoWindow alone otherwise creates a child without those pipes.
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true
                };
                start.ArgumentList.Add("--fake-operation-descendant");
                using var descendant = Process.Start(start) ?? throw new InvalidOperationException("Cannot start fake descendant.");
            }
            await writer.WriteLineAsync("ack");
            if (reply.ExitCode is { } exitCode) return exitCode;
        }
        return 97;
    }

    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "AdbDock 操作检查 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        (string Name, Func<FakeTool, string, Task> Run)[] checks =
        [
            ("AdbOperations/MirrorManager: initial and explicit offline rejection", OfflineAsync),
            ("AdbOperations/MirrorManager: reconnects retire generations and idle serial queues", ReconnectResourcesAsync),
            ("AdbOperations: install exit code and Success/Failure text", InstallAsync),
            ("AdbOperations: APK type and missing-file validation", ApkValidationAsync),
            ("AdbOperations: key whitelist and exact directed arguments", KeysAsync),
            ("AdbOperations: fixed getprop queries and selected serial", InfoAsync),
            ("Performance: CPU deltas, memory fallback and partial battery parsing", PerformanceParsersAsync),
            ("Performance: directed read-only commands and partial unavailable results", PerformanceSamplingAsync),
            ("Performance: per-command timeout preserves remaining metrics", PerformanceTimeoutAsync),
            ("Performance: serial queue and caller cancellation reap only owned client", PerformanceQueueAsync),
            ("Performance: transport replacement between samples stops monitoring without replay", PerformanceMonitorAsync),
            ("AdbOperations: binary screenshot bytes and directed exec-out", ScreenshotAsync),
            ("AdbOperations: per-device queue, disconnect cancellation, no replay", DisconnectAsync),
            ("AdbOperations: same-serial transport replacement cancels old clients and queued requests", ReplaceTransportAsync),
            ("AdbOperations: Logcat streaming, independent queue, cancel and dispose", LogcatAsync),
            ("AdbOperations: unexpected Logcat exit exposes stderr", LogcatFailureAsync),
            ("AdbOperations: exited Logcat drains inherited pipes without stopping descendant", LogcatInheritedPipesAsync),
            ("MirrorManager: renderer readiness, concurrent reuse and owned disposal", MirrorReuseAsync),
            ("MirrorManager: disconnect stops session, reconnect requires explicit start", MirrorDisconnectAsync),
            ("MirrorManager: same-serial transport replacement stops session and checks fresh startup", MirrorTransportAsync),
            ("MirrorManager: startup cancellation and bounded failure output", MirrorFailureAsync),
            ("MirrorManager: inherited pipes cannot block exit or a fresh explicit start", MirrorInheritedPipesAsync)
        ];
        var passed = 0;
        try
        {
            foreach (var (name, run) in checks)
            {
                var directory = Path.Combine(root, passed.ToString());
                Directory.CreateDirectory(directory);
                await using var fake = new FakeTool();
                await run(fake, directory);
                passed++;
                Console.WriteLine($"PASS {name}");
            }
            return passed;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task ReconnectResourcesAsync(FakeTool fake, string directory)
    {
        await using var operations = new AdbOperations(fake.Executable);
        await using var mirrors = new MirrorManager(fake.Executable, fake.Executable);
        CheckReconnects(operations, operations.UpdateOnlineDevices);
        CheckReconnects(mirrors, mirrors.UpdateOnlineDevices);

        static void CheckReconnects(object service, Action<string[]> update)
        {
            // Keep the OS handle for each disconnected generation. A CTS releases
            // that handle on disposal, even if the test still holds the WaitHandle.
            var devices = (System.Collections.IDictionary)service.GetType()
                .GetField("_devices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            for (var i = 0; i < 64; i++)
            {
                update(["stable", "reconnected", $"transient-{i}"]);
                var device = devices["reconnected"]!;
                var connection = device.GetType().GetProperty("Generation")!.GetValue(device)!;
                var token = (CancellationToken)connection.GetType().GetProperty("Token")!.GetValue(connection)!;
                var handle = token.WaitHandle.SafeWaitHandle;
                update(["stable"]);
                Check(handle.IsClosed, "Idle disconnected generation must release its token source.");
                Check(devices.Count == 1 && devices.Contains("stable"), "Only the still-online device should retain its idle queue.");
            }
            update([]);
            Check(devices.Count == 0, "Last disconnect must release all idle device queues.");
        }
    }

    private static async Task OfflineAsync(FakeTool fake, string directory)
    {
        var apk = await MakeApkAsync(directory);
        await using var operations = new AdbOperations(fake.Executable);
        await ExpectAsync<InvalidOperationException>(() => operations.SendKeyAsync("unknown", "Home"));
        await ExpectAsync<InvalidOperationException>(() => operations.ReadInfoAsync("unknown"));
        await ExpectAsync<InvalidOperationException>(() => operations.ScreenshotAsync("unknown", Path.Combine(directory, "offline.png")));
        await ExpectAsync<InvalidOperationException>(() => operations.InstallAsync("unknown", apk));
        await ExpectAsync<InvalidOperationException>(() => operations.ReadLogcatAsync("unknown", _ => { }));
        await ExpectAsync<InvalidOperationException>(() => operations.ReadPerformanceAsync("unknown"));
        await ExpectAsync<InvalidOperationException>(() => operations.MonitorPerformanceAsync("unknown", _ => { }));
        operations.UpdateOnlineDevices(["formerly-online"]);
        operations.UpdateOnlineDevices([]);
        await ExpectAsync<InvalidOperationException>(() => operations.SendKeyAsync("formerly-online", "Back"));
        await using var mirrors = new MirrorManager(fake.Executable, fake.Executable);
        await ExpectAsync<InvalidOperationException>(() => mirrors.StartOrShowAsync("unknown"));
        mirrors.UpdateOnlineDevices(["formerly-online"]);
        mirrors.UpdateOnlineDevices([]);
        await ExpectAsync<InvalidOperationException>(() => mirrors.StartOrShowAsync("formerly-online"));
        Check(fake.Count == 0, "Offline operations must not create any client.");
        Check(!File.Exists(Path.Combine(directory, "offline.png")), "Offline capture must not create a file.");
    }

    private static async Task InstallAsync(FakeTool fake, string directory)
    {
        var apk = await MakeApkAsync(directory);
        await using var operations = new AdbOperations(fake.Executable);
        operations.UpdateOnlineDevices(["install-success", "install-failure", "install-no-success", "install-bad-exit"]);
        var success = operations.InstallAsync("install-success", apk);
        var peer = await fake.NextAsync();
        EqualArguments(peer, "-s", "install-success", "install", "-r", apk);
        await peer.SendAsync(output: "Performing Streamed Install\r\nSuccess\r\n", exitCode: 0);
        await success.WaitAsync(Deadline);

        var failure = operations.InstallAsync("install-failure", apk);
        peer = await fake.NextAsync();
        await peer.SendAsync(output: "Failure [INSTALL_FAILED_TEST_ONLY]\n", exitCode: 0);
        var error = await ExpectAsync<InvalidOperationException>(() => failure);
        Check(error.Message.Contains("INSTALL_FAILED_TEST_ONLY", StringComparison.Ordinal), "Zero-exit Failure reason is preserved.");

        var noSuccess = operations.InstallAsync("install-no-success", apk);
        peer = await fake.NextAsync();
        await peer.SendAsync(output: "Performing Streamed Install\n", exitCode: 0);
        await ExpectAsync<InvalidOperationException>(() => noSuccess);

        var badExit = operations.InstallAsync("install-bad-exit", apk);
        peer = await fake.NextAsync();
        await peer.SendAsync(output: "Success\n", error: "fake installation exit failure\n", exitCode: 9);
        error = await ExpectAsync<InvalidOperationException>(() => badExit);
        Check(error.Message.Contains("fake installation exit failure", StringComparison.Ordinal), "Success text cannot override nonzero exit.");
        Check(fake.Count == 4, "Exactly four directed installation clients were started.");
    }

    private static async Task ApkValidationAsync(FakeTool fake, string directory)
    {
        await using var operations = new AdbOperations(fake.Executable);
        operations.UpdateOnlineDevices(["apk-validation"]);
        var textFile = Path.Combine(directory, "不是应用.txt");
        await File.WriteAllTextAsync(textFile, "not an apk");
        await ExpectAsync<ArgumentException>(() => operations.InstallAsync("apk-validation", textFile));
        await ExpectAsync<FileNotFoundException>(() => operations.InstallAsync("apk-validation", Path.Combine(directory, "missing.apk")));
        Check(fake.Count == 0, "APK validation happens before process creation.");
    }

    private static async Task KeysAsync(FakeTool fake, string directory)
    {
        const string serial = "selected 手机 & ' ; device";
        await using var operations = new AdbOperations(fake.Executable);
        operations.UpdateOnlineDevices([serial]);
        foreach (var (key, code) in new[] { ("Home", "3"), ("Back", "4"), ("Recent", "187"), ("Wake", "224") })
        {
            var task = operations.SendKeyAsync(serial, key);
            var peer = await fake.NextAsync();
            EqualArguments(peer, "-s", serial, "shell", "input", "keyevent", code);
            await peer.SendAsync(exitCode: 0);
            await task.WaitAsync(Deadline);
        }
        foreach (var key in new[] { "Home;reboot", "26", "Shutdown", "input keyevent 3" })
            await ExpectAsync<ArgumentException>(() => operations.SendKeyAsync(serial, key));
        Check(fake.Count == 4, "Non-whitelisted remote shell input never launches a client.");
    }

    private static async Task InfoAsync(FakeTool fake, string directory)
    {
        const string serial = "info-serial-中文";
        await using var operations = new AdbOperations(fake.Executable);
        operations.UpdateOnlineDevices([serial]);
        var task = operations.ReadInfoAsync(serial);
        var queries = new[]
        {
            ("ro.product.model", "Model Test"), ("ro.product.manufacturer", "Maker Test"),
            ("ro.build.version.release", "16"), ("ro.build.version.sdk", "36")
        };
        foreach (var (property, value) in queries)
        {
            var peer = await fake.NextAsync();
            EqualArguments(peer, "-s", serial, "shell", "getprop", property);
            await peer.SendAsync(output: value + "\r\n", exitCode: 0);
        }
        var info = await task.WaitAsync(Deadline);
        Check(info.Contains(serial, StringComparison.Ordinal), "Device information contains the selected serial.");
        foreach (var (_, value) in queries) Check(info.Contains(value, StringComparison.Ordinal), "Property value is included: " + value);
    }

    private static async Task ScreenshotAsync(FakeTool fake, string directory)
    {
        await using var operations = new AdbOperations(fake.Executable);
        operations.UpdateOnlineDevices(["screenshot"]);
        var destination = Path.Combine(directory, "截图 二进制.png");
        // Two independent fixtures: split-IDAT RGBA and an Adam7 indexed image.
        var rgba = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAYAAAD0In+KAAAABElEQVR4nGP4TRdsqgAAAA1JREFUz8vF8K+BQQsADjoCvxUYwlsAAAAASUVORK5CYII=");
        var adam7 = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAkAAAAJAQMAAAGtWKTFAAAABlBMVEUAAAD///+l2Z/dAAAABElEQVR4nGNgs8N33AAAAAhJREFUIBYAAAAqAAFR7WWFAAAAAElFTkSuQmCC");
        async Task CaptureAsync(byte[] data)
        {
            var task = operations.ScreenshotAsync("screenshot", destination);
            var peer = await fake.NextAsync();
            EqualArguments(peer, "-s", "screenshot", "exec-out", "screencap", "-p");
            await peer.SendAsync(binary: data, exitCode: 0);
            await task.WaitAsync(Deadline);
        }
        foreach (var valid in new[] { rgba, adam7 })
        {
            await CaptureAsync(valid);
            Check((await File.ReadAllBytesAsync(destination)).SequenceEqual(valid), "Successful capture preserves the original PNG bytes.");
        }
        var badCrc = rgba.ToArray();
        badCrc[29] ^= 1; // First byte of the IHDR CRC.
        (string Scenario, byte[] Bytes)[] invalid =
        [
            ("signature only", rgba[..16]),
            ("truncated IEND", rgba[..^3]),
            ("bad chunk CRC", badCrc),
            // The following samples have valid chunk CRCs.
            ("bad zlib checksum", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAYAAAD0In+KAAAABElEQVR4nGP4TRdsqgAAAA1JREFUz8vF8K+BQQsADjoCvmIf8s0AAAAASUVORK5CYII=")),
            ("truncated zlib", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAYAAAD0In+KAAAABElEQVR4nGP4TRdsqgAAAApJREFUz8vF8K+BQQsADmlWo+YAAAAASUVORK5CYII=")),
            ("short scanline", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAYAAAD0In+KAAAABElEQVR4nGP4TRdsqgAAAAxJREFUz8vF8K+BAQALewKVScLOHwAAAABJRU5ErkJggg==")),
            ("invalid filter", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAYAAAD0In+KAAAABElEQVR4nGP/03P5CQAAAA1JREFUz8vF8K+BQQsADnkCxk4dOFoAAAAASUVORK5CYII="))
        ];
        foreach (var (scenario, data) in invalid)
        {
            await ExpectAsync<InvalidDataException>(() => CaptureAsync(data));
            Check((await File.ReadAllBytesAsync(destination)).SequenceEqual(adam7), $"Invalid capture ({scenario}) preserves the last good image.");
            Check(Directory.GetFiles(directory).Length == 1, $"Invalid capture ({scenario}) removes the temporary file.");
        }
    }

    private static async Task DisconnectAsync(FakeTool fake, string directory)
    {
        var apk = await MakeApkAsync(directory);
        await using var operations = new AdbOperations(fake.Executable);
        operations.UpdateOnlineDevices(["queue-A", "queue-B"]);
        var running = operations.InstallAsync("queue-A", apk);
        var held = await fake.NextAsync();
        EqualArguments(held, "-s", "queue-A", "install", "-r", apk);
        var queued = operations.SendKeyAsync("queue-A", "Home");
        Check(!queued.IsCompleted, "Same-device short operation waits behind installation.");

        var otherDevice = operations.SendKeyAsync("queue-B", "Back");
        var independent = await fake.NextAsync();
        EqualArguments(independent, "-s", "queue-B", "shell", "input", "keyevent", "4");
        await independent.SendAsync(exitCode: 0);
        await otherDevice.WaitAsync(Deadline);
        Check(!running.IsCompleted && !queued.IsCompleted, "Other devices run without releasing the held device queue.");

        operations.UpdateOnlineDevices(["queue-B"]);
        operations.UpdateOnlineDevices(["queue-A", "queue-B"]);
        await ExpectAsync<OperationCanceledException>(() => running);
        await ExpectAsync<OperationCanceledException>(() => queued);
        await held.WaitForExitAsync();
        var fresh = operations.SendKeyAsync("queue-A", "Recent");
        var explicitRequest = await fake.NextAsync();
        EqualArguments(explicitRequest, "-s", "queue-A", "shell", "input", "keyevent", "187");
        await explicitRequest.SendAsync(exitCode: 0);
        await fresh.WaitAsync(Deadline);
        Check(fake.Count == 3, "Reconnect never replays the queued Home or old installation.");
    }

    private static async Task ReplaceTransportAsync(FakeTool fake, string directory)
    {
        const string serial = "reused-serial";
        var apk = await MakeApkAsync(directory);
        await using var operations = new AdbOperations(fake.Executable);
        operations.ApplySnapshot(OnlineTransport(serial, 41));
        var install = operations.InstallAsync(serial, apk);
        var installer = await fake.NextAsync();
        EqualArguments(installer, "-t", "41", "install", "-r", apk);
        var queued = operations.SendKeyAsync(serial, "Home");
        var logs = operations.ReadLogcatAsync(serial, _ => { });
        var logcat = await fake.NextAsync();
        EqualArguments(logcat, "-t", "41", "logcat", "-v", "time");

        operations.ApplySnapshot(new DeviceSnapshot([], "temporary listing failure"));
        operations.ApplySnapshot(OnlineTransport(serial, 41, "Updated model"));
        Check(!install.IsCompleted && !queued.IsCompleted && !logs.IsCompleted && fake.Count == 2,
            "A failed listing or refreshed model of the same transport preserves ongoing operations.");

        operations.ApplySnapshot(OnlineTransport(serial, 42));
        await ExpectAsync<OperationCanceledException>(() => install);
        await ExpectAsync<OperationCanceledException>(() => queued);
        await ExpectAsync<OperationCanceledException>(() => logs);
        await installer.WaitForExitAsync();
        await logcat.WaitForExitAsync();

        var fresh = operations.SendKeyAsync(serial, "Back");
        var key = await fake.NextAsync();
        EqualArguments(key, "-t", "42", "shell", "input", "keyevent", "4");
        await key.SendAsync(exitCode: 0);
        await fresh.WaitAsync(Deadline);
        Check(fake.Count == 3, "Only a new explicit request may use the new transport.");
    }

    private static DeviceSnapshot OnlineTransport(string serial, ulong id, string name = "Fixture") =>
        new([new AdbDevice(serial, name, "device", "USB") { TransportId = id }]);

    private static async Task LogcatAsync(FakeTool fake, string directory)
    {
        await using var operations = new AdbOperations(fake.Executable);
        operations.UpdateOnlineDevices(["logcat"]);
        using var cancellation = new CancellationTokenSource();
        var lines = new ConcurrentQueue<string>();
        var bothStreams = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logcat = operations.ReadLogcatAsync("logcat", line =>
        {
            lines.Enqueue(line);
            if (lines.Any(value => value.Contains("stdout-marker", StringComparison.Ordinal)) &&
                lines.Any(value => value.Contains("stderr-marker", StringComparison.Ordinal))) bothStreams.TrySetResult();
        }, cancellation.Token);
        var peer = await fake.NextAsync();
        EqualArguments(peer, "-s", "logcat", "logcat", "-v", "time");
        await peer.SendAsync(output: new string('o', 70000) + " stdout-marker\n",
            error: new string('e', 70000) + " stderr-marker\n");
        await bothStreams.Task.WaitAsync(Deadline);
        var key = operations.SendKeyAsync("logcat", "Home");
        var keyPeer = await fake.NextAsync();
        EqualArguments(keyPeer, "-s", "logcat", "shell", "input", "keyevent", "3");
        await keyPeer.SendAsync(exitCode: 0);
        await key.WaitAsync(Deadline);
        Check(!logcat.IsCompleted, "Logcat does not hold the short-command gate.");
        cancellation.Cancel();
        await ExpectAsync<OperationCanceledException>(() => logcat);
        await peer.WaitForExitAsync();

        var secondLogcat = operations.ReadLogcatAsync("logcat", _ => { });
        var secondPeer = await fake.NextAsync();
        await operations.DisposeAsync();
        await ExpectAsync<OperationCanceledException>(() => secondLogcat);
        await secondPeer.WaitForExitAsync();
        await operations.DisposeAsync();
        await ExpectAsync<ObjectDisposedException>(() => operations.SendKeyAsync("logcat", "Home"));
    }

    private static async Task LogcatFailureAsync(FakeTool fake, string directory)
    {
        await using var operations = new AdbOperations(fake.Executable);
        operations.UpdateOnlineDevices(["failed-logcat"]);
        var task = operations.ReadLogcatAsync("failed-logcat", _ => { });
        var peer = await fake.NextAsync();
        await peer.SendAsync(error: "fake logcat permission denied\n", exitCode: 7);
        var failure = await ExpectAsync<InvalidOperationException>(() => task);
        Check(failure.Message.Contains("fake logcat permission denied", StringComparison.Ordinal), "Unexpected Logcat exit includes stderr.");
        await peer.WaitForExitAsync();
    }

    private static async Task LogcatInheritedPipesAsync(FakeTool fake, string directory)
    {
        await using var operations = new AdbOperations(fake.Executable);
        operations.UpdateOnlineDevices(["inherited-logcat"]);
        var inheritedOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = operations.ReadLogcatAsync("inherited-logcat", line =>
        {
            if (line == "DESCENDANT_PIPE_MARKER") inheritedOutput.TrySetResult();
        });
        var parent = await fake.NextAsync();
        await parent.SendAsync(keepPipesOpen: true);
        var descendant = await fake.NextAsync();
        EqualArguments(descendant, "--fake-operation-descendant");
        await descendant.SendAsync(output: "DESCENDANT_PIPE_MARKER\n");
        // This proves the descendant actually owns the Logcat client's pipe, not an unrelated one.
        await inheritedOutput.Task.WaitAsync(Deadline);
        await parent.SendAsync(error: "OWNED_LOGCAT_EXIT\n", exitCode: 7);
        var failure = await ExpectAsync<InvalidOperationException>(() => task);
        Check(failure.Message.Contains("OWNED_LOGCAT_EXIT", StringComparison.Ordinal), "Bounded drain retains the owned client's stderr.");
        await parent.WaitForExitAsync();
        Check(descendant.IsRunning, "Exited Logcat must not terminate a descendant retaining its pipes.");
    }

    private static async Task MirrorReuseAsync(FakeTool fake, string directory)
    {
        const string serial = "mirror-selected 中文";
        await using var mirrors = new MirrorManager(fake.Executable, fake.Executable);
        mirrors.UpdateOnlineDevices([serial]);
        var statuses = new ConcurrentQueue<string>();
        var reportedSerials = new ConcurrentQueue<string>();
        mirrors.StatusChanged += (device, status) =>
        {
            reportedSerials.Enqueue(device);
            statuses.Enqueue(status);
        };
        var first = mirrors.StartOrShowAsync(serial);
        var concurrent = mirrors.StartOrShowAsync(serial);
        var peer = await fake.NextAsync();
        EqualArguments(peer, "-s", serial, "--window-title", "ADB Dock · " + serial);
        Check(peer.Invocation.Adb == fake.Executable, "scrcpy receives the selected adb executable through ADB.");
        Check(!first.IsCompleted && !concurrent.IsCompleted, "Process creation alone must not complete mirror startup.");
        Check(!statuses.Any(status => status.Contains("运行中", StringComparison.Ordinal)), "No running status before window or renderer evidence.");
        await peer.SendAsync(output: "INFO: Renderer: fake-sdk-renderer\n");
        await Task.WhenAll(first, concurrent).WaitAsync(Deadline);
        await mirrors.StartOrShowAsync(serial).WaitAsync(Deadline);
        Check(fake.Count == 1, "Concurrent and repeated starts reuse exactly one managed client.");
        Check(statuses.Any(status => status.Contains("运行中", StringComparison.Ordinal)), "Official renderer output establishes running state.");
        Check(reportedSerials.All(device => device == serial), "Every mirror status remains bound to the selected serial.");
        Check(!statuses.Any(status => status.Contains("已显示", StringComparison.Ordinal)), "A windowless fake renderer is never described as displayed.");
        using var external = ProcessRunner.Start(fake.Executable, ["-s", "external-session", "--window-title", "Not managed by MirrorManager"]);
        var externalPeer = await fake.NextAsync();
        Check(externalPeer.Invocation.Pid == external.Id, "External fake session belongs to the test, not to MirrorManager.");
        await mirrors.DisposeAsync();
        await mirrors.DisposeAsync();
        await peer.WaitForExitAsync();
        Check(!external.HasExited, "Mirror disposal must not kill an independently started process with the same executable.");
        external.Kill(entireProcessTree: false);
        await externalPeer.WaitForExitAsync();
        await ExpectAsync<ObjectDisposedException>(() => mirrors.StartOrShowAsync(serial));
    }

    private static async Task MirrorDisconnectAsync(FakeTool fake, string directory)
    {
        const string serial = "mirror-disconnect";
        await using var mirrors = new MirrorManager(fake.Executable, fake.Executable);
        mirrors.UpdateOnlineDevices([serial]);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mirrors.StatusChanged += (_, status) => { if (status.Contains("断开", StringComparison.Ordinal)) disconnected.TrySetResult(); };
        var first = mirrors.StartOrShowAsync(serial);
        var oldPeer = await fake.NextAsync();
        await oldPeer.SendAsync(output: "INFO: Renderer: fake-sdk-renderer\n");
        await first.WaitAsync(Deadline);
        mirrors.UpdateOnlineDevices([]);
        mirrors.UpdateOnlineDevices([serial]);
        await oldPeer.WaitForExitAsync();
        await disconnected.Task.WaitAsync(Deadline);
        Check(fake.Count == 1, "Reconnect alone does not launch a mirror.");
        var explicitStart = mirrors.StartOrShowAsync(serial);
        var newPeer = await fake.NextAsync();
        EqualArguments(newPeer, "-s", serial, "--window-title", "ADB Dock · " + serial);
        Check(newPeer.Invocation.Pid != oldPeer.Invocation.Pid, "Explicit reconnect startup creates a new owned session.");
        await newPeer.SendAsync(output: "INFO: Renderer: fake-sdk-renderer\n");
        await explicitStart.WaitAsync(Deadline);
        Check(fake.Count == 2, "Only the explicit reconnect request launches a second client.");
        mirrors.UpdateOnlineDevices([]);
        await newPeer.WaitForExitAsync();
    }

    private static async Task MirrorTransportAsync(FakeTool fake, string directory)
    {
        const string serial = "mirror-transport";
        await using var mirrors = new MirrorManager(fake.Executable, fake.Executable);
        mirrors.ApplySnapshot(OnlineTransport(serial, 41));
        var first = mirrors.StartOrShowAsync(serial);
        var firstCheck = await fake.NextAsync();
        EqualArguments(firstCheck, "-t", "41", "get-serialno");
        await firstCheck.SendAsync(output: serial + "\n", exitCode: 0);
        var oldMirror = await fake.NextAsync();
        EqualArguments(oldMirror, "-s", serial, "--window-title", "ADB Dock · " + serial);
        await oldMirror.SendAsync(output: "INFO: Renderer: fake-sdk-renderer\n");
        await first.WaitAsync(Deadline);

        mirrors.ApplySnapshot(new DeviceSnapshot([], "temporary listing failure"));
        mirrors.ApplySnapshot(OnlineTransport(serial, 41, "Updated model"));
        await mirrors.StartOrShowAsync(serial).WaitAsync(Deadline);
        Check(fake.Count == 2, "Failed listings and metadata updates preserve the existing session.");
        mirrors.ApplySnapshot(OnlineTransport(serial, 42));
        await oldMirror.WaitForExitAsync();
        Check(fake.Count == 2, "Transport replacement alone must not launch a mirror.");

        var stale = mirrors.StartOrShowAsync(serial);
        var staleCheck = await fake.NextAsync();
        EqualArguments(staleCheck, "-t", "42", "get-serialno");
        await staleCheck.SendAsync(error: "transport not found", exitCode: 1);
        await ExpectAsync<InvalidOperationException>(() => stale);
        Check(fake.Count == 3, "A stale transport must not launch the serial-only scrcpy client.");

        var fresh = mirrors.StartOrShowAsync(serial);
        var freshCheck = await fake.NextAsync();
        EqualArguments(freshCheck, "-t", "42", "get-serialno");
        await freshCheck.SendAsync(output: serial + "\n", exitCode: 0);
        var newMirror = await fake.NextAsync();
        EqualArguments(newMirror, "-s", serial, "--window-title", "ADB Dock · " + serial);
        await newMirror.SendAsync(output: "INFO: Renderer: fake-sdk-renderer\n");
        await fresh.WaitAsync(Deadline);
        await mirrors.DisposeAsync();
        await newMirror.WaitForExitAsync();
    }

    private static async Task MirrorFailureAsync(FakeTool fake, string directory)
    {
        const string serial = "mirror-failure";
        await using var mirrors = new MirrorManager(fake.Executable, fake.Executable);
        mirrors.UpdateOnlineDevices([serial]);
        using var cancellation = new CancellationTokenSource();
        var pending = mirrors.StartOrShowAsync(serial, cancellation.Token);
        var cancelled = await fake.NextAsync();
        cancellation.Cancel();
        await ExpectAsync<OperationCanceledException>(() => pending);
        await cancelled.WaitForExitAsync();

        var failure = mirrors.StartOrShowAsync(serial);
        var failed = await fake.NextAsync();
        await failed.SendAsync(error: new string('x', 20000) + "\nSPECIFIC_SCRCPY_FAILURE\n", exitCode: 3);
        var exception = await ExpectAsync<InvalidOperationException>(() => failure);
        Check(exception.Message.Contains("SPECIFIC_SCRCPY_FAILURE", StringComparison.Ordinal), "Startup failure preserves the end of stderr.");
        Check(exception.Message.Length < 9000, "Retained scrcpy output is bounded, not the complete output flood.");
        await failed.WaitForExitAsync();
    }

    private static async Task MirrorInheritedPipesAsync(FakeTool fake, string directory)
    {
        const string serial = "inherited-mirror";
        await using var mirrors = new MirrorManager(fake.Executable, fake.Executable);
        mirrors.UpdateOnlineDevices([serial]);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mirrors.StatusChanged += (_, status) => { if (status == "投屏已结束。") ended.TrySetResult(); };
        var first = mirrors.StartOrShowAsync(serial);
        var parent = await fake.NextAsync();
        await parent.SendAsync(keepPipesOpen: true);
        var descendant = await fake.NextAsync();
        EqualArguments(descendant, "--fake-operation-descendant");
        // Startup completing on this renderer marker proves the descendant inherited scrcpy's pipe.
        await descendant.SendAsync(output: "INFO: Renderer: inherited-child-renderer\n");
        await first.WaitAsync(Deadline);
        await parent.SendAsync(exitCode: 0);
        await ended.Task.WaitAsync(Deadline);
        await parent.WaitForExitAsync();
        Check(descendant.IsRunning, "Mirror completion leaves the inherited-pipe descendant alive.");

        var next = mirrors.StartOrShowAsync(serial);
        var restarted = await fake.NextAsync();
        EqualArguments(restarted, "-s", serial, "--window-title", "ADB Dock · " + serial);
        await restarted.SendAsync(output: "INFO: Renderer: fresh-client-renderer\n");
        await next.WaitAsync(Deadline);
        Check(fake.Count == 3, "An explicit start creates exactly one new mirror after the old client exits.");
        await mirrors.DisposeAsync();
        await restarted.WaitForExitAsync();
        Check(descendant.IsRunning, "Mirror disposal stops its own client without killing the inherited-pipe descendant.");
    }

    private static async Task<string> MakeApkAsync(string directory)
    {
        var path = Path.Combine(directory, "测试 应用.APK");
        await File.WriteAllBytesAsync(path, [0, 1, 2]);
        return path;
    }

    private static void EqualArguments(Peer peer, params string[] expected) =>
        Check(peer.Invocation.Arguments.SequenceEqual(expected),
            "Exact directed argument list. Expected " + JsonSerializer.Serialize(expected) +
            "; actual " + JsonSerializer.Serialize(peer.Invocation.Arguments));

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }

    private static async Task<T> ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action().WaitAsync(Deadline); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private sealed record Invocation(int Pid, string[] Arguments, string? Adb);
    private sealed record Reply(string? Output, string? Error, string? Binary, int? ExitCode, bool KeepPipesOpen = false);

    private sealed class FakeTool : IAsyncDisposable
    {
        private readonly string _pipeName = "AdbDock.Operations." + Guid.NewGuid().ToString("N");
        private readonly string? _previousEnvironment;
        private readonly CancellationTokenSource _stop = new();
        private readonly Channel<Peer> _incoming = Channel.CreateUnbounded<Peer>();
        private readonly List<Peer> _peers = [];
        private readonly Task _accepting;

        public FakeTool()
        {
            var assembly = Assembly.GetExecutingAssembly().Location;
            Executable = OperatingSystem.IsWindows() ? Path.ChangeExtension(assembly, ".exe") : Path.Combine(
                Path.GetDirectoryName(assembly)!, Path.GetFileNameWithoutExtension(assembly));
            if (!File.Exists(Executable)) throw new FileNotFoundException("Checks require the SDK-generated apphost.", Executable);
            _previousEnvironment = Environment.GetEnvironmentVariable(PipeEnvironment);
            Environment.SetEnvironmentVariable(PipeEnvironment, _pipeName);
            _accepting = AcceptAsync();
        }

        public string Executable { get; }
        public int Count { get { lock (_peers) return _peers.Count; } }
        public Task<Peer> NextAsync() => _incoming.Reader.ReadAsync().AsTask().WaitAsync(Deadline);

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    NamedPipeServerStream? pipe = new(_pipeName, PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    StreamReader? reader = null;
                    try
                    {
                        await pipe.WaitForConnectionAsync(_stop.Token);
                        reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                        var hello = await reader.ReadLineAsync(_stop.Token);
                        var invocation = JsonSerializer.Deserialize<Invocation>(hello ?? "") ?? throw new InvalidDataException("Missing fake-tool invocation.");
                        var peer = new Peer(pipe, reader, invocation);
                        lock (_peers) _peers.Add(peer);
                        _incoming.Writer.TryWrite(peer);
                        pipe = null;
                        reader = null;
                    }
                    finally
                    {
                        reader?.Dispose();
                        pipe?.Dispose();
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception exception) { _incoming.Writer.TryComplete(exception); }
            finally { _incoming.Writer.TryComplete(); }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _stop.Cancel();
                await _accepting;
                Peer[] peers;
                lock (_peers) peers = _peers.ToArray();
                foreach (var peer in peers) await peer.DisposeAsync();
            }
            finally
            {
                _stop.Dispose();
                Environment.SetEnvironmentVariable(PipeEnvironment, _previousEnvironment);
            }
        }
    }

    private sealed class Peer : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly Process _process;

        public Peer(NamedPipeServerStream pipe, StreamReader reader, Invocation invocation)
        {
            _pipe = pipe;
            _reader = reader;
            Invocation = invocation;
            _writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            // Capture the exact owned process before allowing the fake child to exit.
            _process = Process.GetProcessById(invocation.Pid);
        }

        public Invocation Invocation { get; }
        public bool IsRunning => !_process.HasExited;

        public async Task SendAsync(string? output = null, string? error = null, byte[]? binary = null, int? exitCode = null,
            bool keepPipesOpen = false)
        {
            var reply = new Reply(output, error, binary is null ? null : Convert.ToBase64String(binary), exitCode, keepPipesOpen);
            await _writer.WriteLineAsync(JsonSerializer.Serialize(reply)).WaitAsync(Deadline);
            Check(await _reader.ReadLineAsync().WaitAsync(Deadline) == "ack", "Fake tool acknowledged flushing its streams.");
        }

        public async Task WaitForExitAsync()
        {
            await _process.WaitForExitAsync().WaitAsync(Deadline);
            Check(_process.HasExited, "The exact fake client owned by this test was reaped.");
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: false);
                await _process.WaitForExitAsync().WaitAsync(Deadline);
            }
            finally
            {
                _writer.Dispose();
                _reader.Dispose();
                _pipe.Dispose();
                _process.Dispose();
            }
        }
    }
}
