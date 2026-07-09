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
}
