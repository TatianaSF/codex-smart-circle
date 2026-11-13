using System.Globalization;
using System.Text.Json;

namespace CodexSmartCircle.Core;

public sealed record UsageWindow(int RemainingPercent, DateTimeOffset? ResetsAtUtc, int DurationMinutes);
public sealed record UsageSnapshot(UsageWindow? FiveHour, UsageWindow? SevenDay, string? UnavailableReason, string? QuotaScope = null)
{
    public static UsageSnapshot Unavailable(string reason) => new(null, null, reason);
}

public static class RateLimitParser
{
    public static UsageSnapshot Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("result", out var result)) root = result;
        var windows = new List<UsageWindow>();
        if (root.TryGetProperty("rateLimits", out var limits)) AddCandidates(limits, windows);
        if (root.TryGetProperty("rateLimitsByLimitId", out var byId))
            foreach (var item in byId.EnumerateObject()) AddCandidates(item.Value, windows);
        var five = SelectWindow(windows, 300, "five-hour");
        var week = SelectWindow(windows, 10080, "weekly");
        if (five.Ambiguous || week.Ambiguous)
            return UsageSnapshot.Unavailable(string.Join(" ", new[] { five, week }.Where(x => x.Ambiguous).Select(x => x.Error)));
        var fiveWindow = five.Window;
        var weekWindow = week.Window;
        return fiveWindow is null && weekWindow is null ? UsageSnapshot.Unavailable("No 300-minute or 10080-minute window was reported.") : new UsageSnapshot(fiveWindow, weekWindow, null);
    }

    private static (UsageWindow? Window, bool Ambiguous, string Error) SelectWindow(List<UsageWindow> windows, int duration, string label)
    {
        var candidates = windows.Where(x => x.DurationMinutes == duration)
            .DistinctBy(x => (x.RemainingPercent, x.ResetsAtUtc, x.DurationMinutes)).ToArray();
        return candidates.Length switch
        {
            0 => (null, false, string.Empty),
            1 => (candidates[0], false, string.Empty),
            _ => (null, true, $"Ambiguous {label} quota data; values are hidden.")
        };
    }

    private static void AddCandidates(JsonElement element, List<UsageWindow> output)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        foreach (var name in new[] { "primary", "secondary" })
            if (element.TryGetProperty(name, out var w) && w.ValueKind == JsonValueKind.Object &&
                w.TryGetProperty("windowDurationMins", out var duration) && duration.TryGetInt32(out var mins) &&
                w.TryGetProperty("usedPercent", out var used) && used.TryGetInt32(out var percent) && percent is >= 0 and <= 100)
            {
                DateTimeOffset? reset = null;
                if (w.TryGetProperty("resetsAt", out var r) && r.TryGetInt64(out var epoch) && epoch > 0)
                    try { reset = DateTimeOffset.FromUnixTimeSeconds(epoch); } catch (ArgumentOutOfRangeException) { }
                output.Add(new UsageWindow(100 - percent, reset, mins));
            }
    }

    public static string FormatReset(DateTimeOffset? utc, TimeZoneInfo? zone = null)
    {
        if (utc is null) return "reset time unavailable";
        var local = TimeZoneInfo.ConvertTime(utc.Value, zone ?? TimeZoneInfo.Local);
        return local.ToString("HH:mm", CultureInfo.InvariantCulture);
    }
}

public static class WidgetMenuCommands
{
    public static IReadOnlyList<string> OrderedLabels { get; } = ["Show", "Refresh", "Settings", "Reset Position", "|", "Exit"];
}

public sealed record RefreshDiagnostics(DateTimeOffset? LastSuccessUtc, string? Trigger, string ConnectionState, string? ErrorCategory)
{
    public string FormatStatus(DateTimeOffset nowUtc)
    {
        var age = LastSuccessUtc is { } last ? $"Last updated {Math.Max(0, (int)(nowUtc - last).TotalMinutes)} min ago" : "No successful refresh yet";
        return $"{ConnectionState}; {age}" + (ErrorCategory is null ? string.Empty : $"; last error: {ErrorCategory}") + (Trigger is null ? string.Empty : $"; trigger: {Trigger}");
    }
}

public static class OverlayPlacement
{
    public static (double Left, double Top) Calculate(double windowLeft, double windowTop, double windowWidth, double windowHeight, double dpiScale, double diameter = 112)
    {
        var scale = Math.Max(0.5, dpiScale);
        return (windowLeft / scale + 38 - diameter / 2, windowTop / scale + windowHeight / scale - 165 - diameter / 2);
    }
}

public static class WindowClassification
{
    public static bool IsCodex(string processName, string title) =>
        processName.Equals("Codex", StringComparison.OrdinalIgnoreCase) ||
        (processName.Equals("codex", StringComparison.OrdinalIgnoreCase) && title.Contains("Codex", StringComparison.OrdinalIgnoreCase)) ||
        (processName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) && title.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase));
}

public enum WidgetStyle { Circle, RoundedSquare, VerticalPill, HorizontalBar }
public static class WidgetLayout
{
    public const double DefaultScale = 2.0;
    public const double MinimumScale = 0.5;
    public const double MaximumScale = 2.5;
    public static double ClampScale(double scale) => double.IsFinite(scale) ? Math.Clamp(scale, MinimumScale, MaximumScale) : DefaultScale;
    public static (double Width, double Height) DimensionsFor(WidgetStyle style, double scale) { var s=ClampScale(scale); var b=SizeFor(style); return (b.Width*s,b.Height*s); }
    public static (double Width, double Height) SizeFor(WidgetStyle style) => style switch
    {
        WidgetStyle.Circle => (90, 90),
        WidgetStyle.RoundedSquare => (60, 60),
        WidgetStyle.VerticalPill => (48, 80),
        WidgetStyle.HorizontalBar => (100, 42),
        _ => throw new ArgumentOutOfRangeException(nameof(style))
    };
}
public static class WidgetInteractionPolicy
{
    public static bool CanStartDrag(bool isLeftButton) => isLeftButton;
}
public sealed class WidgetSettings
{
    public int SchemaVersion { get; set; } = 1;
    public WidgetStyle SelectedStyle { get; set; }
    public Dictionary<WidgetStyle, WidgetPosition> Positions { get; set; } = new();
    public Dictionary<WidgetStyle, double> StyleScales { get; set; } = new();
    public double GetScale(WidgetStyle style) => StyleScales.TryGetValue(style, out var scale) ? WidgetLayout.ClampScale(scale) : WidgetLayout.DefaultScale;
    public void SetScale(WidgetStyle style, double scale) => StyleScales[style] = WidgetLayout.ClampScale(scale);
    public WidgetPosition GetPosition(WidgetStyle style) => Positions.TryGetValue(style, out var p) && double.IsFinite(p.X) && double.IsFinite(p.Y) ? p : new WidgetPosition();
    public void SetPosition(WidgetStyle style, double x, double y) { if (!double.IsFinite(x) || !double.IsFinite(y)) return; Positions[style] = new WidgetPosition { X = x, Y = y }; }
    public void ResetPosition(WidgetStyle style) => Positions[style] = new WidgetPosition();
    public void Select(WidgetStyle style) { SelectedStyle = style; }
}
public sealed class WidgetPosition { public double X { get; set; } public double Y { get; set; } }
public static class WidgetSettingsStore
{
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexSmartCircle", "settings.json");
    public static WidgetSettings Load(string? path = null)
    {
        try { return ParseOrDefault(File.ReadAllText(path ?? FilePath)); } catch { return new(); }
    }
    public static WidgetSettings ParseOrDefault(string json)
    { try { var s=JsonSerializer.Deserialize<WidgetSettings>(json) ?? new(); if(s.SchemaVersion!=1 || !Enum.IsDefined(s.SelectedStyle) || s.Positions is null) return new(); s.StyleScales ??= new(); foreach(var p in s.Positions.Values) if(p is null || !double.IsFinite(p.X) || !double.IsFinite(p.Y)) return new(); foreach(var k in s.StyleScales.Keys.ToArray()) s.StyleScales[k]=WidgetLayout.ClampScale(s.StyleScales[k]); return s; } catch { return new(); } }
    public static void Save(WidgetSettings settings, string? path = null)
    {
        var target = path ?? FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings));
        File.Move(temp, target, true);
    }
}
