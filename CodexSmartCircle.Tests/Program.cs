using CodexSmartCircle.Core;
using CodexSmartCircle;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO;
using System.Threading;

var checks = 0;
void Check(bool value, string name) { checks++; if (!value) throw new Exception($"FAIL: {name}"); Console.WriteLine($"PASS: {name}"); }
var quotaFixture = """{"result":{"accountId":"private-account","rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":20,"windowDurationMins":300},"secondary":{"usedPercent":40,"windowDurationMins":10080}}}}}""";
var quotaDiagnostic = QuotaDiagnostics.Parse(quotaFixture);
Check(quotaDiagnostic.RpcSucceeded && quotaDiagnostic.AccountIdState == "present" && quotaDiagnostic.LimitIdsState == "present" && quotaDiagnostic.FiveHourState == "valid" && quotaDiagnostic.WeeklyState == "valid", "V1 quota dictionary and windows recognized");
Check(QuotaDiagnostics.Parse("""{"result":{"accountId":null,"rateLimitsByLimitId":{}}}""").AccountIdState == "null", "nullable accountId recognized");
Check(QuotaDiagnostics.Parse("""{"result":{"rateLimitsByLimitId":{"a":{"primary":{"usedPercent":101,"windowDurationMins":300}}}}}""").FiveHourState == "missing", "invalid quota percent rejected");
Check(!QuotaDiagnostics.Parse("malformed").RpcSucceeded, "malformed quota response fails safely");
Check(!JsonSerializer.Serialize(quotaDiagnostic).Contains("private-account", StringComparison.Ordinal), "quota diagnostics exclude raw account identifier");
var acct = AccountIdentity.Parse("""{"result":{"account":{"type":"chatgpt","userId":"synthetic-user","workspaceName":"Synthetic"}}}""", DateTimeOffset.UtcNow);
Check(acct.Status == AccountIdentityStatus.PendingQuotaScope && acct.AccountType == "chatgpt" && !acct.UserIdProvided && acct.PrivateScopeKey is null, "V1-style account response does not require or invent userId");
var currentAccount = AccountIdentity.Parse("""{"result":{"account":{"type":"chatgpt","email":"synthetic@example.invalid","planType":"plus","workspaceName":null},"requiresOpenaiAuth":false}}""", DateTimeOffset.UtcNow);
Check(currentAccount.Status == AccountIdentityStatus.PendingQuotaScope && currentAccount.AccountType == "chatgpt" && !currentAccount.UserIdProvided, "current App Server account/read schema is accepted without userId");
Check(!currentAccount.QuotaScopeAvailable, "history waits for quota-scope verification");
Check(AccountIdentity.Parse("""{"result":{"account":{"type":"chatgpt","userId":null}}}""", DateTimeOffset.UtcNow).Status == AccountIdentityStatus.PendingQuotaScope, "null userId does not block account verification when quota scope can verify it");
Check(AccountIdentity.Parse("""{"result":{"account":{"type":"chatgpt"}}}""", DateTimeOffset.UtcNow).Status == AccountIdentityStatus.PendingQuotaScope, "missing userId does not block account verification when quota scope can verify it");
Check(AccountIdentity.Parse("""{"result":{"account":null}}""", DateTimeOffset.UtcNow).Status == AccountIdentityStatus.Unknown, "null account remains unknown");
Check(AccountIdentity.Parse("""{"error":{"code":-32601,"message":"method not found"}}""", DateTimeOffset.UtcNow).Status == AccountIdentityStatus.Unsupported, "unsupported account read is typed");
string QuotaScopeFixture(string accountId, string limitId) => JsonSerializer.Serialize(new { result = new { accountId, rateLimitsByLimitId = new Dictionary<string, object> { [limitId] = new { limitId, primary = new { usedPercent = 20, windowDurationMins = 300, resetsAt = 2000000000 }, secondary = new { usedPercent = 40, windowDurationMins = 10080, resetsAt = 2000003600 } } } } });
var scopeVerifier = new QuotaScopeVerifier();
var firstScope = scopeVerifier.Observe("chatgpt", QuotaScopeFixture("synthetic-account-A", "codex"));
Check(!firstScope.Verified && firstScope.ConsecutiveObservations == 1 && firstScope.ScopeKey is null, "first valid quota RPC is pending scope verification");
var secondScope = scopeVerifier.Observe("chatgpt", QuotaScopeFixture("synthetic-account-A", "codex"));
Check(secondScope.Verified && secondScope.ConsecutiveObservations == 2 && QuotaScopeVerifier.IsValidScopeKey(secondScope.ScopeKey), "two matching account and quota reads verify stable private scope");
Check(!secondScope.ScopeKey!.Contains("synthetic-account-A", StringComparison.Ordinal), "private scope key does not contain raw account identifier");
var switchedAccount = scopeVerifier.Observe("chatgpt", QuotaScopeFixture("synthetic-account-B", "codex"));
Check(!switchedAccount.Verified && switchedAccount.ConsecutiveObservations == 1, "account switch clears verified scope until confirmed twice");
var switchedAccountConfirmed = scopeVerifier.Observe("chatgpt", QuotaScopeFixture("synthetic-account-B", "codex"));
Check(switchedAccountConfirmed.Verified && switchedAccountConfirmed.ScopeKey != secondScope.ScopeKey, "account switch creates an isolated scope");
scopeVerifier.Reset();
_ = scopeVerifier.Observe("chatgpt", QuotaScopeFixture("synthetic-account-A", "codex"));
var firstDifferentBucket = scopeVerifier.Observe("chatgpt", QuotaScopeFixture("synthetic-account-A", "codex-enterprise"));
Check(!firstDifferentBucket.Verified && firstDifferentBucket.ConsecutiveObservations == 1, "quota bucket switch resets consecutive verification");
var confirmedDifferentBucket = scopeVerifier.Observe("chatgpt", QuotaScopeFixture("synthetic-account-A", "codex-enterprise"));
Check(confirmedDifferentBucket.Verified && confirmedDifferentBucket.ScopeKey != secondScope.ScopeKey, "quota bucket switch creates a distinct verified scope");
Check(!scopeVerifier.Observe("chatgpt", """{"result":{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":20,"windowDurationMins":300,"resetsAt":2000000000},"secondary":{"usedPercent":40,"windowDurationMins":10080,"resetsAt":2000003600}}}}}""").Verified, "missing rate-limit accountId cannot enable history");
var nullEmbeddedLimitId = QuotaScopeFixture("synthetic-account-A", "codex").Replace("\"limitId\":\"codex\"", "\"limitId\":null", StringComparison.Ordinal);
Check(QuotaDiagnostics.Parse(nullEmbeddedLimitId).Classification == "valid", "null embedded limitId safely falls back to the keyed quota bucket");
var mismatchedEmbeddedLimitId = QuotaScopeFixture("synthetic-account-A", "codex").Replace("\"limitId\":\"codex\"", "\"limitId\":\"different-bucket\"", StringComparison.Ordinal);
Check(QuotaDiagnostics.Parse(mismatchedEmbeddedLimitId).Classification == "ambiguous" && !new QuotaScopeVerifier().Observe("chatgpt", mismatchedEmbeddedLimitId).Verified, "inconsistent bucket key and embedded limitId fail closed");
Check(!new QuotaScopeVerifier().Observe("apiKey", QuotaScopeFixture("synthetic-account-A", "codex")).Verified, "non-ChatGPT account type cannot enable ChatGPT quota history");
var forecastNow = DateTimeOffset.UtcNow;
var f5Reset = forecastNow.AddHours(2); var fwReset = forecastNow.AddDays(3);
var syntheticScope = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("synthetic-account-A/codex")));
var otherSyntheticScope = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("synthetic-account-B/codex")));
ForecastObservation Obs(int mins, double f, double w, DateTimeOffset? r5 = null, string? scope = null, DateTimeOffset? rw = null) => new(forecastNow.AddMinutes(mins), f, w, r5 ?? f5Reset, rw ?? fwReset, scope ?? syntheticScope);
var forecast = UsageForecaster.Calculate(new[] { Obs(-60, 0, 10), Obs(-1, 80, 22) }, forecastNow);
Check(forecast.Availability == ForecastAvailability.Available && Math.Abs(forecast.WeeklyCostPerFullCycle!.Value - 15) < 0.001 && forecast.RemainingFullCycles == 5, "forecast matches original 80/12 formula");
Check(forecast.Confidence == ForecastConfidence.Low, "single-window estimate is low confidence");
Check(UsageForecaster.Calculate(new[] { Obs(-10, 40, 20), Obs(-1, 45, 20) }, forecastNow).Reason == ForecastUnavailableReason.InsufficientHistory, "forecast requires at least 10 accumulated five-hour usage points");
Check(UsageForecaster.Calculate(new[] { Obs(-60, 0, 20), Obs(-1, 15, 20) }, forecastNow).Reason == ForecastUnavailableReason.NoMeasurableConsumption, "forecast requires positive weekly consumption");
var forecastFormatter = typeof(MainWindow).GetMethod("GetForecastIndicatorText", BindingFlags.Static | BindingFlags.NonPublic)!;
string ForecastLabel(UsageForecast value, DateTimeOffset now) => (string)forecastFormatter.Invoke(null, [value, now])!;
Check(ForecastLabel(forecast, forecastNow) == "5", "available forecast binds its calculated integer");
Check(ForecastLabel(UsageForecast.Unavailable(ForecastUnavailableReason.InsufficientHistory), forecastNow) == "-", "unavailable forecast uses placeholder");
Check(ForecastLabel(forecast with { LastObservationUtc = forecastNow.AddMinutes(-3) }, forecastNow) == "-", "stale forecast is never displayed as current");
Check(ForecastLabel(forecast with { LastObservationUtc = forecastNow.AddMinutes(2) }, forecastNow) == "-", "future-dated forecast is rejected for display");
Check(UsageForecaster.Calculate(Array.Empty<ForecastObservation>(), forecastNow).Reason == ForecastUnavailableReason.InsufficientHistory, "empty history is unavailable");
Check(UsageForecaster.Calculate(new[] { Obs(-60, 0, 10), Obs(-1, 80, 22, f5Reset.AddMinutes(1)) }, forecastNow).Reason == ForecastUnavailableReason.ResetBoundary, "reset boundary is rejected");
Check(UsageForecaster.Calculate(new[] { Obs(-60, 0, 10), Obs(-1, 80, 22, rw: fwReset.AddMinutes(1)) }, forecastNow).Reason == ForecastUnavailableReason.ResetBoundary, "weekly reset boundary is rejected");
Check(UsageForecaster.Calculate(new[] { Obs(-60, 0, 10), Obs(-1, 80, 22, scope: otherSyntheticScope) }, forecastNow).Reason == ForecastUnavailableReason.IncompatibleAccount, "different quota scope is rejected");
Check(UsageForecaster.Calculate(new[] { Obs(-1000, 0, 10), Obs(-900, 80, 22) }, forecastNow).Reason == ForecastUnavailableReason.StaleData, "stale history is rejected");
Check(UsageForecaster.Calculate(new[] { Obs(-60, -1, 10), Obs(-1, 80, 22) }, forecastNow).Reason == ForecastUnavailableReason.InvalidData, "invalid quota samples are rejected");
var generationGate = new ForecastGenerationGate();
var oldForecastGeneration = generationGate.Advance();
var newestForecastGeneration = generationGate.Advance();
Check(!generationGate.IsCurrent(oldForecastGeneration) && generationGate.IsCurrent(newestForecastGeneration), "late async forecast cannot replace a newer snapshot");
var historyPath = Path.Combine(Path.GetTempPath(), $"csc-usage-{Guid.NewGuid():N}", "history.jsonl");
var store = new UsageHistoryStore(historyPath);
Check(await store.AppendAsync(Obs(-2, 40, 30), forecastNow), "history snapshot persists");
Check((await store.LoadAsync(forecastNow)).Count == 1, "history snapshot reloads");
Check(!(await store.AppendAsync(Obs(-1, 40, 30), forecastNow)), "duplicate quota observations are rejected");
Check(!(await store.AppendAsync(Obs(-3, 39, 29), forecastNow)), "out-of-order quota observations are rejected");
Check(!(await store.AppendAsync(Obs(-1, 41, 31, scope: "codex"), forecastNow)), "unscoped legacy label is rejected for new history");
Check((await store.LoadForScopeAsync(otherSyntheticScope, forecastNow)).Count == 0, "history query never mixes another account scope");
Check((await store.LoadForScopeAsync(syntheticScope, forecastNow)).Count == 1, "history reload returns only matching verified scope");
Directory.Delete(Path.GetDirectoryName(historyPath)!, true);
var sample = """{"rateLimits":{"primary":{"usedPercent":39,"windowDurationMins":300,"resetsAt":2000000000},"secondary":{"usedPercent":28,"windowDurationMins":10080,"resetsAt":2000003600}}}""";
var parsed = RateLimitParser.Parse(sample);
Check(parsed.FiveHour?.RemainingPercent == 61, "five-hour remaining percentage");
Check(parsed.SevenDay?.RemainingPercent == 72, "weekly remaining percentage");
Check(RateLimitParser.Parse($"{{\"id\":2,\"result\":{sample}}}").FiveHour?.RemainingPercent == 61, "JSON-RPC result envelope");
Check(RateLimitParser.Parse("""{"rateLimits":{"primary":{"usedPercent":20,"windowDurationMins":60}}}""").FiveHour is null, "unknown duration not mislabeled");
var quotaPair = """{"rateLimits":{"primary":{"usedPercent":20,"windowDurationMins":300},"secondary":{"usedPercent":28,"windowDurationMins":10080}}}""";
Check(RateLimitParser.Parse(quotaPair).FiveHour?.RemainingPercent == 80, "five-hour bucket selected by duration");
Check(RateLimitParser.Parse(quotaPair).SevenDay?.RemainingPercent == 72, "weekly bucket selected by duration");
Check(RateLimitParser.Parse("""{"rateLimits":{"primary":{"usedPercent":20,"windowDurationMins":300},"secondary":{"usedPercent":21,"windowDurationMins":300}}}""").UnavailableReason?.Contains("Ambiguous", StringComparison.Ordinal) == true, "ambiguous matching buckets rejected");
Check(RateLimitParser.Parse("""{"rateLimits":{"primary":{"usedPercent":20,"windowDurationMins":300},"secondary":{"usedPercent":20,"windowDurationMins":300}}}""").FiveHour?.RemainingPercent == 80, "equivalent duplicate buckets deduplicated");
Check(RateLimitParser.Parse("{}").UnavailableReason is not null, "missing values");
Check(RateLimitParser.Parse("""{"rateLimits":{"primary":{"usedPercent":140,"windowDurationMins":300}}}""").FiveHour is null, "out-of-range usage rejected");
var bad = false; try { RateLimitParser.Parse("not-json"); } catch (JsonException) { bad = true; }
Check(bad, "invalid JSON rejected");
Check(RateLimitParser.FormatReset(null) == "reset time unavailable", "missing reset formatting");
Check(WindowClassification.IsCodex("Codex", "Codex"), "Codex window classification");
Check(WindowClassification.IsCodex("ChatGPT", "ChatGPT"), "Codex Desktop packaged window classification");
Check(!WindowClassification.IsCodex("chrome", "Codex"), "unrelated process rejected");
var pos = OverlayPlacement.Calculate(100, 100, 1000, 800, 1.5);
Check(double.IsFinite(pos.Left) && double.IsFinite(pos.Top), "overlay placement is finite");
Check(Math.Abs(pos.Left - (100 / 1.5 + 38 - 56)) < 0.01, "DPI-aware horizontal placement");
var fixedZone = TimeZoneInfo.CreateCustomTimeZone("UTC-05", TimeSpan.FromHours(-5), "UTC-05", "UTC-05");
var formatted = RateLimitParser.FormatReset(DateTimeOffset.FromUnixTimeSeconds(2000000000), fixedZone);
Check(formatted.Length == 5 && formatted[2] == ':', "local 24-hour reset formatting");
foreach (var (hour, minute, expected) in new[] { (0,0,"00:00"), (6,0,"06:00"), (9,5,"09:05"), (12,0,"12:00"), (13,30,"13:30"), (18,0,"18:00"), (18,10,"18:10"), (23,59,"23:59") })
    Check(RateLimitParser.FormatReset(new DateTimeOffset(2026, 11, 6, hour, minute, 0, TimeSpan.Zero), TimeZoneInfo.Utc) == expected, $"24-hour reset formatting {expected}");
Check(WidgetMenuCommands.OrderedLabels.SequenceEqual(new[] { "Show", "Refresh", "Settings", "Reset Position", "|", "Exit" }), "widget and tray shared menu command order");
Check(WidgetLayout.SizeFor(WidgetStyle.Circle) == (90d, 90d), "Circle is permanently 90 DIP");
Check(WidgetLayout.DimensionsFor(WidgetStyle.Circle, WidgetLayout.DefaultScale) == (180d, 180d), "Circle default 200 percent size");
Check(WidgetLayout.DimensionsFor(WidgetStyle.RoundedSquare, WidgetLayout.DefaultScale) == (120d, 120d), "Rounded Square default 200 percent size");
Check(WidgetLayout.DimensionsFor(WidgetStyle.VerticalPill, WidgetLayout.DefaultScale) == (96d, 160d), "Vertical Pill preserves aspect ratio at 200 percent");
Check(WidgetLayout.DimensionsFor(WidgetStyle.HorizontalBar, WidgetLayout.DefaultScale) == (200d, 84d), "Horizontal Bar preserves aspect ratio at 200 percent");
Check(WidgetLayout.ClampScale(0.1) == 0.5 && WidgetLayout.ClampScale(3) == 2.5 && WidgetLayout.ClampScale(double.NaN) == 2, "widget scale clamps slider bounds and invalid values");
Check(WidgetLayout.SizeFor(WidgetStyle.RoundedSquare) == (60d, 60d) && WidgetLayout.SizeFor(WidgetStyle.VerticalPill) == (48d, 80d) && WidgetLayout.SizeFor(WidgetStyle.HorizontalBar) == (100d, 42d), "other widget styles retain dimensions");
var savedRoundedSquare = WidgetSettingsStore.ParseOrDefault("{\"SchemaVersion\":1,\"SelectedStyle\":1,\"Positions\":{}} ");
Check(savedRoundedSquare.SelectedStyle == WidgetStyle.RoundedSquare && WidgetLayout.SizeFor(savedRoundedSquare.SelectedStyle) == (60d, 60d), "persisted Rounded Square selection is not mistaken for Circle");
var savedCircle = WidgetSettingsStore.ParseOrDefault("{\"SchemaVersion\":1,\"SelectedStyle\":0,\"Positions\":{}} ");
Check(savedCircle.SelectedStyle == WidgetStyle.Circle && WidgetLayout.SizeFor(savedCircle.SelectedStyle) == (90d, 90d), "persisted Circle selection resolves to 90 DIP");
Check(new RefreshDiagnostics(DateTimeOffset.UtcNow.AddMinutes(-4), "periodic", "Connected", null).FormatStatus(DateTimeOffset.UtcNow).Contains("4 min ago", StringComparison.Ordinal), "refresh diagnostics report snapshot age");
Check(!new RefreshDiagnostics(null, null, "Disconnected", "IOException").FormatStatus(DateTimeOffset.UtcNow).Contains("token", StringComparison.OrdinalIgnoreCase), "diagnostics expose categories without secrets");
var informationalVersion = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
Check(informationalVersion == "0.1.0-rc.9" && Regex.IsMatch(informationalVersion, @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$"), "assembly exposes configured SemVer 2.0 version");
Check(WidgetInteractionPolicy.CanStartDrag(true) && !WidgetInteractionPolicy.CanStartDrag(false), "only left button initiates drag");
var mutexName = $"CodexSmartCircle.Tests.{Guid.NewGuid():N}";
using (var first = new Mutex(true, mutexName, out var firstCreated))
using (var second = new Mutex(true, mutexName, out var secondCreated))
{
    Check(firstCreated && !secondCreated, "single-instance mutex behavior");
    first.ReleaseMutex();
}
var settings = new WidgetSettings();
foreach (var style in Enum.GetValues<WidgetStyle>()) settings.SetPosition(style, (int)style * 10, (int)style * 20);
settings.Select(WidgetStyle.VerticalPill);
var restored = WidgetSettingsStore.ParseOrDefault(JsonSerializer.Serialize(settings));
Check(Enum.GetValues<WidgetStyle>().Length == 4, "four widget styles available");
Check(restored.SelectedStyle == WidgetStyle.VerticalPill, "selected style restores");
restored.SetScale(WidgetStyle.Circle, 1.4); restored.SetScale(WidgetStyle.HorizontalBar, 2.3);
var resizedRestore = WidgetSettingsStore.ParseOrDefault(JsonSerializer.Serialize(restored));
Check(resizedRestore.GetScale(WidgetStyle.Circle) == 1.4 && resizedRestore.GetScale(WidgetStyle.HorizontalBar) == 2.3, "per-style sizes restore independently");
Check(new WidgetSettings().GetScale(WidgetStyle.Circle) == 2.0, "legacy settings migrate to 200 percent default");
Check(restored.GetPosition(WidgetStyle.Circle).X != restored.GetPosition(WidgetStyle.HorizontalBar).X, "separate per-style positions");
restored.ResetPosition(WidgetStyle.Circle);
Check(restored.GetPosition(WidgetStyle.Circle).X == 0 && restored.GetPosition(WidgetStyle.HorizontalBar).X != 0, "reset affects active style only");
Check(WidgetSettingsStore.ParseOrDefault("invalid json").SelectedStyle == WidgetStyle.Circle, "corrupt settings recover to defaults");
Check(new WidgetSettings().GetPosition(WidgetStyle.RoundedSquare).X == 0, "missing style position uses default anchor offset");
var tempSettings = Path.Combine(Path.GetTempPath(), $"CodexSmartCircle-{Guid.NewGuid():N}", "settings.json");
var fileSettings = new WidgetSettings(); fileSettings.Select(WidgetStyle.HorizontalBar); fileSettings.SetPosition(WidgetStyle.HorizontalBar, 42, 84);
WidgetSettingsStore.Save(fileSettings, tempSettings);
var fileRestored = WidgetSettingsStore.Load(tempSettings);
Check(fileRestored.SelectedStyle == WidgetStyle.HorizontalBar && fileRestored.GetPosition(WidgetStyle.HorizontalBar).Y == 84, "settings file save and restart restore");
File.WriteAllText(tempSettings, "broken");
Check(WidgetSettingsStore.Load(tempSettings).SelectedStyle == WidgetStyle.Circle, "corrupted settings file recovery");
Directory.Delete(Path.GetDirectoryName(tempSettings)!, true);
Exception? wpfFailure = null;
var wpfThread = new Thread(() =>
{
    try
    {
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow();
        var settingsField = typeof(MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var styleField = typeof(MainWindow).GetField("_style", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var applyStyle = typeof(MainWindow).GetMethod("ApplyStyle", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var uiRoot = (System.Windows.FrameworkElement)typeof(MainWindow).GetField("Root", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var shape = (System.Windows.Controls.Border)typeof(MainWindow).GetField("StyleShape", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var ellipse = (System.Windows.Shapes.Ellipse)typeof(MainWindow).GetField("Ellipse", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var circleLabels = (System.Windows.Controls.Grid)typeof(MainWindow).GetField("CircleLabels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var otherLabels = (System.Windows.Controls.Grid)typeof(MainWindow).GetField("OtherLabels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var resetText = (System.Windows.Controls.TextBlock)typeof(MainWindow).GetField("ResetText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var fiveText = (System.Windows.Controls.TextBlock)typeof(MainWindow).GetField("FiveText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var forecastText = (System.Windows.Controls.TextBlock)typeof(MainWindow).GetField("ForecastText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var weekText = (System.Windows.Controls.TextBlock)typeof(MainWindow).GetField("WeekText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var otherResetText = (System.Windows.Controls.TextBlock)typeof(MainWindow).GetField("OtherResetText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var otherFiveText = (System.Windows.Controls.TextBlock)typeof(MainWindow).GetField("OtherFiveText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var otherWeekText = (System.Windows.Controls.TextBlock)typeof(MainWindow).GetField("OtherWeekText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var otherForecastText = (System.Windows.Controls.TextBlock)typeof(MainWindow).GetField("OtherForecastText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var innerArc = (System.Windows.Shapes.Path)typeof(MainWindow).GetField("InnerArc", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var outerArc = (System.Windows.Shapes.Path)typeof(MainWindow).GetField("OuterArc", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var setArc = typeof(MainWindow).GetMethod("SetArc", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var forecastColor = ((System.Windows.Media.SolidColorBrush)forecastText.Foreground).Color;
        Check(forecastColor == System.Windows.Media.Color.FromRgb(0xFF, 0xD3, 0x4A), "Circle forecast uses approved yellow");
        var settings = new WidgetSettings(); foreach (var style in Enum.GetValues<WidgetStyle>()) settings.SetScale(style, 2);
        settingsField.SetValue(window, settings);
        foreach (var style in Enum.GetValues<WidgetStyle>())
        {
            styleField.SetValue(window, style); applyStyle.Invoke(window, null);
            var expected = WidgetLayout.DimensionsFor(style, 2);
            Check(window.Width == expected.Width && window.Height == expected.Height && uiRoot.Width == expected.Width && uiRoot.Height == expected.Height, $"WPF Window and Root apply {style} geometry");
            var expectedCorner = style switch { WidgetStyle.Circle => 90d, WidgetStyle.VerticalPill => 60d, WidgetStyle.RoundedSquare => 24d, _ => 40d };
            Check(shape.CornerRadius.TopLeft == expectedCorner, $"WPF visual surface applies distinct {style} corner geometry");
            Check((ellipse.Visibility == System.Windows.Visibility.Visible) == (style == WidgetStyle.Circle), $"WPF shape template distinguishes {style}");
            Check((circleLabels.Visibility == System.Windows.Visibility.Visible) == (style == WidgetStyle.Circle) && (otherLabels.Visibility == System.Windows.Visibility.Visible) == (style != WidgetStyle.Circle), $"WPF label template is isolated for {style}");
            if (style != WidgetStyle.Circle)
                Check(otherResetText.FontSize == 16 && otherFiveText.FontSize == 28 && otherWeekText.FontSize == 12 && otherForecastText.FontSize == 12, $"Non-circle typography is preserved for {style}");
        }
        styleField.SetValue(window, WidgetStyle.Circle);
        foreach (var scale in new[] { 0.9, 1.0, 1.6, 2.0 })
        {
            settings.SetScale(WidgetStyle.Circle, scale); applyStyle.Invoke(window, null);
            resetText.Text = "00:40"; fiveText.Text = "89%"; weekText.Text = "80%"; forecastText.Text = "5";
            var size = WidgetLayout.DimensionsFor(WidgetStyle.Circle, scale);
            uiRoot.Measure(new System.Windows.Size(size.Width, size.Height));
            uiRoot.Arrange(new System.Windows.Rect(0, 0, size.Width, size.Height));
            uiRoot.UpdateLayout();
            var weekBounds = weekText.TransformToAncestor(uiRoot).TransformBounds(new System.Windows.Rect(0, 0, weekText.ActualWidth, weekText.ActualHeight));
            var forecastBounds = forecastText.TransformToAncestor(uiRoot).TransformBounds(new System.Windows.Rect(0, 0, forecastText.ActualWidth, forecastText.ActualHeight));
            var fiveBounds = fiveText.TransformToAncestor(uiRoot).TransformBounds(new System.Windows.Rect(0, 0, fiveText.ActualWidth, fiveText.ActualHeight));
            var resetBounds = resetText.TransformToAncestor(uiRoot).TransformBounds(new System.Windows.Rect(0, 0, resetText.ActualWidth, resetText.ActualHeight));
            double MaxRadius(System.Windows.Rect rect) => new[] { new System.Windows.Point(rect.Left, rect.Top), new System.Windows.Point(rect.Right, rect.Top), new System.Windows.Point(rect.Left, rect.Bottom), new System.Windows.Point(rect.Right, rect.Bottom) }
                .Max(point => Math.Sqrt(Math.Pow(point.X - size.Width / 2, 2) + Math.Pow(point.Y - size.Height / 2, 2)));
            var innerRadius = size.Width * 0.46 * (24d / 28d);
            Check(forecastText.Text == "5", $"Circle forecast integer is bound at {scale:P0}");
            Check(shape.CornerRadius.TopLeft == 45 * scale, $"Circle remains fully round at {scale:P0}");
            Check(circleLabels.Margin == new System.Windows.Thickness(0), $"Circle uses a dedicated full-size label canvas at {scale:P0}");
            Check(weekText.HorizontalAlignment == System.Windows.HorizontalAlignment.Left && forecastText.HorizontalAlignment == System.Windows.HorizontalAlignment.Right, $"Circle weekly and forecast labels anchor to opposite lower corners at {scale:P0}");
            Check(weekBounds.Right < forecastBounds.Left, $"Circle lower indicators do not overlap at {scale:P0}");
            Check(weekBounds.Left >= 0 && forecastBounds.Right <= size.Width && weekBounds.Bottom <= size.Height && forecastBounds.Bottom <= size.Height, $"Circle lower indicators remain within the widget at {scale:P0}");
            Check(MaxRadius(fiveBounds) < innerRadius - innerArc.StrokeThickness / 2 && MaxRadius(weekBounds) < innerRadius - innerArc.StrokeThickness / 2 && MaxRadius(forecastBounds) < innerRadius - innerArc.StrokeThickness / 2, $"Circle text avoids the inner arc at {scale:P0}");
            Check(MaxRadius(resetBounds) < size.Width * 0.46 - outerArc.StrokeThickness / 2, $"Circle reset time avoids the outer ring at {scale:P0}");
            Check(Math.Abs(forecastText.FontSize - weekText.FontSize) < 0.01, $"bottom labels use matching scale at {scale:P0}");
            Check(Math.Abs(resetText.Margin.Top - size.Width * 0.147) < 0.001, $"Circle reset layout scales with geometry at {scale:P0}");
            Check(Math.Abs(fiveText.FontSize - 13.2 * scale * 1.5) < 0.001 && fiveText.FontWeight == System.Windows.FontWeights.Bold, $"Circle five-hour typography matches approved size and weight at {scale:P0}");
            Check(Math.Abs(fiveText.Margin.Top + size.Width * 0.047) < 0.001, $"Circle five-hour label offset scales with geometry at {scale:P0}");
            Check(Math.Abs(weekText.FontSize - 8.2 * scale * 1.5) < 0.001 && Math.Abs(forecastText.FontSize - weekText.FontSize) < 0.001, $"Circle bottom labels share the approved visual scale at {scale:P0}");
            Check(Math.Abs(weekText.Margin.Left - size.Width * 0.30) < 0.001 && Math.Abs(weekText.Margin.Bottom - size.Width * 0.1973) < 0.001 && Math.Abs(forecastText.Margin.Right - size.Width * 0.29) < 0.001 && Math.Abs(forecastText.Margin.Bottom - size.Width * 0.1973) < 0.001, $"Circle bottom-row layout scales with geometry at {scale:P0}");
            Check(resetText.FontWeight == System.Windows.FontWeights.Bold && weekText.FontWeight == System.Windows.FontWeights.Normal, $"Circle reset and weekly font weights are preserved at {scale:P0}");
            var fitBottomTypography = typeof(MainWindow).GetMethod("ApplyCircleBottomTypography", BindingFlags.Instance | BindingFlags.NonPublic)!;
            weekText.Text = "100%"; forecastText.Text = "5"; fitBottomTypography.Invoke(window, null); uiRoot.UpdateLayout();
            var widerWeekBounds = weekText.TransformToAncestor(uiRoot).TransformBounds(new System.Windows.Rect(0, 0, weekText.ActualWidth, weekText.ActualHeight));
            var widerForecastBounds = forecastText.TransformToAncestor(uiRoot).TransformBounds(new System.Windows.Rect(0, 0, forecastText.ActualWidth, forecastText.ActualHeight));
            Check(widerWeekBounds.Right < widerForecastBounds.Left && widerForecastBounds.Right <= size.Width && widerWeekBounds.Left >= 0, $"Circle accommodates 100 percent with one-digit forecast at {scale:P0}");
            weekText.Text = "80%"; forecastText.Text = "12"; fitBottomTypography.Invoke(window, null); uiRoot.UpdateLayout();
            widerWeekBounds = weekText.TransformToAncestor(uiRoot).TransformBounds(new System.Windows.Rect(0, 0, weekText.ActualWidth, weekText.ActualHeight));
            widerForecastBounds = forecastText.TransformToAncestor(uiRoot).TransformBounds(new System.Windows.Rect(0, 0, forecastText.ActualWidth, forecastText.ActualHeight));
            Check(widerWeekBounds.Right < widerForecastBounds.Left && widerForecastBounds.Right <= size.Width && widerWeekBounds.Left >= 0, $"Circle accommodates two-digit forecast with 80 percent at {scale:P0}");
            weekText.Text = "100%"; forecastText.Text = "12"; fitBottomTypography.Invoke(window, null); uiRoot.UpdateLayout();
            widerWeekBounds = weekText.TransformToAncestor(uiRoot).TransformBounds(new System.Windows.Rect(0, 0, weekText.ActualWidth, weekText.ActualHeight));
            widerForecastBounds = forecastText.TransformToAncestor(uiRoot).TransformBounds(new System.Windows.Rect(0, 0, forecastText.ActualWidth, forecastText.ActualHeight));
            Check(Math.Abs(weekText.FontSize - 6 * scale * 1.5) < 0.001 && Math.Abs(forecastText.FontSize - weekText.FontSize) < 0.001 && widerWeekBounds.Right < widerForecastBounds.Left, $"Circle fits combined wide labels without overlap at {scale:P0}");
            weekText.Text = "80%"; forecastText.Text = "5"; uiRoot.UpdateLayout();
            fitBottomTypography.Invoke(window, null); uiRoot.UpdateLayout();
            var radius = size.Width * 0.46;
            setArc.Invoke(window, [innerArc, radius * (24d / 28d), 89d]);
            setArc.Invoke(window, [outerArc, radius, 80d]);
            var renderDirectory = Environment.GetEnvironmentVariable("CSBL_RENDER_DIR");
            if (!string.IsNullOrWhiteSpace(renderDirectory))
            {
                Directory.CreateDirectory(renderDirectory);
                var pixelWidth = (int)Math.Ceiling(size.Width * 2); var pixelHeight = (int)Math.Ceiling(size.Height * 2);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(pixelWidth, pixelHeight, 192, 192, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(uiRoot);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.Combine(renderDirectory, $"Synthetic-{scale:P0}-200dpi.png"))) encoder.Save(stream);
                if (scale == 1.0)
                {
                    foreach (var forecastValue in new[] { "-", "5" })
                    {
                        forecastText.Text = forecastValue;
                        var forecastBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(pixelWidth, pixelHeight, 192, 192, System.Windows.Media.PixelFormats.Pbgra32);
                        forecastBitmap.Render(uiRoot);
                        var forecastEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); forecastEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(forecastBitmap));
                        using var forecastStream = File.Create(Path.Combine(renderDirectory, $"Synthetic-100%-forecast-{(forecastValue == "-" ? "dash" : "5")}-200dpi.png")); forecastEncoder.Save(forecastStream);
                    }
                    forecastText.Text = "5";
                }
            }
        }
        weekText.Text = "--"; forecastText.Text = "-";
        var updateForecastLabels = typeof(MainWindow).GetMethod("UpdateForecastLabels", BindingFlags.Instance | BindingFlags.NonPublic)!;
        updateForecastLabels.Invoke(window, [DateTimeOffset.UtcNow]);
        Check(forecastText.Text == "-" && ((System.Windows.Media.SolidColorBrush)forecastText.Foreground).Color == System.Windows.Media.Color.FromRgb(0xFF, 0xD3, 0x4A), "unavailable Circle forecast stays a yellow dash");
        app.Shutdown();
    }
    catch (Exception ex) { wpfFailure = ex; }
});
wpfThread.SetApartmentState(ApartmentState.STA); wpfThread.Start(); wpfThread.Join();
if (wpfFailure is not null) throw new Exception("WPF integration test failed", wpfFailure);
Console.WriteLine($"{checks} passed, 0 failed");
