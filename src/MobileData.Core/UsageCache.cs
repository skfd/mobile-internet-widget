using System.Text.Json;
using System.Text.Json.Serialization;

namespace MobileData.Core;

/// <summary>
/// On-disk cache record written by the collector and read by the widget provider.
/// Lives at <c>%USERPROFILE%\.mobile-widget\{provider}.json</c>.
/// </summary>
public sealed class UsageCacheRecord
{
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("planName")] public string PlanName { get; set; } = "";
    [JsonPropertyName("isUnlimited")] public bool IsUnlimited { get; set; }
    [JsonPropertyName("totalGb")] public double? TotalGb { get; set; }
    [JsonPropertyName("usedGb")] public double UsedGb { get; set; }
    [JsonPropertyName("cycleResetsAt")] public DateTimeOffset? CycleResetsAt { get; set; }
    [JsonPropertyName("fetchedAt")] public DateTimeOffset FetchedAt { get; set; }

    /// <summary>When the collector last <em>attempted</em> a scrape, whether it
    /// succeeded or failed. Lets the widget tell "the collector ran and failed"
    /// (session likely expired) apart from "we simply haven't scraped in a while"
    /// (laptop asleep/offline).</summary>
    [JsonPropertyName("lastAttemptAt")] public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>Reason the last attempt failed, or null if it succeeded.</summary>
    [JsonPropertyName("lastError")] public string? LastError { get; set; }
}

/// <summary>
/// Locates, reads, and writes the shared usage cache.
/// </summary>
public static class UsageCache
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Directory holding per-carrier cache files.</summary>
    public static string Directory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mobile-widget");

    /// <summary>Full path of the cache file for a given provider id.</summary>
    public static string PathFor(string providerId) =>
        Path.Combine(Directory, $"{providerId}.json");

    /// <summary>Read the cache for a provider, or null if missing/unreadable.</summary>
    public static UsageCacheRecord? TryRead(string providerId)
    {
        try
        {
            var path = PathFor(providerId);
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<UsageCacheRecord>(json, Options);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Write (overwrite) the cache for a provider.</summary>
    public static void Write(UsageCacheRecord record)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var json = JsonSerializer.Serialize(record, Options);
        File.WriteAllText(PathFor(record.Provider), json);
    }

    private static string ForceFlagFor(string providerId) =>
        Path.Combine(Directory, $"{providerId}.force");

    /// <summary>
    /// Ask the next collector run to scrape even if we don't look tethered.
    /// The widget's Refresh button can only <em>start</em> the collector's
    /// scheduled task (schtasks /run takes no arguments), so it leaves this
    /// one-shot flag on disk to pass the intent along.
    /// </summary>
    public static void RequestForce(string providerId)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(ForceFlagFor(providerId), "");
        }
        catch
        {
            // Best-effort: without the flag the run just honours the usual skip.
        }
    }

    /// <summary>Take a pending force request, if any. True at most once per request.</summary>
    public static bool ConsumeForce(string providerId)
    {
        try
        {
            var path = ForceFlagFor(providerId);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
