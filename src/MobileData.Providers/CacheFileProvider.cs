using MobileData.Core;

namespace MobileData.Providers;

/// <summary>
/// Base provider that reads a carrier's usage from the shared on-disk cache and
/// raises <see cref="DataChanged"/> when the file is updated by the collector.
/// </summary>
public abstract class CacheFileProvider : IDataProvider, IDisposable
{
    /// <summary>Cached data is considered stale once older than this.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(6);

    private readonly FileSystemWatcher? _watcher;

    protected CacheFileProvider()
    {
        try
        {
            Directory.CreateDirectory(UsageCache.Directory);
            _watcher = new FileSystemWatcher(UsageCache.Directory, $"{ProviderId}.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += (_, _) => DataChanged?.Invoke(this, EventArgs.Empty);
            _watcher.Created += (_, _) => DataChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Watching is best-effort; the timer-based refresh still picks up changes.
        }
    }

    public abstract string ProviderId { get; }
    public abstract string DisplayName { get; }

    public TimeSpan PollingInterval => TimeSpan.FromSeconds(60);

    public event EventHandler? DataChanged;

    public Task<ProviderAvailability> CheckAvailabilityAsync()
    {
        var record = UsageCache.TryRead(ProviderId);
        return Task.FromResult(record is null
            ? new ProviderAvailability(false,
                $"No cached data. Run the collector to sign in to {DisplayName} and fetch usage.")
            : new ProviderAvailability(true, null));
    }

    public Task<DataUsageSnapshot> FetchUsageAsync(CancellationToken ct)
    {
        var record = UsageCache.TryRead(ProviderId);
        if (record is null)
        {
            return Task.FromResult(new DataUsageSnapshot(
                ProviderId, DisplayName, IsUnlimited: false,
                TotalGb: null, UsedGb: 0, CycleResetsAt: null,
                FetchedAt: DateTimeOffset.MinValue,
                Message: "No data yet — run the collector."));
        }

        var age = DateTimeOffset.Now - record.FetchedAt;
        string? message = null;
        if (age > StaleAfter)
        {
            var days = (int)age.TotalDays;
            var ageText = days > 0 ? $"{days}d {age.Hours}h" : $"{age.Hours}h";
            message = $"Data is {ageText} old — re-run the collector.";
        }

        return Task.FromResult(new DataUsageSnapshot(
            ProviderId: ProviderId,
            PlanName: string.IsNullOrWhiteSpace(record.PlanName) ? DisplayName : record.PlanName,
            IsUnlimited: record.IsUnlimited,
            TotalGb: record.TotalGb,
            UsedGb: record.UsedGb,
            CycleResetsAt: record.CycleResetsAt,
            FetchedAt: record.FetchedAt,
            Message: message));
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Rogers wireless data usage.</summary>
public sealed class RogersProvider : CacheFileProvider
{
    public override string ProviderId => "rogers";
    public override string DisplayName => "Rogers";
}

/// <summary>Fido wireless data usage.</summary>
public sealed class FidoProvider : CacheFileProvider
{
    public override string ProviderId => "fido";
    public override string DisplayName => "Fido";
}
