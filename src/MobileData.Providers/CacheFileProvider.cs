using MobileData.Core;

namespace MobileData.Providers;

/// <summary>
/// Base provider that reads a carrier's usage from the shared on-disk cache and
/// raises <see cref="DataChanged"/> when the file is updated by the collector.
/// </summary>
public abstract class CacheFileProvider : IDataProvider, IDisposable
{
    /// <summary>Cached data is considered stale once older than this. Kept short
    /// so an expired collector session surfaces the Reconnect prompt quickly.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);

    /// <summary>A failed scrape attempt within this window counts as "the
    /// collector is actively failing" (session expired) rather than "the machine
    /// was asleep." The collector runs every 20 min, so this spans one missed
    /// cycle without treating a lidded laptop's old attempt as a live failure.</summary>
    private static readonly TimeSpan RecentAttemptWindow = TimeSpan.FromMinutes(45);

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
                Message: "No data yet — run the collector.",
                NeedsReconnect: true));
        }

        var age = DateTimeOffset.Now - record.FetchedAt;
        var days = (int)age.TotalDays;
        var ageText = days > 0 ? $"{days}d {age.Hours}h"
            : age.Hours > 0 ? $"{age.Hours}h"
            : $"{Math.Max(1, age.Minutes)}m";

        // The timestamp always shows; it renders muted while fresh and switches
        // to normal emphasis once the cache goes stale.
        var message = $"Last updated {ageText} ago.";
        var messageIsSubtle = age <= StaleAfter;
        var needsReconnect = false;
        if (age > StaleAfter)
        {
            // Only prompt a sign-in when the collector actually ran recently and
            // failed (session likely expired). If it simply hasn't run — laptop
            // asleep or offline — keep the neutral timestamp: the scheduled scrape
            // will catch up on its own, so don't cry "sign in again."
            var recentlyFailed = record.LastError is not null
                && record.LastAttemptAt is { } attempt
                && DateTimeOffset.Now - attempt < RecentAttemptWindow;

            if (recentlyFailed)
            {
                message = $"Couldn't refresh {DisplayName} data ({ageText} old). Tap Reconnect to sign in again.";
                needsReconnect = true;
            }
        }

        return Task.FromResult(new DataUsageSnapshot(
            ProviderId: ProviderId,
            PlanName: string.IsNullOrWhiteSpace(record.PlanName) ? DisplayName : record.PlanName,
            IsUnlimited: record.IsUnlimited,
            TotalGb: record.TotalGb,
            UsedGb: record.UsedGb,
            CycleResetsAt: record.CycleResetsAt,
            FetchedAt: record.FetchedAt,
            Message: message,
            NeedsReconnect: needsReconnect,
            MessageIsSubtle: messageIsSubtle));
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
