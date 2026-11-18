using System.Text;

public static class UiAdbFixture
{
    public static async Task<int?> RunAsync(string[] args)
    {
        var snapshot = Environment.GetEnvironmentVariable("ADBDOCK_UI_SNAPSHOT");
        if (string.IsNullOrEmpty(snapshot)) return null;
        if (args.Length == 0) return null;
        if (Environment.GetEnvironmentVariable("ADBDOCK_UI_CLIENTS") is { Length: > 0 } clientDirectory)
        {
            Directory.CreateDirectory(clientDirectory);
            using var current = System.Diagnostics.Process.GetCurrentProcess();
            var marker = Path.Combine(clientDirectory, current.Id + ".pid");
            File.WriteAllText(marker + ".tmp",
                current.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.Move(marker + ".tmp", marker, true);
        }
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args[0] == "version") { Console.WriteLine("Android Debug Bridge version 1.0.41\nVersion UI-TEST-FIXTURE"); return 0; }
        if (args[0] == "--version") { Console.WriteLine("scrcpy UI-TEST-FIXTURE"); return 0; }
        if (args[0] == "devices")
        {
            var text = await ReadSnapshotAsync(snapshot);
            if (text.StartsWith("ERROR:", StringComparison.Ordinal)) { Console.Error.WriteLine(text[6..]); return 1; }
            Console.WriteLine("List of devices attached"); Console.WriteLine(text); return 0;
        }
        if (args[0] == "track-devices")
        {
            string? previous = null;
            while (true)
            {
                var current = await ReadSnapshotAsync(snapshot);
                if (current != previous) { Console.WriteLine(current); Console.WriteLine(); await Console.Out.FlushAsync(); previous = current; }
                await Task.Delay(200);
            }
        }
        if (args[0] != "-s" || args.Length < 3) return null;
        if (args[2] == "shell" && args.Length >= 5 && args[3] == "getprop")
        {
            Console.WriteLine(args[4] switch { "ro.product.model" => "UI Fixture Phone " + args[1], "ro.product.manufacturer" => "Test Only", "ro.build.version.release" => "15", "ro.build.version.sdk" => "35", _ => "" });
            return 0;
        }
        if (args[2] == "shell" && args.Length >= 5 && args[3] == "cat")
        {
            if (args[4] == "/proc/stat")
            {
                var ticks = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 10;
                Console.WriteLine($"cpu  {ticks * 2 + 100} 0 {ticks + 50} {ticks * 7 + 200} 0 0 0 0 0 0");
                return 0;
            }
            if (args[4] == "/proc/meminfo")
            {
                Console.WriteLine("MemTotal: 8192000 kB\nMemAvailable: 3072000 kB\nMemFree: 1024000 kB");
                return 0;
            }
        }
        if (args[2] == "shell" && args.Length >= 5 && args[3] == "dumpsys" && args[4] == "battery")
        {
            Console.WriteLine("Current Battery Service state:\n  level: 75\n  scale: 100\n  temperature: 315\n  voltage: 4100\n  status: 2");
            return 0;
        }
        if (args[2] == "logcat")
        {
            for (var i = 0; ; i++) { Console.WriteLine($"11-17 15:00:00.000 100 100 I UiFixture: {args[1]} test log {i}"); await Console.Out.FlushAsync(); await Task.Delay(150); }
        }
        Console.Error.WriteLine("UI fixture refuses device writes and mirror startup.");
        return 1;
    }

    private static async Task<string> ReadSnapshotAsync(string path)
    {
        // The UI runner atomically replaces this file while the listener is reading it.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}
