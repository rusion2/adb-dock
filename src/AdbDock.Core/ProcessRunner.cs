using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AdbDock.Core;

public static class ProcessRunner
{
    public static Process Start(string executable, IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        var executablePath = GetExecutablePath(executable);
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument);
            startInfo.ArgumentList.Add(argument);
        }
        if (environment is not null)
            foreach (var (name, value) in environment)
                startInfo.Environment[name] = value;

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"无法启动程序：{executable}");
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ValidateTimeout(timeout);
        cancellationToken.ThrowIfCancellationRequested();
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        using var process = Start(executable, arguments, environment);
        var output = process.StandardOutput.ReadToEndAsync(operation.Token);
        var error = process.StandardError.ReadToEndAsync(operation.Token);
        var exited = process.WaitForExitAsync(operation.Token);
        try
        {
            await CompleteAsync(exited, output, error).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            return new ProcessResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            operation.Cancel();
            await StopClientAsync(process).ConfigureAwait(false);
            await ObserveAsync(exited, output, error).ConfigureAwait(false);
            if (exception is OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (timeoutSource.IsCancellationRequested)
                    throw new TimeoutException($"程序执行超时（{timeout.TotalSeconds:0.##} 秒）：{executable}", exception);
            }
            throw;
        }
    }

    public static Task RunBinaryToFileAsync(string executable, IEnumerable<string> arguments,
        string destination, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        RunBinaryToFileAsync(executable, arguments, destination, timeout, cancellationToken, validate: null);

    internal static async Task RunBinaryToFileAsync(string executable, IEnumerable<string> arguments,
        string destination, TimeSpan timeout, CancellationToken cancellationToken, Action<Stream, CancellationToken>? validate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ValidateTimeout(timeout);
        cancellationToken.ThrowIfCancellationRequested();
        var fullDestination = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(fullDestination)!;
        Directory.CreateDirectory(directory);
        // A sibling temporary file makes the final rename atomic, including when replacing a screenshot.
        var temporary = Path.Combine(directory, $".{Path.GetFileName(fullDestination)}.{Guid.NewGuid():N}.tmp");
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await using var file = new FileStream(temporary, FileMode.CreateNew, validate is null ? FileAccess.Write : FileAccess.ReadWrite,
                FileShare.None, 81920, FileOptions.Asynchronous);
            using var process = Start(executable, arguments);
            var output = process.StandardOutput.BaseStream.CopyToAsync(file, operation.Token);
            var error = process.StandardError.ReadToEndAsync(operation.Token);
            var exited = process.WaitForExitAsync(operation.Token);
            try
            {
                await CompleteAsync(exited, output, error).ConfigureAwait(false);
                new ProcessResult(process.ExitCode, "", await error.ConfigureAwait(false)).EnsureSuccess();
                if (file.Length == 0) throw new InvalidDataException("程序未返回任何二进制数据，文件未保存。");
                await file.FlushAsync(operation.Token).ConfigureAwait(false);
                if (validate is not null)
                {
                    file.Position = 0;
                    validate(file, operation.Token);
                }
                await file.DisposeAsync().ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested();
                File.Move(temporary, fullDestination, overwrite: true);
            }
            catch (Exception exception)
            {
                operation.Cancel();
                await StopClientAsync(process).ConfigureAwait(false);
                await ObserveAsync(exited, output, error).ConfigureAwait(false);
                if (exception is OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (timeoutSource.IsCancellationRequested)
                        throw new TimeoutException($"二进制文件读取超时（{timeout.TotalSeconds:0.##} 秒），文件未保存。", exception);
                }
                throw;
            }
        }
        finally
        {
            // Never remove or truncate the requested destination on failure.
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static async Task StopClientAsync(Process process)
    {
        try
        {
            // adb may have started a shared server: only terminate this client, never its descendants.
            if (!process.HasExited) process.Kill(entireProcessTree: false);
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
        catch (OperationCanceledException) { }
    }

    internal static async Task ObserveAsync(params Task[] tasks)
    {
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch { /* The original command/timeout exception is the actionable failure. */ }
    }

    private static async Task CompleteAsync(params Task[] tasks)
    {
        // Task.WhenAll alone delays an I/O failure until the process exits (which can deadlock a
        // producer on a full pipe). Surface the first failure so the caller can stop its client.
        var pending = tasks.ToList();
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
            pending.Remove(completed);
        }
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout != Timeout.InfiniteTimeSpan && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "超时时间必须大于零，或为无限等待。");
    }

    private static string GetExecutablePath(string executable)
    {
        if (Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar) ||
            executable.Contains(Path.AltDirectorySeparatorChar) || File.Exists(executable))
            return Path.GetFullPath(executable);

        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var directory = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
            if (directory.Length == 0) continue;
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(directory, executable));
                if (File.Exists(candidate)) return candidate;
                if (OperatingSystem.IsWindows() && !Path.HasExtension(executable) && File.Exists(candidate + ".exe"))
                    return candidate + ".exe";
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }
        throw new FileNotFoundException($"未找到可执行程序：{executable}", executable);
    }
}
