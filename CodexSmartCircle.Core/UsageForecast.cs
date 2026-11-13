using System.Text.Json;

namespace CodexSmartCircle.Core;

public enum ForecastAvailability { Available, Unavailable }
public sealed record QuotaDiagnostics(bool RpcSucceeded, string AccountIdState, bool BucketsAvailable, string LimitIdsState, string FiveHourState, string WeeklyState, string Classification)
{
    public static QuotaDiagnostics Parse(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json); var root = d.RootElement;
            if (root.TryGetProperty("error", out _)) return new(false, "unknown", false, "missing", "invalid", "invalid", "invalid");
            if (root.TryGetProperty("result", out var r)) root = r;
            var account = root.TryGetProperty("accountId", out var a) ? (a.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(a.GetString()) ? "present" : a.ValueKind == JsonValueKind.Null ? "null" : "missing") : "missing";
            if (!root.TryGetProperty("rateLimitsByLimitId", out var buckets) || buckets.ValueKind != JsonValueKind.Object) return new(true, account, false, "missing", "missing", "missing", "unknown");
            var five = 0; var week = 0; var idCount = 0; var idMismatch = false;
            foreach (var bucket in buckets.EnumerateObject())
            {
                var snapshot = bucket.Value;
                var limitIdPresent = !string.IsNullOrWhiteSpace(bucket.Name);
                if (snapshot.ValueKind == JsonValueKind.Object && snapshot.TryGetProperty("limitId", out var embedded))
                {
                    if (embedded.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(embedded.GetString()))
                    {
                        idMismatch |= !string.Equals(embedded.GetString(), bucket.Name, StringComparison.Ordinal);
                        limitIdPresent = true;
                    }
                    else if (embedded.ValueKind != JsonValueKind.Null) limitIdPresent = false;
                }
                if (limitIdPresent) idCount++;
                if (snapshot.ValueKind != JsonValueKind.Object) continue;
                foreach (var windowName in new[] { "primary", "secondary" })
                    if (snapshot.TryGetProperty(windowName, out var window) && window.ValueKind == JsonValueKind.Object && window.TryGetProperty("windowDurationMins", out var duration) && duration.ValueKind == JsonValueKind.Number && duration.TryGetInt32(out var minutes) && window.TryGetProperty("usedPercent", out var used) && used.ValueKind == JsonValueKind.Number && used.TryGetInt32(out var pct) && pct is >= 0 and <= 100)
                    { if (minutes == 300) five++; if (minutes == 10080) week++; }
            }
            var f = five == 1 ? "valid" : five == 0 ? "missing" : "ambiguous"; var w = week == 1 ? "valid" : week == 0 ? "missing" : "ambiguous";
            var classification = idMismatch || f == "ambiguous" || w == "ambiguous" || idCount > 1 ? "ambiguous" : f == "valid" && w == "valid" && idCount == 1 ? "valid" : "unknown";
            return new(true, account, true, idCount == 0 ? "missing" : "present", f, w, classification);
        }
        catch (JsonException) { return new(false, "unknown", false, "missing", "invalid", "invalid", "invalid"); }
    }
}
public static class SafeDiagnosticStore
{
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexSmartCircle", "runtime-diagnostics.json");
    public static void Save(object state)
    {
        try { var dir = Path.GetDirectoryName(FilePath)!; Directory.CreateDirectory(dir); var tmp = FilePath + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(state)); File.Move(tmp, FilePath, true); }
        catch { }
    }
}
public enum AccountIdentityStatus { Verified, PendingQuotaScope, Unknown, Unsupported, Error }
public sealed record AccountIdentity(AccountIdentityStatus Status, string? PrivateScopeKey, string? AccountType, bool UserIdProvided, bool QuotaScopeAvailable, DateTimeOffset? VerifiedAtUtc)
{
    public static AccountIdentity Unknown(AccountIdentityStatus status = AccountIdentityStatus.Unknown) => new(status, null, null, false, false, null);
    public static AccountIdentity Parse(string json, DateTimeOffset nowUtc)
    {
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            return Unknown(message.Contains("method", StringComparison.OrdinalIgnoreCase) ? AccountIdentityStatus.Unsupported : AccountIdentityStatus.Error);
        }
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.Object) return Unknown();
        var type = account.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (!string.Equals(type, "chatgpt", StringComparison.Ordinal)) return Unknown();

        // The installed App Server schema exposes no stable userId in account/read.
        // Quota recording stays pending until two matching accountId + limitId reads verify scope.
        return new(AccountIdentityStatus.PendingQuotaScope, null, type, false, false, null);
    }
}

public sealed record QuotaScopeVerification(bool Verified, string? ScopeKey, int ConsecutiveObservations, string State);

public sealed class QuotaScopeVerifier
{
    private string? _candidate;
    private int _consecutiveObservations;

    public QuotaScopeVerification Observe(string? accountType, string rateLimitJson)
    {
        var candidate = TryCreateScopeKey(accountType, rateLimitJson);
        if (candidate is null)
        {
            Reset();
            return new(false, null, 0, "unverified");
        }

        if (!string.Equals(candidate, _candidate, StringComparison.Ordinal))
        {
            _candidate = candidate;
            _consecutiveObservations = 1;
            return new(false, null, 1, "awaiting-second-matching-observation");
        }

        _consecutiveObservations++;
        return new(true, candidate, _consecutiveObservations, "verified");
    }

    public void Reset()
    {
        _candidate = null;
        _consecutiveObservations = 0;
    }

    public static bool IsValidScopeKey(string? scopeKey) => scopeKey is { Length: 64 } && scopeKey.All(Uri.IsHexDigit);

    private static string? TryCreateScopeKey(string? accountType, string json)
    {
        if (!string.Equals(accountType, "chatgpt", StringComparison.Ordinal) || QuotaDiagnostics.Parse(json).Classification != "valid") return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out _)) return null;
            if (root.TryGetProperty("result", out var result)) root = result;
            if (!root.TryGetProperty("accountId", out var accountId) || accountId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(accountId.GetString())) return null;
            if (!root.TryGetProperty("rateLimitsByLimitId", out var buckets) || buckets.ValueKind != JsonValueKind.Object) return null;

            var eligibleIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var bucket in buckets.EnumerateObject())
            {
                if (bucket.Value.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(bucket.Name)) return null;
                var limitId = bucket.Name;
                if (bucket.Value.TryGetProperty("limitId", out var embeddedId))
                {
                    if (embeddedId.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(embeddedId.GetString()))
                    {
                        if (!string.Equals(embeddedId.GetString(), bucket.Name, StringComparison.Ordinal)) return null;
                        limitId = embeddedId.GetString()!;
                    }
                    else if (embeddedId.ValueKind != JsonValueKind.Null) return null;
                }

                if (HasWindow(bucket.Value, 300) || HasWindow(bucket.Value, 10080)) eligibleIds.Add(limitId);
            }

            // Fail closed unless exactly one backend quota bucket carries both windows.
            if (eligibleIds.Count != 1) return null;
            var id = eligibleIds.Single();
            var bucketElement = buckets.GetProperty(id);
            if (!HasWindow(bucketElement, 300) || !HasWindow(bucketElement, 10080)) return null;

            var material = $"chatgpt\n{accountId.GetString()}\n{id}";
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)));
        }
        catch (JsonException) { return null; }
    }

    private static bool HasWindow(JsonElement bucket, int minutes)
    {
        foreach (var name in new[] { "primary", "secondary" })
            if (bucket.TryGetProperty(name, out var window) && window.ValueKind == JsonValueKind.Object &&
                window.TryGetProperty("windowDurationMins", out var duration) && duration.TryGetInt64(out var actual) && actual == minutes &&
                window.TryGetProperty("usedPercent", out var used) && used.ValueKind == JsonValueKind.Number && used.TryGetDouble(out var percent) && double.IsFinite(percent) && percent is >= 0 and <= 100 &&
                window.TryGetProperty("resetsAt", out var reset) && reset.ValueKind == JsonValueKind.Number && reset.TryGetInt64(out var resetEpoch) && resetEpoch > 0)
                return true;
        return false;
    }
}
public enum ForecastConfidence { Low, Moderate }
public enum ForecastUnavailableReason { InsufficientHistory, StaleData, InvalidData, AmbiguousQuota, ResetBoundary, IncompatibleAccount, NoMeasurableConsumption }

public sealed record ForecastObservation(DateTimeOffset TimestampUtc, double FiveHourUsedPercent, double WeeklyUsedPercent,
    DateTimeOffset FiveHourResetUtc, DateTimeOffset WeeklyResetUtc, string QuotaScope);
public sealed record UsageForecast(ForecastAvailability Availability, int? RemainingFullCycles, double? WeeklyCostPerFullCycle,
    ForecastConfidence? Confidence, int CompatibleObservationCount, DateTimeOffset? LastObservationUtc, ForecastUnavailableReason? Reason)
{
    public static UsageForecast Unavailable(ForecastUnavailableReason reason, int count = 0, DateTimeOffset? last = null) =>
        new(ForecastAvailability.Unavailable, null, null, null, count, last, reason);
}

public sealed class ForecastGenerationGate
{
    private long _generation;
    public long Advance() => Interlocked.Increment(ref _generation);
    public bool IsCurrent(long generation) => generation == Volatile.Read(ref _generation);
}

public static class UsageForecaster
{
    public static UsageForecast Calculate(IEnumerable<ForecastObservation> observations, DateTimeOffset nowUtc, TimeSpan? maxAge = null)
    {
        var all = observations.OrderBy(x => x.TimestampUtc).ToArray();
        if (all.Length == 0) return UsageForecast.Unavailable(ForecastUnavailableReason.InsufficientHistory);
        var last = all[^1];
        if (nowUtc - last.TimestampUtc > (maxAge ?? TimeSpan.FromMinutes(2))) return UsageForecast.Unavailable(ForecastUnavailableReason.StaleData, 0, last.TimestampUtc);
        if (all.Any(x => !Valid(x))) return UsageForecast.Unavailable(ForecastUnavailableReason.InvalidData, 0, last.TimestampUtc);
        if (all.Select(x => x.QuotaScope).Distinct(StringComparer.Ordinal).Count() != 1) return UsageForecast.Unavailable(ForecastUnavailableReason.IncompatibleAccount, 0, last.TimestampUtc);
        double sum5 = 0, sumWeek = 0; var pairs = 0; var groups = new HashSet<string>(); var resetBoundary = false;
        for (var i = 1; i < all.Length; i++)
        {
            var a = all[i - 1]; var b = all[i];
            if (b.TimestampUtc <= a.TimestampUtc) continue;
            if (a.FiveHourResetUtc != b.FiveHourResetUtc || a.WeeklyResetUtc != b.WeeklyResetUtc) { resetBoundary = true; continue; }
            var d5 = b.FiveHourUsedPercent - a.FiveHourUsedPercent; var dw = b.WeeklyUsedPercent - a.WeeklyUsedPercent;
            if (d5 > 0 && dw >= 0 && d5 <= 100 && dw <= 100)
            { sum5 += d5; sumWeek += dw; pairs++; groups.Add($"{b.FiveHourResetUtc.UtcTicks}:{b.WeeklyResetUtc.UtcTicks}"); }
        }
        if (sum5 < 10 || pairs == 0) return UsageForecast.Unavailable(resetBoundary ? ForecastUnavailableReason.ResetBoundary : ForecastUnavailableReason.InsufficientHistory, pairs, last.TimestampUtc);
        if (sumWeek <= 0) return UsageForecast.Unavailable(ForecastUnavailableReason.NoMeasurableConsumption, pairs, last.TimestampUtc);
        var cost = 100d * sumWeek / sum5;
        if (!double.IsFinite(cost) || cost <= 0) return UsageForecast.Unavailable(ForecastUnavailableReason.InvalidData, pairs, last.TimestampUtc);
        var remaining = Math.Clamp(100d - last.WeeklyUsedPercent, 0, 100);
        var confidence = sum5 >= 80 && sumWeek >= 5 && groups.Count >= 2 ? ForecastConfidence.Moderate : ForecastConfidence.Low;
        return new(ForecastAvailability.Available, (int)Math.Floor(remaining / cost), cost, confidence, pairs, last.TimestampUtc, null);
    }
    private static bool Valid(ForecastObservation x) => double.IsFinite(x.FiveHourUsedPercent) && x.FiveHourUsedPercent is >= 0 and <= 100 &&
        double.IsFinite(x.WeeklyUsedPercent) && x.WeeklyUsedPercent is >= 0 and <= 100 && x.TimestampUtc.Offset == TimeSpan.Zero &&
        x.FiveHourResetUtc != default && x.WeeklyResetUtc != default && QuotaScopeVerifier.IsValidScopeKey(x.QuotaScope);
}

public sealed class UsageHistoryStore
{
    private const int MaxEntries = 5000;
    public string FilePath { get; }
    private readonly SemaphoreSlim _gate = new(1, 1);
    public UsageHistoryStore(string? path = null) => FilePath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexSmartCircle", "usage-history-v1.jsonl");

    public async Task<IReadOnlyList<ForecastObservation>> LoadAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(FilePath)) return [];
            return await LoadWithoutGateAsync(nowUtc, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ForecastObservation>> LoadForScopeAsync(string scopeKey, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        if (!QuotaScopeVerifier.IsValidScopeKey(scopeKey)) return [];
        return (await LoadAsync(nowUtc, cancellationToken)).Where(x => string.Equals(x.QuotaScope, scopeKey, StringComparison.Ordinal)).ToArray();
    }

    public async Task<bool> AppendAsync(ForecastObservation item, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        if (!QuotaScopeVerifier.IsValidScopeKey(item.QuotaScope) || !Valid(item) || item.TimestampUtc > nowUtc.AddMinutes(1)) return false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = (await LoadWithoutGateAsync(nowUtc, cancellationToken)).ToList();
            if (items.Count > 0 && item.TimestampUtc <= items[^1].TimestampUtc) return false;
            if (items.Count > 0 && item.FiveHourUsedPercent == items[^1].FiveHourUsedPercent && item.WeeklyUsedPercent == items[^1].WeeklyUsedPercent && item.FiveHourResetUtc == items[^1].FiveHourResetUtc && item.WeeklyResetUtc == items[^1].WeeklyResetUtc && item.QuotaScope == items[^1].QuotaScope) return false;
            items.Add(item); items = items.TakeLast(MaxEntries).ToList();
            var dir = Path.GetDirectoryName(FilePath)!; Directory.CreateDirectory(dir);
            var temp = FilePath + ".tmp"; await File.WriteAllLinesAsync(temp, items.Select(x => JsonSerializer.Serialize(x)), cancellationToken);
            File.Move(temp, FilePath, true); return true;
        }
        finally { _gate.Release(); }
    }
    private async Task<IReadOnlyList<ForecastObservation>> LoadWithoutGateAsync(DateTimeOffset nowUtc, CancellationToken token)
    {
        if (!File.Exists(FilePath)) return [];
        var cutoff = nowUtc.AddDays(-14); var result = new List<ForecastObservation>(); var changed = false;
        foreach (var line in await File.ReadAllLinesAsync(FilePath, token))
        {
            try
            {
                var x = JsonSerializer.Deserialize<ForecastObservation>(line);
                if (x is null || x.TimestampUtc < cutoff || x.TimestampUtc > nowUtc.AddMinutes(1) || !QuotaScopeVerifier.IsValidScopeKey(x.QuotaScope) || !Valid(x)) { changed = true; continue; }
                if (result.Count > 0 && x.TimestampUtc <= result[^1].TimestampUtc) { changed = true; continue; }
                result.Add(x);
            }
            catch (JsonException) { changed = true; }
        }
        if (result.Count > MaxEntries) { result = result.TakeLast(MaxEntries).ToList(); changed = true; }
        if (changed)
        {
            var temp = FilePath + ".compact.tmp"; var backup = FilePath + ".bak";
            try
            {
                await File.WriteAllLinesAsync(temp, result.Select(x => JsonSerializer.Serialize(x)), token);
                File.Copy(FilePath, backup, true);
                File.Move(temp, FilePath, true);
            }
            catch
            {
                if (File.Exists(backup)) File.Copy(backup, FilePath, true);
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        return result;
    }
    private static bool Valid(ForecastObservation x) => double.IsFinite(x.FiveHourUsedPercent) && x.FiveHourUsedPercent is >= 0 and <= 100 && double.IsFinite(x.WeeklyUsedPercent) && x.WeeklyUsedPercent is >= 0 and <= 100 && x.TimestampUtc.Offset == TimeSpan.Zero && x.FiveHourResetUtc != default && x.WeeklyResetUtc != default && !string.IsNullOrWhiteSpace(x.QuotaScope);
}
