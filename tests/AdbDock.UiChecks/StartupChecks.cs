using System.IO;
using System.Text.Json;
using AdbDock.App;
using Microsoft.Win32;

// Runs before Application initialization: independent of the production singleton and GUI.
internal static class StartupChecks
{
    public static int Run(string[] args)
    {
        var directory = Path.GetFullPath(args.ElementAtOrDefault(0) ?? Path.Combine("artifacts", "startup-checks"));
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "安装 路径", "AdbDock.exe");
        var other = Path.Combine(directory, "另一版本", "AdbDock.exe");
        var testPath = @"Software\AdbDock.UiChecks\" + Guid.NewGuid().ToString("N");
        var success = false;
        string? failure = null;
        try
        {
            Require(Desktop.StartupCommandTargetsExecutable('"' + executable + "\" --test", executable), "quoted Unicode path with arguments did not match");
            Require(Desktop.StartupCommandTargetsExecutable('"' + executable.ToUpperInvariant() + '"', executable), "case-insensitive executable match failed");
            Require(!Desktop.StartupCommandTargetsExecutable('"' + other + '"', executable), "other copy matched");
            Require(!Desktop.StartupCommandTargetsExecutable("cmd.exe /c \"" + executable + '"', executable), "shell wrapper matched executable");
            Require(!Desktop.StartupCommandTargetsExecutable("AdbDock.exe", executable), "relative command matched");
            using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            try
            {
                using var test = currentUser.CreateSubKey(testPath, true);
                using var run = test.CreateSubKey("Run", true);
                using var approval = test.CreateSubKey("StartupApproved", true);
                run.SetValue("OtherApp", "untouched");
                Require(!Desktop.IsStartupEnabled(run, approval, executable), "missing entry reported enabled");
                Desktop.ConfigureStartup(true, run, approval, executable);
                Equal('"' + executable + '"', run.GetValue("AdbDock") as string);
                Require(Desktop.IsStartupEnabled(run, approval, executable), "new registration not detected");
                foreach (var flag in new byte[] { 3, 7 })
                {
                    var disabled = new byte[12]; disabled[0] = flag;
                    approval.SetValue("AdbDock", disabled, RegistryValueKind.Binary);
                    Require(!Desktop.IsStartupEnabled(run, approval, executable), "Task Manager disable ignored");
                    Require(((byte[])approval.GetValue("AdbDock")!).SequenceEqual(disabled), "read-only detection changed approval");
                }
                Desktop.ConfigureStartup(true, run, approval, executable);
                Require(approval.GetValue("AdbDock") is null && Desktop.IsStartupEnabled(run, approval, executable), "explicit enable did not clear disabled state");
                var otherCommand = '"' + other + '"'; run.SetValue("AdbDock", otherCommand);
                approval.SetValue("AdbDock", new byte[] { 3, 0, 0, 0 }, RegistryValueKind.Binary);
                Desktop.ConfigureStartup(false, run, approval, executable);
                Equal(otherCommand, run.GetValue("AdbDock") as string);
                Equal((byte)3, ((byte[])approval.GetValue("AdbDock")!)[0]);
                Equal("untouched", run.GetValue("OtherApp") as string);
                Desktop.ConfigureStartup(false, run, approval, other);
                Require(run.GetValue("AdbDock") is null, "matching registration was not removed");
            }
            finally { currentUser.DeleteSubKeyTree(testPath, false); }
            success = true;
            Console.WriteLine("PASS Startup command parsing, disabled-state detection and ownership-safe registry changes (isolated HKCU key)");
            Console.WriteLine("Startup checks: 1 passed, 0 failed; real startup registration unchanged.");
            return 0;
        }
        catch (Exception exception)
        {
            failure = exception.ToString();
            Console.Error.WriteLine("FAIL Startup checks: " + failure);
            return 1;
        }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "startup-result.json"), JsonSerializer.Serialize(new
            {
                success, failure, temporaryRegistryKey = @"HKCU\" + testPath,
                realStartupRegistrationChanged = false, checkedAt = DateTimeOffset.Now
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Require(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected '{expected}', got '{actual}'.");
}
