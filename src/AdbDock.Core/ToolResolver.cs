namespace AdbDock.Core;

public static class ToolResolver
{
    public static string Resolve(string configuredPath, string executableName)
    {
        ArgumentNullException.ThrowIfNull(configuredPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);
        var relativeBundle = executableName.ToLowerInvariant() switch
        {
            "adb.exe" => Path.Combine("tools", "platform-tools", "adb.exe"),
            "scrcpy.exe" => Path.Combine("tools", "scrcpy", "scrcpy.exe"),
            _ => throw new ArgumentException("仅支持 adb.exe 或 scrcpy.exe。", nameof(executableName))
        };

        var configured = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var candidate = Path.GetFullPath(configured);
            if (Directory.Exists(candidate)) candidate = Path.Combine(candidate, executableName);
            if (File.Exists(candidate)) return candidate;
            // A mistaken explicit setting must not silently select an unrelated PATH installation.
            throw new FileNotFoundException($"指定的 {executableName} 不存在，请检查设置中的工具路径：{candidate}", candidate);
        }

        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var directory = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
            if (directory.Length == 0) continue;
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(directory, executableName));
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* Ignore a malformed unrelated PATH entry. */ }
            catch (NotSupportedException) { }
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, relativeBundle);
        if (File.Exists(bundled)) return Path.GetFullPath(bundled);
        throw new FileNotFoundException($"未找到 {executableName}。请在设置中指定路径，或将工具加入 PATH。", bundled);
    }
}
