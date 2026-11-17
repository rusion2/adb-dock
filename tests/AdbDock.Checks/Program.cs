using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AdbDock.Core;

internal static class Program
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(10);
    private static string _root = "";
    private static string _tool = "";
    private static int _assertions;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = new UTF8Encoding(false);
        if (await UiAdbFixture.RunAsync(args) is { } uiExit) return uiExit;
        if (await OperationsChecks.HandleFakeToolAsync(args) is { } operationExitCode) return operationExitCode;
        if (args.Length > 0 && args[0].StartsWith("--fake-", StringComparison.Ordinal))
            return await RunFakeAsync(args);
        if (Environment.GetEnvironmentVariable("ADB_DOCK_CHECKS_STATE") is { Length: > 0 } state &&
            args.Length > 0 && args[0] is "version" or "devices" or "track-devices")
            return await RunFakeAdbAsync(args, state);

        _root = Path.Combine(Path.GetTempPath(), "AdbDock 检查 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("ParseDevices: states, order, models, daemon output and counts", ParseMixedAsync),
            ("ParseDevices: empty, malformed and duplicate rows", ParseMalformedAsync),
            ("ParseDevices: reliable connection and port boundaries", ParseConnectionsAsync),
            ("ToolResolver: explicit, PATH, bundle and invalid configuration", ResolverAsync),
            ("ProcessRunner: ArgumentList, Unicode, environment and working directory", ArgumentsAsync),
            ("ProcessRunner: concurrent stdout/stderr and nonzero exit", OutputAsync),
            ("ProcessRunner: timeout cleans up only its own client", TimeoutAsync),
            ("ProcessRunner: cancellation and pre-cancelled calls", CancellationAsync),
            ("ProcessRunner: does not kill descendant/shared-server processes", DescendantAsync),
            ("ProcessRunner: exact binary bytes and atomic replacement", BinaryAsync),
            ("ProcessRunner: binary failure/empty output preserve destinations", BinaryFailureAsync),
            ("ProcessRunner: binary timeout/cancellation clean temporary files", BinaryCancellationAsync),
            ("AdbService: handshake, serial refresh, polling and listener recovery", ServiceAsync),
            ("AdbService: missing tool is an error, not zero connected devices", ServiceFailureAsync),
            ("SettingsStore: JSON round-trip, corrupt defaults and preserved backup", SettingsAsync)
        };
        var passed = 0;
        try
        {
            _tool = PrepareFakeTool();
            foreach (var (name, run) in tests)
            {
                try
                {
                    await run();
                    passed++;
                    Console.WriteLine($"PASS {name}");
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"FAIL {name}\n{exception}");
                }
            }
            var total = tests.Length;
            try
            {
                var operationsPassed = await OperationsChecks.RunAsync();
                passed += operationsPassed;
                total += operationsPassed;
                Console.WriteLine($"PASS OperationsChecks ({operationsPassed} checks)");
            }
            catch (Exception exception)
            {
                total++;
                Console.Error.WriteLine($"FAIL OperationsChecks\n{exception}");
            }
            Console.WriteLine($"{passed}/{total} checks passed; {_assertions} core assertions.");
            return passed == total ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (Exception exception) { Console.Error.WriteLine($"Temporary cleanup: {exception.Message}"); }
        }
    }

    private static Task ParseMixedAsync()
    {
        var devices = AdbService.ParseDevices("""
            * daemon not running; starting now at tcp:5037
            * daemon started successfully
            List of devices attached
            USB123 device usb:1-4 product:panther model:Pixel_8_Pro device:husky transport_id:1
            AUTH unauthorized usb:2-1 transport_id:2
            OFF offline transport_id:3
            emulator-5554 device product:sdk model:Android_SDK device:emu transport_id:4
            192.168.1.8:5555 device model:WiFi_Phone transport_id:5
            [2001:db8::1]:37123 device transport_id:6
            REC recovery model:Recovery_Device transport_id:7
            SIDE sideload transport_id:8
            ???????? no permissions (user needs udev rules)
            FUTURE custom-state model:Future_Device transport_id:9
            """);
        Equal(10, devices.Count, "all discovered states counted");
        Equal("Pixel 8 Pro", devices[0].Name, "model underscores become spaces");
        Equal("AUTH", devices[1].Name, "missing model falls back to serial");
        Equal("unauthorized", devices[1].State, "unauthorized preserved");
        Equal("offline", devices[2].State, "offline preserved");
        Equal("recovery", devices[6].State, "recovery preserved");
        Equal("sideload", devices[7].State, "sideload preserved");
        Equal("no permissions", devices[8].State, "multiword no-permissions state");
        Equal("custom-state", devices[9].State, "future raw states preserved");
        Equal("USB", devices[0].Connection, "explicit USB transport");
        Equal("模拟器", devices[3].Connection, "emulator serial");
        Equal("网络", devices[4].Connection, "IPv4 serial");
        Equal("网络", devices[5].Connection, "bracketed IPv6 serial");
        Equal("未知", devices[2].Connection, "unknown is not guessed USB");
        var snapshot = new DeviceSnapshot(devices);
        Check(snapshot.IsAvailable, "successful snapshot available");
        Equal(4, snapshot.OnlineCount, "online only counts device state");
        Check(!devices[1].IsOnline && !devices[6].IsOnline, "unauthorized/recovery are not online");
        Equal("USB123,AUTH,OFF,emulator-5554,192.168.1.8:5555,[2001:db8::1]:37123,REC,SIDE,????????,FUTURE",
            string.Join(',', devices.Select(device => device.Serial)), "ADB order maintained");
        return Task.CompletedTask;
    }

    private static Task ParseMalformedAsync()
    {
        Equal(0, AdbService.ParseDevices("").Count, "empty output");
        Equal(0, AdbService.ParseDevices("\r\n \t\n﻿List of devices attached\r\n").Count, "empty header and BOM");
        Equal(0, AdbService.ParseDevices("""
            * daemon not running; starting now at tcp:5037
            * daemon started successfully
            adb: failed to check server version
            error: cannot connect to daemon at tcp:5037
            broken
            missing-state
            broken invalid! model:test
            random row with extra garbage
            0000
            """).Count, "diagnostics, framing and malformed rows ignored");
        var devices = AdbService.ParseDevices("SERIAL\tdevice model:\r\nSERIAL offline\nOTHER unauthorized\n");
        Equal(2, devices.Count, "duplicate serial does not inflate count");
        Equal("SERIAL", devices[0].Name, "empty model falls back to serial");
        Equal("device", devices[0].State, "first occurrence retains ADB order");
        var error = new DeviceSnapshot(Array.Empty<AdbDevice>(), "ADB unavailable");
        Check(!error.IsAvailable, "error snapshot is not a successful empty list");
        Check(new DeviceSnapshot(Array.Empty<AdbDevice>()).IsAvailable, "empty success remains available");
        Throws<ArgumentNullException>(() => AdbService.ParseDevices(null!), "null parser argument");
        return Task.CompletedTask;
    }

    private static Task ParseConnectionsAsync()
    {
        var cases = new (string Serial, string Metadata, string Expected)[]
        {
            ("127.0.0.1:1", "", "网络"),
            ("192.168.1.2:65535", "", "网络"),
            ("192.168.1.2:65536", "", "未知"),
            ("192.168.1.2:0", "", "未知"),
            ("192.168.1.2:-1", "", "未知"),
            ("192.168.1.999:5555", "", "未知"),
            ("phone.example:5555", "", "未知"),
            ("127.1:5555", "", "未知"),
            ("0x7f000001:5555", "", "未知"),
            ("[::1]:5555", "", "网络"),
            ("2001:db8::1:5555", "", "未知"),
            ("[::1]:65536", "", "未知"),
            ("emulator-5556", "", "模拟器"),
            ("emulator-phone", "", "未知"),
            ("emulator-65536", "", "未知"),
            ("USB-Looking-Serial", "", "未知"),
            ("any-serial", "usb:", "未知"),
            ("another-serial", "usb:2-3", "USB"),
            ("adb-phone._adb-tls-connect._tcp", "", "未知")
        };
        foreach (var item in cases)
        {
            var parsed = AdbService.ParseDevices($"{item.Serial} device {item.Metadata}");
            Equal(1, parsed.Count, $"valid row {item.Serial}");
            Equal(item.Expected, parsed[0].Connection, $"connection {item.Serial}");
        }
        return Task.CompletedTask;
    }

    private static Task ResolverAsync()
    {
        var directory = NewDirectory("路径 包含 空格");
        var configuredDirectory = Path.Combine(directory, "指定工具");
        var pathDirectory = Path.Combine(directory, "PATH 工具");
        Directory.CreateDirectory(configuredDirectory);
        Directory.CreateDirectory(pathDirectory);
        var configured = Path.Combine(configuredDirectory, "adb.exe");
        var fromPath = Path.Combine(pathDirectory, "adb.exe");
        var scrcpy = Path.Combine(pathDirectory, "scrcpy.exe");
        File.WriteAllText(configured, "fake");
        File.WriteAllText(fromPath, "fake");
        File.WriteAllText(scrcpy, "fake");
        using var path = new EnvironmentScope("PATH", $"\"{pathDirectory}\"");
        using var expansion = new EnvironmentScope("ADB_DOCK_CHECKS_TOOLS", configuredDirectory);
        Equal(configured, ToolResolver.Resolve(configured, "adb.exe"), "explicit path precedes PATH");
        Equal(configured, ToolResolver.Resolve(configuredDirectory, "adb.exe"), "configured directory supported");
        Equal(configured, ToolResolver.Resolve($"\"{configured}\"", "adb.exe"), "quoted explicit path supported");
        Equal(configured, ToolResolver.Resolve("%ADB_DOCK_CHECKS_TOOLS%/adb.exe", "adb.exe"), "environment path expanded");
        Equal(fromPath, ToolResolver.Resolve("", "adb.exe"), "PATH resolution");
        Equal(scrcpy, ToolResolver.Resolve(" ", "scrcpy.exe"), "scrcpy PATH resolution");
        Throws<FileNotFoundException>(() => ToolResolver.Resolve(Path.Combine(directory, "missing.exe"), "adb.exe"),
            "bad explicit path never silently falls back");
        Throws<FileNotFoundException>(() => ToolResolver.Resolve(directory, "scrcpy.exe"), "bad explicit directory reported");
        Throws<ArgumentException>(() => ToolResolver.Resolve("", "../adb.exe"), "tool name cannot escape bundle directory");

        Environment.SetEnvironmentVariable("PATH", "");
        foreach (var (name, folder) in new[] { ("adb.exe", "platform-tools"), ("scrcpy.exe", "scrcpy") })
        {
            var bundleDirectory = Path.Combine(AppContext.BaseDirectory, "tools", folder);
            var bundle = Path.Combine(bundleDirectory, name);
            var existed = File.Exists(bundle);
            Directory.CreateDirectory(bundleDirectory);
            try
            {
                if (!existed) File.WriteAllText(bundle, "fake bundled file");
                Equal(bundle, ToolResolver.Resolve("", name), $"bundled {name} fallback");
                if (!existed)
                {
                    File.Delete(bundle);
                    Throws<FileNotFoundException>(() => ToolResolver.Resolve("", name), $"missing {name} reported");
                }
            }
            finally
            {
                if (!existed && File.Exists(bundle)) File.Delete(bundle);
                if (!Directory.EnumerateFileSystemEntries(bundleDirectory).Any()) Directory.Delete(bundleDirectory);
            }
        }
        var tools = Path.Combine(AppContext.BaseDirectory, "tools");
        if (Directory.Exists(tools) && !Directory.EnumerateFileSystemEntries(tools).Any()) Directory.Delete(tools);
        return Task.CompletedTask;
    }

    private static async Task ArgumentsAsync()
    {
        string[] arguments = ["", "has spaces", "中文路径/截图 文件.png", "quote\"inside", "trailing\\", "\\\"", "&|<>^%PATH%;", "line\r\nbreak", "'single'"];
        var environment = new Dictionary<string, string> { ["ADB_DOCK_CHECKS_ENV"] = "子进程 专用 & value" };
        using var process = ProcessRunner.Start(_tool, new[] { "--fake-report" }.Concat(arguments), environment);
        Check(!process.StartInfo.UseShellExecute, "shell execution disabled");
        Check(process.StartInfo.CreateNoWindow, "no command window");
        Check(process.StartInfo.RedirectStandardOutput && process.StartInfo.RedirectStandardError, "both streams redirected");
        Equal(arguments.Length + 1, process.StartInfo.ArgumentList.Count, "arguments are not flattened");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var report = JsonSerializer.Deserialize<FakeReport>(await stdout)!;
        Check(arguments.SequenceEqual(report.Arguments), "empty, quoted, Unicode and metacharacter arguments round-trip exactly");
        Equal(Path.GetDirectoryName(_tool), report.WorkingDirectory, "working directory is the tool's directory");
        Equal(environment["ADB_DOCK_CHECKS_ENV"], report.Environment, "child environment override");
        Check(Environment.GetEnvironmentVariable("ADB_DOCK_CHECKS_ENV") != report.Environment, "parent environment unchanged");
        Equal("", await stderr, "no unexpected stderr");
        Equal(0, process.ExitCode, "fake process success");
        var run = await ProcessRunner.RunAsync(_tool, ["--fake-report", ""], Timeout.InfiniteTimeSpan, environment: environment);
        Equal(0, run.ExitCode, "infinite timeout accepted");
        Check(JsonSerializer.Deserialize<FakeReport>(run.Output)!.Arguments.SequenceEqual(new[] { "" }), "RunAsync forwards empty argument");
        using var path = new EnvironmentScope("PATH", Path.GetDirectoryName(_tool));
        var byName = await ProcessRunner.RunAsync(Path.GetFileName(_tool), ["--fake-report"], ShortTimeout);
        Equal(Path.GetDirectoryName(_tool), JsonSerializer.Deserialize<FakeReport>(byName.Output)!.WorkingDirectory,
            "bare executable resolved from PATH still uses its own working directory");
    }

    private static async Task OutputAsync()
    {
        var result = await ProcessRunner.RunAsync(_tool, ["--fake-flood"], ShortTimeout);
        Equal(37, result.ExitCode, "nonzero exit preserved");
        Equal(131072, result.Output.Length, "large stdout fully drained");
        Equal(131072, result.Error.Length, "large stderr fully drained concurrently");
        Check(result.Output.All(c => c == 'o') && result.Error.All(c => c == 'e'), "streams remain separate");
        Throws<InvalidOperationException>(result.EnsureSuccess, "nonzero result rejects success");
        Equal("output failure", new ProcessResult(2, " output failure \n", "").Failure, "stdout failure fallback");
        await ThrowsAsync<Win32Exception>(() => ProcessRunner.RunAsync(Path.Combine(_root, "missing.exe"), [], ShortTimeout), "missing executable");
        await ThrowsAsync<ArgumentOutOfRangeException>(() => ProcessRunner.RunAsync(_tool, [], TimeSpan.Zero), "invalid timeout rejected");
    }

    private static async Task TimeoutAsync()
    {
        var pidFile = NewPath("timeout.pid");
        await ThrowsAsync<TimeoutException>(() => ProcessRunner.RunAsync(_tool, ["--fake-wait", pidFile], TimeSpan.FromSeconds(1)), "timeout distinguished from cancellation");
        await CheckExitedAsync(pidFile, "timed-out client is reaped");
    }

    private static async Task CancellationAsync()
    {
        var pidFile = NewPath("cancel.pid");
        using var cancellation = new CancellationTokenSource();
        var task = ProcessRunner.RunAsync(_tool, ["--fake-wait", pidFile], ShortTimeout, cancellation.Token);
        await ReadPidAsync(pidFile);
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => task, "caller cancellation propagated");
        await CheckExitedAsync(pidFile, "cancelled client is reaped");
        var notStarted = NewPath("never-started.pid");
        await ThrowsAsync<OperationCanceledException>(() => ProcessRunner.RunAsync(_tool, ["--fake-wait", notStarted], ShortTimeout, cancellation.Token), "pre-cancellation propagated");
        Check(!File.Exists(notStarted), "pre-cancelled process never starts");
    }

    private static async Task DescendantAsync()
    {
        var parentPid = NewPath("parent.pid");
        var childPid = NewPath("descendant.pid");
        try
        {
            await ThrowsAsync<TimeoutException>(() => ProcessRunner.RunAsync(_tool,
                ["--fake-parent", parentPid, childPid], TimeSpan.FromSeconds(1.5)), "parent client times out");
            await CheckExitedAsync(parentPid, "parent was terminated");
            var child = await ReadPidAsync(childPid);
            Check(IsRunning(child), "descendant survives client cleanup (no entire-process-tree kill)");
        }
        finally
        {
            if (File.Exists(childPid))
            {
                using var child = Process.GetProcessById(await ReadPidAsync(childPid));
                if (!child.HasExited) child.Kill(entireProcessTree: false);
                await child.WaitForExitAsync();
            }
        }
    }

    private static async Task BinaryAsync()
    {
        var directory = NewDirectory("二进制 截图");
        var destination = Path.Combine(directory, "截图.png");
        await ProcessRunner.RunBinaryToFileAsync(_tool, ["--fake-binary"], destination, ShortTimeout);
        Check((await File.ReadAllBytesAsync(destination)).SequenceEqual(BinaryPayload()), "binary bytes are not text-decoded or newline-converted");
        await File.WriteAllTextAsync(destination, "old destination");
        await ProcessRunner.RunBinaryToFileAsync(_tool, ["--fake-binary"], destination, ShortTimeout);
        Check((await File.ReadAllBytesAsync(destination)).SequenceEqual(BinaryPayload()), "successful binary atomically replaces destination");
        Equal(1, Directory.GetFiles(directory).Length, "successful binary leaves no temporary file");
        var nested = Path.Combine(directory, "新目录", "capture.bin");
        await ProcessRunner.RunBinaryToFileAsync(_tool, ["--fake-binary"], nested, ShortTimeout);
        Check(File.Exists(nested), "new screenshot directory is created");
    }

    private static async Task BinaryFailureAsync()
    {
        var directory = NewDirectory("binary-failure");
        var destination = Path.Combine(directory, "capture.png");
        await ThrowsAsync<InvalidOperationException>(() => ProcessRunner.RunBinaryToFileAsync(_tool, ["--fake-binary-fail"], destination, ShortTimeout), "nonzero binary exit fails");
        Check(!File.Exists(destination), "failed capture never appears as a successful file");
        Equal(0, Directory.GetFiles(directory).Length, "failure removes temporary file");
        await File.WriteAllTextAsync(destination, "keep this existing image");
        await ThrowsAsync<InvalidOperationException>(() => ProcessRunner.RunBinaryToFileAsync(_tool, ["--fake-binary-fail"], destination, ShortTimeout), "replacement failure reported");
        Equal("keep this existing image", await File.ReadAllTextAsync(destination), "failure preserves previous destination");
        var empty = Path.Combine(directory, "empty.png");
        await ThrowsAsync<InvalidDataException>(() => ProcessRunner.RunBinaryToFileAsync(_tool, ["--fake-empty"], empty, ShortTimeout), "empty binary output is not a screenshot");
        Check(!File.Exists(empty), "empty output leaves no fake screenshot");
        Equal(1, Directory.GetFiles(directory).Length, "all failure temporary files cleaned");
        await ThrowsAsync<Win32Exception>(() => ProcessRunner.RunBinaryToFileAsync(Path.Combine(_root, "missing.exe"), [], empty, ShortTimeout), "binary missing executable");
        Equal(1, Directory.GetFiles(directory).Length, "startup failure cleans temporary file");
    }

    private static async Task BinaryCancellationAsync()
    {
        var directory = NewDirectory("binary-cancel");
        var destination = Path.Combine(directory, "capture.png");
        var timeoutPid = NewPath("binary-timeout.pid");
        await ThrowsAsync<TimeoutException>(() => ProcessRunner.RunBinaryToFileAsync(_tool, ["--fake-binary-slow", timeoutPid], destination, TimeSpan.FromSeconds(1)), "binary timeout reported");
        Check(!File.Exists(destination), "binary timeout leaves no destination");
        await CheckExitedAsync(timeoutPid, "binary timed-out client reaped");
        await File.WriteAllTextAsync(destination, "previous image");
        var cancelPid = NewPath("binary-cancel.pid");
        using var cancellation = new CancellationTokenSource();
        var task = ProcessRunner.RunBinaryToFileAsync(_tool, ["--fake-binary-slow", cancelPid], destination, ShortTimeout, cancellation.Token);
        await ReadPidAsync(cancelPid);
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => task, "binary caller cancellation propagated");
        await CheckExitedAsync(cancelPid, "binary cancelled client reaped");
        Equal("previous image", await File.ReadAllTextAsync(destination), "binary cancellation preserves existing image");
        Equal(1, Directory.GetFiles(directory).Length, "binary timeout/cancellation remove all temporary files");
    }

    private static async Task ServiceAsync()
    {
        var directory = NewDirectory("fake-adb-service");
        WriteState(directory, "");
        WriteFixtureText(Path.Combine(directory, "on-track-snapshot.txt"), "FIRST device usb:1-2 model:Handshake_Phone\n");
        using var environment = new EnvironmentScope("ADB_DOCK_CHECKS_STATE", directory);
        var snapshots = new ConcurrentQueue<DeviceSnapshot>();
        var service = new AdbService(_tool);
        service.SnapshotChanged += _ => throw new InvalidOperationException("fake view failure");
        service.SnapshotChanged += snapshots.Enqueue;
        try
        {
            Equal(_tool, service.ExecutablePath, "service exposes selected executable");
            await service.StartAsync();
            Check(snapshots.Any(snapshot => snapshot.IsAvailable && snapshot.Devices.Count == 0), "initial empty snapshot published");
            await UntilAsync(() => snapshots.Any(snapshot => snapshot.Devices.FirstOrDefault()?.Name == "Handshake Phone"),
                "initial track handshake triggers full devices -l refresh", TimeSpan.FromSeconds(1.8));
            Check(Latest(snapshots).IsAvailable, "subscriber exception does not stop discovery");
            await service.StartAsync();
            Equal(1, CommandFiles(directory, "version").Length, "repeated StartAsync is idempotent");
            Equal(1, CommandFiles(directory, "track-devices").Length, "only one listener started");

            WriteFixtureText(Path.Combine(directory, "delay.txt"), "40");
            WriteState(directory, "AUTH unauthorized usb:2-1\nSECOND offline\n");
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.RefreshAsync()));
            var current = Latest(snapshots);
            Equal(2, current.Devices.Count, "explicit refresh can be repeated");
            Equal(0, current.OnlineCount, "unavailable states not counted online");
            Check(!File.Exists(Path.Combine(directory, "overlap.flag")), "devices queries never execute concurrently");
            File.Delete(Path.Combine(directory, "delay.txt"));

            WriteState(directory, "!error\nfake adb unavailable");
            await service.RefreshAsync();
            current = Latest(snapshots);
            Check(!current.IsAvailable && current.Error!.Contains("fake adb unavailable"), "command failure is error snapshot");
            Equal(0, current.Devices.Count, "error does not retain stale green devices");
            WriteState(directory, "");
            await service.RefreshAsync();
            Check(Latest(snapshots).IsAvailable && Latest(snapshots).Devices.Count == 0, "successful empty refresh recovers from failure");

            WriteFixtureText(Path.Combine(directory, "quiet.txt"), "");
            WriteState(directory, "192.168.2.3:5555 device model:Polled_Phone\n");
            await UntilAsync(() => Latest(snapshots).Devices.FirstOrDefault()?.Name == "Polled Phone", "two-second fallback polling repairs missed notifications", TimeSpan.FromSeconds(4));
            File.Delete(Path.Combine(directory, "quiet.txt"));
            WriteFixtureText(Path.Combine(directory, "exit-track.txt"), "");
            await UntilAsync(() => snapshots.Any(snapshot => snapshot.Error?.Contains("监听") == true), "listener exit reports recovery state");
            File.Delete(Path.Combine(directory, "exit-track.txt"));
            await UntilAsync(() => CommandFiles(directory, "track-devices").Length >= 2 && Latest(snapshots).IsAvailable,
                "listener retries under control and devices recover");
            Check(CommandFiles(directory, "track-devices").Length <= 4, "listener failure does not busy-loop");

            WriteFixtureText(Path.Combine(directory, "delay.txt"), "3000");
            using var cancellation = new CancellationTokenSource();
            var refresh = service.RefreshAsync(cancellation.Token);
            await UntilAsync(() => Directory.GetFiles(directory, "*.active").Length > 0, "slow refresh running");
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => refresh, "explicit refresh cancellation propagated");
            File.Delete(Path.Combine(directory, "delay.txt"));
            await service.RefreshAsync();
            Check(Latest(snapshots).IsAvailable, "cancelled refresh releases serial gate");

            var commands = Directory.GetFiles(directory, "*.command").Select(ReadFixtureText).ToArray();
            Check(commands.All(command => command is "version" or "devices\n-l" or "track-devices"),
                "discovery never issues shell/device-writing/server-kill commands, including unauthorized devices");
        }
        finally { await service.DisposeAsync(); }
        await service.DisposeAsync();
        foreach (var pidFile in Directory.GetFiles(directory, "track.*.pid"))
            await CheckExitedAsync(pidFile, "DisposeAsync reaps owned listener");
        Check(Directory.GetFiles(directory, "*.active").All(file => !IsRunning(int.Parse(Path.GetFileNameWithoutExtension(file)))),
            "dispose leaves no running devices query");
        await ThrowsAsync<ObjectDisposedException>(() => service.RefreshAsync(), "refresh after disposal rejected");
        await ThrowsAsync<ObjectDisposedException>(() => service.StartAsync(), "start after disposal rejected");
    }

    private static async Task ServiceFailureAsync()
    {
        await using var service = new AdbService(Path.Combine(_root, "missing-adb.exe"));
        DeviceSnapshot? snapshot = null;
        service.SnapshotChanged += value => snapshot = value;
        await service.StartAsync();
        Check(snapshot is { IsAvailable: false }, "missing tool reported as failure snapshot");
        Check(!string.IsNullOrWhiteSpace(snapshot!.Error), "failure has actionable text");
        await service.RefreshAsync();
        await service.RefreshAsync();
        Check(snapshot is { IsAvailable: false }, "repeated failed refresh remains distinct from no devices");
    }

    private static Task SettingsAsync()
    {
        var directory = NewDirectory("settings JSON 中文");
        var store = new SettingsStore(directory);
        var defaults = store.Load();
        Check(defaults.Topmost && defaults.AutoCollapse && !defaults.AutoStart, "missing configuration uses safe defaults");
        Check(store.LoadError is null, "missing configuration is not corruption");
        var settings = new AppSettings
        {
            AdbPath = @"C:\工具 空格\adb.exe",
            ScrcpyPath = @"D:\镜像 工具\scrcpy.exe",
            ScreenshotDirectory = @"D:\截图 文件",
            Topmost = false,
            AutoCollapse = false,
            Pinned = true,
            AutoStart = true,
            Side = DockSide.Top,
            WindowX = -1250,
            WindowY = 240
        };
        store.Save(settings);
        Equal(JsonSerializer.Serialize(settings), JsonSerializer.Serialize(store.Load()), "all settings JSON round-trip");
        Check(!File.Exists(Path.Combine(directory, "settings.json.tmp")), "atomic save leaves no temporary file");
        const string corrupt = "{ damaged JSON ";
        File.WriteAllText(Path.Combine(directory, "settings.json"), corrupt);
        defaults = store.Load();
        Check(defaults.Topmost && defaults.AutoCollapse && !defaults.AutoStart, "corrupt configuration returns defaults without throwing");
        Check(!string.IsNullOrWhiteSpace(store.LoadError), "corrupt configuration exposes an error");
        Equal(corrupt, File.ReadAllText(Path.Combine(directory, "settings.json")), "load does not overwrite damaged configuration");
        store.Save(settings);
        var backups = Directory.GetFiles(directory, "settings.invalid-*.json");
        Equal(1, backups.Length, "saving after corruption preserves a backup");
        Equal(corrupt, File.ReadAllText(backups[0]), "backup preserves original damaged bytes");
        Equal(JsonSerializer.Serialize(settings), JsonSerializer.Serialize(store.Load()), "configuration recovers after explicit save");
        Check(store.LoadError is null, "successful recovery clears load error");
        File.WriteAllText(Path.Combine(directory, "settings.json"), "{\"AdbPath\":null,\"ScrcpyPath\":null,\"ScreenshotDirectory\":null,\"Side\":999}");
        var normalized = new SettingsStore(directory).Load();
        Equal("", normalized.AdbPath, "null adb path normalized");
        Equal("", normalized.ScrcpyPath, "null scrcpy path normalized");
        Check(!string.IsNullOrWhiteSpace(normalized.ScreenshotDirectory), "null screenshot directory normalized");
        Equal(DockSide.None, normalized.Side, "invalid docking value normalized");
        return Task.CompletedTask;
    }

    private static async Task<int> RunFakeAsync(string[] args)
    {
        switch (args[0])
        {
            case "--fake-report":
                Console.Write(JsonSerializer.Serialize(new FakeReport(args[1..], Environment.CurrentDirectory,
                    Environment.GetEnvironmentVariable("ADB_DOCK_CHECKS_ENV"))));
                return 0;
            case "--fake-flood":
                await Task.WhenAll(Console.Out.WriteAsync(new string('o', 131072)), Console.Error.WriteAsync(new string('e', 131072)));
                return 37;
            case "--fake-wait":
                WriteFixtureText(args[1], Environment.ProcessId.ToString());
                await Task.Delay(TimeSpan.FromSeconds(30));
                return 0;
            case "--fake-parent":
                WriteFixtureText(args[1], Environment.ProcessId.ToString());
                using (var child = ProcessRunner.Start(Environment.ProcessPath!, ["--fake-wait", args[2]]))
                    await Task.Delay(TimeSpan.FromSeconds(30));
                return 0;
            case "--fake-binary":
            case "--fake-binary-fail":
            case "--fake-binary-slow":
                await Console.OpenStandardOutput().WriteAsync(BinaryPayload());
                await Console.OpenStandardOutput().FlushAsync();
                if (args[0] == "--fake-binary-fail")
                {
                    Console.Error.Write("fake binary rejected");
                    return 9;
                }
                if (args[0] == "--fake-binary-slow")
                {
                    WriteFixtureText(args[1], Environment.ProcessId.ToString());
                    await Task.Delay(TimeSpan.FromSeconds(30));
                }
                return 0;
            case "--fake-empty": return 0;
            default: return 99;
        }
    }

    private static async Task<int> RunFakeAdbAsync(string[] args, string directory)
    {
        WriteFixtureText(Path.Combine(directory, $"{args[0]}.{Guid.NewGuid():N}.command"), string.Join('\n', args));
        switch (args[0])
        {
            case "version":
                Console.WriteLine("Android Debug Bridge version 1.0.41\nVersion SDK-only fake");
                return 0;
            case "devices":
                foreach (var stale in Directory.GetFiles(directory, "*.active"))
                    if (!IsRunning(int.Parse(Path.GetFileNameWithoutExtension(stale)))) File.Delete(stale);
                var active = Path.Combine(directory, $"{Environment.ProcessId}.active");
                WriteFixtureText(active, "");
                try
                {
                    if (Directory.GetFiles(directory, "*.active").Length > 1)
                        WriteFixtureText(Path.Combine(directory, "overlap.flag"), "");
                    var delayFile = Path.Combine(directory, "delay.txt");
                    if (int.TryParse(ReadOptionalFixtureText(delayFile), out var delay))
                        await Task.Delay(delay);
                    var snapshot = ReadFixtureText(Path.Combine(directory, "snapshot.txt"));
                    if (snapshot.StartsWith("!error\n", StringComparison.Ordinal))
                    {
                        Console.Error.Write(snapshot[7..]);
                        return 1;
                    }
                    Console.WriteLine("List of devices attached");
                    Console.Write(snapshot);
                    return 0;
                }
                finally { File.Delete(active); }
            case "track-devices":
                WriteFixtureText(Path.Combine(directory, $"track.{Environment.ProcessId}.pid"), Environment.ProcessId.ToString());
                var onTrack = Path.Combine(directory, "on-track-snapshot.txt");
                if (ReadOptionalFixtureText(onTrack) is { } handshake)
                {
                    WriteState(directory, handshake);
                    File.Delete(onTrack);
                }
                var last = ReadFixtureText(Path.Combine(directory, "snapshot.txt"));
                Console.WriteLine("0000");
                await Console.Out.FlushAsync();
                while (true)
                {
                    await Task.Delay(60);
                    if (File.Exists(Path.Combine(directory, "exit-track.txt")))
                    {
                        Console.Error.WriteLine("fake listener disconnected");
                        return 7;
                    }
                    var next = ReadFixtureText(Path.Combine(directory, "snapshot.txt"));
                    if (next != last && !File.Exists(Path.Combine(directory, "quiet.txt")))
                    {
                        Console.WriteLine($"{Encoding.UTF8.GetByteCount(next):x4}");
                        Console.WriteLine(next);
                        await Console.Out.FlushAsync();
                    }
                    last = next;
                }
            default: return 98;
        }
    }

    private static string PrepareFakeTool()
    {
        var directory = NewDirectory("中文 工具目录");
        var assembly = Assembly.GetExecutingAssembly().Location;
        var appHost = Path.ChangeExtension(assembly, OperatingSystem.IsWindows() ? ".exe" : null);
        if (!File.Exists(appHost)) throw new InvalidOperationException("Checks require the SDK-generated console apphost.");
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        var destination = Path.Combine(directory, OperatingSystem.IsWindows() ? "假 ADB 工具.exe" : "假 ADB 工具");
        File.Copy(appHost, destination);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, File.GetUnixFileMode(appHost));
        return destination;
    }

    private static byte[] BinaryPayload() => Enumerable.Range(0, 65536).Select(index => (byte)index).ToArray();
    private static string NewPath(string name) => Path.Combine(_root, name);
    private static string NewDirectory(string name)
    {
        var path = NewPath(name);
        Directory.CreateDirectory(path);
        return path;
    }
    private static string[] CommandFiles(string directory, string command) => Directory.GetFiles(directory, command + ".*.command");
    private static DeviceSnapshot Latest(ConcurrentQueue<DeviceSnapshot> snapshots) => snapshots.Last();
    private static async Task<int> ReadPidAsync(string file)
    {
        var pid = 0;
        // Do not cancel the child until its atomically published PID marker is readable.
        await UntilAsync(() =>
        {
            try { return int.TryParse(ReadFixtureText(file), out pid) && pid > 0; }
            catch (IOException) { return false; }
        }, "complete PID marker: " + Path.GetFileName(file));
        return pid;
    }
    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
    private static async Task CheckExitedAsync(string file, string message)
    {
        var pid = await ReadPidAsync(file);
        await UntilAsync(() => !IsRunning(pid), message);
    }
    private static void WriteState(string directory, string value)
        => WriteFixtureText(Path.Combine(directory, "snapshot.txt"), value);

    private static string ReadFixtureText(string path)
    {
        // Readers retain a consistent opened version while the test controller replaces or
        // deletes the published pathname. Default ReadAllText sharing would block Windows rename.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string? ReadOptionalFixtureText(string path)
    {
        try { return ReadFixtureText(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void WriteFixtureText(string path, string value)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".state");
        try
        {
            File.WriteAllText(temporary, value, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static async Task UntilAsync(Func<bool> predicate, string message, TimeSpan? timeout = null)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.Elapsed > (timeout ?? TimeSpan.FromSeconds(6))) throw new InvalidOperationException("Timed out: " + message);
            await Task.Delay(20);
        }
        Check(true, message);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
        _assertions++;
    }
    private static void Equal<T>(T expected, T actual, string message) => Check(EqualityComparer<T>.Default.Equals(expected, actual),
        $"{message}; expected [{expected}], actual [{actual}]");
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { Check(true, message); return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}: {message}");
    }
    private static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { Check(true, message); return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}: {message}");
    }
    private sealed record FakeReport(string[] Arguments, string WorkingDirectory, string? Environment);
    private sealed class EnvironmentScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _original;
        public EnvironmentScope(string name, string? value)
        {
            _name = name;
            _original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
        public void Dispose() => Environment.SetEnvironmentVariable(_name, _original);
    }
}
