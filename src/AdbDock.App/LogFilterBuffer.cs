using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AdbDock.App;

public sealed class LogFilterBuffer
{
    public const int MaximumLines = 20000;
    public const int MaximumLineLength = 8000;
    public const int MaximumCharacters = 16 * 1024 * 1024;
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(25);
    public static readonly TimeSpan RefilterBudget = TimeSpan.FromMilliseconds(150);

    private sealed class LogLine(string text)
    {
        public string Text { get; } = text;
        public bool IsVisible { get; set; }
        public bool IsRetained { get; set; } = true;
    }

    private readonly Queue<LogLine> raw = new();
    private readonly int capacity;
    private readonly int maxLineLength;
    private readonly int maxCharacters;
    private List<string> visible = new();
    private Regex? expression;
    private string requestedPattern = "";
    private bool hasRequestedPattern;

    public LogFilterBuffer(int capacity = MaximumLines, int maxLineLength = MaximumLineLength, int maxCharacters = MaximumCharacters)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maxLineLength <= 0) throw new ArgumentOutOfRangeException(nameof(maxLineLength));
        if (maxCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        this.capacity = capacity;
        this.maxLineLength = Math.Min(maxLineLength, maxCharacters);
        this.maxCharacters = maxCharacters;
    }

    public int TotalCount => raw.Count;
    public int TotalCharacters { get; private set; }
    public IReadOnlyList<string> VisibleLines => visible;
    public string ActivePattern { get; private set; } = "";
    public string? FilterError { get; private set; }
    public bool IsFilterSuspended { get; private set; }
    internal int LastRemovedVisibleLines { get; private set; }
    internal IReadOnlyList<string> LastAppendedVisibleLines { get; private set; } = Array.Empty<string>();

    public bool SetFilter(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        // A suspended expression is retried only after the input actually changes.
        if (hasRequestedPattern && string.Equals(requestedPattern, pattern, StringComparison.Ordinal))
            return FilterError is null && !IsFilterSuspended;
        requestedPattern = pattern;
        hasRequestedPattern = true;

        Regex? candidate;
        try { candidate = pattern.Length == 0 ? null : new Regex(pattern, RegexOptions.None, MatchTimeout); }
        catch (ArgumentException exception)
        {
            var reason = exception.Message.Length <= 180 ? exception.Message : exception.Message[..180] + "…";
            FilterError = "正则表达式无效：" + reason + "；当前保留上次有效筛选。";
            return false;
        }

        if (!TryFilter(candidate, raw, out var matches)) return false;
        expression = candidate;
        ActivePattern = pattern;
        foreach (var line in raw) line.IsVisible = false;
        visible = new List<string>(matches.Count);
        foreach (var line in matches) { line.IsVisible = true; visible.Add(line.Text); }
        FilterError = null;
        IsFilterSuspended = false;
        return true;
    }

    public bool Append(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return Append(new[] { line });
    }

    public bool Append(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        LastRemovedVisibleLines = 0;
        LastAppendedVisibleLines = Array.Empty<string>();
        var pending = new List<LogLine>();
        var removedVisible = 0;
        foreach (var line in lines)
        {
            ArgumentNullException.ThrowIfNull(line);
            var retained = new LogLine(line.Length > maxLineLength ? line[..maxLineLength] : line);
            raw.Enqueue(retained);
            pending.Add(retained);
            TotalCharacters += retained.Text.Length;
            while (raw.Count > capacity || TotalCharacters > maxCharacters)
            {
                var removed = raw.Dequeue();
                removed.IsRetained = false;
                TotalCharacters -= removed.Text.Length;
                if (removed.IsVisible) removedVisible++;
            }
        }
        if (pending.Count == 0 || IsFilterSuspended) return false;
        // Match only new retained lines. Refiltering the entire 20,000-line cache
        // is reserved for an explicit expression change, not every stream tick.
        if (!TryFilter(expression, pending, out var matches)) return false;
        LastRemovedVisibleLines = Math.Min(removedVisible, visible.Count);
        if (LastRemovedVisibleLines > 0) visible.RemoveRange(0, LastRemovedVisibleLines);
        var addedVisible = new List<string>(matches.Count);
        foreach (var line in matches) { line.IsVisible = true; addedVisible.Add(line.Text); }
        visible.AddRange(addedVisible);
        LastAppendedVisibleLines = addedVisible;
        return true;
    }

    private bool TryFilter(Regex? candidate, IEnumerable<LogLine> lines, out List<LogLine> matches)
    {
        var result = new List<LogLine>();
        if (candidate is null)
        {
            foreach (var line in lines) if (line.IsRetained) result.Add(line);
            matches = result;
            return true;
        }
        var watch = Stopwatch.StartNew();
        try
        {
            foreach (var line in lines)
            {
                if (!line.IsRetained) continue;
                if (watch.Elapsed >= RefilterBudget) return Suspend(out matches);
                if (candidate.IsMatch(line.Text)) result.Add(line);
            }
            if (watch.Elapsed >= RefilterBudget) return Suspend(out matches);
            matches = result;
            return true;
        }
        catch (RegexMatchTimeoutException) { return Suspend(out matches); }
    }

    private bool Suspend(out List<LogLine> matches)
    {
        matches = new List<LogLine>();
        IsFilterSuspended = true;
        FilterError = "正则筛选耗时过长，已暂停筛选。请简化表达式，修改后重试。";
        return false;
    }
}
