namespace MobileData.Core;

/// <summary>
/// Contract for a mobile-carrier data-usage provider.
/// Mirrors the LLM widget's provider pattern: the widget process never talks to
/// the carrier directly. A separate collector writes a local cache file and the
/// provider simply reads it.
/// </summary>
public interface IDataProvider
{
    /// <summary>Unique identifier for this provider (e.g., "rogers", "fido").</summary>
    string ProviderId { get; }

    /// <summary>Human-readable name shown in the widget UI.</summary>
    string DisplayName { get; }

    /// <summary>Check whether cached usage data is available.</summary>
    Task<ProviderAvailability> CheckAvailabilityAsync();

    /// <summary>Read the latest cached usage snapshot.</summary>
    Task<DataUsageSnapshot> FetchUsageAsync(CancellationToken ct);

    /// <summary>How often the widget should re-read the cache.</summary>
    TimeSpan PollingInterval { get; }

    /// <summary>Raised when the underlying cache file changes.</summary>
    event EventHandler? DataChanged;
}
