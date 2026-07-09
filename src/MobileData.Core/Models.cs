namespace MobileData.Core;

/// <summary>
/// A single mobile-data usage snapshot for one carrier account/line.
/// </summary>
public record DataUsageSnapshot(
    string ProviderId,
    string PlanName,
    bool IsUnlimited,
    double? TotalGb,
    double UsedGb,
    DateTimeOffset? CycleResetsAt,
    DateTimeOffset FetchedAt,
    string? Message)
{
    /// <summary>Remaining GB, or null when unlimited / total unknown.</summary>
    public double? RemainingGb =>
        IsUnlimited || TotalGb is null ? null : Math.Max(0, TotalGb.Value - UsedGb);

    /// <summary>Fraction of the allotment consumed (0..1), or null when unlimited/unknown.</summary>
    public double? Utilization =>
        IsUnlimited || TotalGb is null || TotalGb.Value <= 0
            ? null
            : Math.Clamp(UsedGb / TotalGb.Value, 0, 1);
}

/// <summary>
/// Whether a provider has usable cached data.
/// </summary>
public record ProviderAvailability(
    bool IsAvailable,
    string? Message);
