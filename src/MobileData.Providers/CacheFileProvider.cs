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

    /// <summary>How many missed billing cycles we're willing to assume through.
    /// One is routine — a few days on home Wi-Fi across the reset date and the
    /// collector never runs, since it skips scrapes when not tethered. Two means
    /// the cache is a month-plus old, where a full-bar "you have everything"
    /// card would be invention rather than inference.</summary>
    private const int MaxProjectedCycles = 1;

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

        var now = DateTimeOffset.Now;
        var age = now - record.FetchedAt;
        var days = (int)age.TotalDays;
        var ageText = days > 0 ? $"{days}d {age.Hours}h"
            : age.Hours > 0 ? $"{age.Hours}h"
            : $"{Math.Max(1, age.Minutes)}m";

        // A lapsed cycle means the allotment refilled. Nothing tells us so — the
        // collector skips scrapes when we're not tethered, so a stretch on home
        // Wi-Fi across the reset date strands the cache on last cycle's depleted
        // number. Roll the anniversary forward and assume the refill, but flag
        // it: a stale reading understates what's left, whereas an assumed reset
        // *overstates* it, so the card must not present it as measured.
        var cycleResetsAt = record.CycleResetsAt;
        var usedGb = record.UsedGb;
        var cycleProjected = false;
        var outlivedCycle = false;
        // Only when the reading itself predates the reset. A scrape taken on the
        // last day of a cycle reports an anchor of midnight *this morning*, which
        // is already past — projecting from that would throw away a number the
        // carrier just gave us.
        if (record.CycleResetsAt is { } observed && observed <= now && record.FetchedAt < observed)
        {
            if (ProjectReset(observed, now) is { } next)
            {
                cycleResetsAt = next;
                usedGb = 0;
                cycleProjected = true;
            }
            else
            {
                // Older than we'll project for. Keep the last observed numbers —
                // understating what's left is the safe direction to be wrong in —
                // but say plainly that they predate a reset.
                outlivedCycle = true;
            }
        }

        // The timestamp always shows; it renders muted while fresh and switches
        // to normal emphasis once the cache goes stale.
        var message = $"Last updated {ageText} ago.";
        var messageIsSubtle = age <= StaleAfter;
        var needsReconnect = false;

        if (cycleProjected)
        {
            // Say both halves out loud: what we're assuming, and how long it's
            // been since anything was actually measured. Never muted — an
            // assumption shouldn't read as quietly as a fresh reading.
            message = $"Assumed reset {cycleResetsAt:MMM d} — unconfirmed for {ageText}.";
            messageIsSubtle = false;
        }

        if (age > StaleAfter)
        {
            // Only prompt a sign-in when the collector actually ran recently and
            // failed (session likely expired). If it simply hasn't run — laptop
            // asleep or offline — keep the neutral timestamp: the scheduled scrape
            // will catch up on its own, so don't cry "sign in again."
            var recentlyFailed = record.LastError is not null
                && record.LastAttemptAt is { } attempt
                && now - attempt < RecentAttemptWindow;

            if (recentlyFailed)
            {
                message = $"Couldn't refresh {DisplayName} data ({ageText} old). Tap Reconnect to sign in again.";
                needsReconnect = true;
            }
            else if (outlivedCycle)
            {
                // A month without a successful scrape isn't a nap — something
                // needs a human, so prompt even though nothing failed just now.
                message = $"Reading is {ageText} old and predates a cycle reset. Tap Reconnect to sign in again.";
                needsReconnect = true;
            }
        }

        return Task.FromResult(new DataUsageSnapshot(
            ProviderId: ProviderId,
            PlanName: string.IsNullOrWhiteSpace(record.PlanName) ? DisplayName : record.PlanName,
            IsUnlimited: record.IsUnlimited,
            TotalGb: record.TotalGb,
            UsedGb: usedGb,
            CycleResetsAt: cycleResetsAt,
            FetchedAt: record.FetchedAt,
            Message: message,
            NeedsReconnect: needsReconnect,
            MessageIsSubtle: messageIsSubtle,
            CycleProjected: cycleProjected));
    }

    /// <summary>
    /// Roll a lapsed cycle-reset date forward to its next monthly anniversary.
    /// Carrier cycles are anchored to a day of the month, so once the observed
    /// reset has passed we can say when the next one lands — and that the
    /// allotment refilled in between. Returns null when the anchor is too old to
    /// project honestly (see <see cref="MaxProjectedCycles"/>).
    /// </summary>
    /// <remarks>
    /// The anchor is soft: Fido reports "N days remaining in bill cycle", which
    /// the collector turns into today+N — day granularity, unknown hour, and
    /// plausibly ±1 depending on whether "remaining" counts inclusively. That
    /// error carries into the projection, which is why the card shows "~MMM d"
    /// rather than a precise countdown once it's projecting.
    /// AddMonths clamps a 31st anchor into a short month on its own; capping the
    /// projection at one step also keeps that clamp from compounding.
    /// </remarks>
    internal static DateTimeOffset? ProjectReset(DateTimeOffset observedReset, DateTimeOffset now)
    {
        var next = observedReset;
        for (var i = 0; i < MaxProjectedCycles && next <= now; i++)
            next = next.AddMonths(1);

        return next > now ? next : null;
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
