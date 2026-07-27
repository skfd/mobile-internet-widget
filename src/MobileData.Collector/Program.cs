using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using MobileData.Collector;
using MobileData.Core;
using MobileData.Providers;

// Mobile data usage collector.
//
// Opens the carrier's account portal in a persistent browser profile (so the
// login session survives between runs) and scrapes remaining/used data into the
// widget's cache at %USERPROFILE%\.mobile-widget\{provider}.json.
//
// If credentials are stored (see --set-login), it fills the login form
// automatically. Otherwise, or if auto-login can't complete (e.g. 2FA), you sign
// in manually once and the persistent profile remembers you afterwards.
//
// Usage:
//   MobileData.Collector fido                # fetch usage
//   MobileData.Collector fido --set-login    # store DPAPI-encrypted credentials
//   MobileData.Collector fido --headless     # run without a visible window (needs a live session)
//   MobileData.Collector fido --attach        # scrape via your own Chrome (CDP on :9222) to pass login reCAPTCHA
//   MobileData.Collector fido --force          # scrape even when we don't look tethered (bypass the skip)
//
// NOTE: portal URLs and text patterns are tuned for Fido; if extraction fails
// the page text is dumped to the debug folder so patterns can be adjusted.

var providerId = (args.FirstOrDefault() ?? "").Trim().ToLowerInvariant();
if (providerId is not ("rogers" or "fido"))
{
    Console.Error.WriteLine("Usage: MobileData.Collector <rogers|fido> [--set-login] [--headless] [--attach] [--force]");
    return 1;
}

var config = CarrierConfig.For(providerId);

// --set-login: capture and store credentials, then exit.
if (args.Contains("--set-login"))
{
    Console.Write($"{config.DisplayName} username/email: ");
    var user = Console.ReadLine()?.Trim() ?? "";
    var pass = ConsoleInput.ReadPassword($"{config.DisplayName} password: ");
    if (user.Length == 0 || pass.Length == 0)
    {
        Console.Error.WriteLine("Username and password are required.");
        return 1;
    }
    CredentialStore.Save(providerId, new Credentials(user, pass));
    Console.WriteLine($"Saved encrypted credentials to {UsageCache.Directory}\\{providerId}.cred");
    return 0;
}

var headless = args.Contains("--headless");
var attach = args.Contains("--attach");
// --force, or a one-shot flag dropped by the widget's Refresh button (which can
// only start this collector's scheduled task, with no way to pass arguments).
// `|`, not `||`: consume any pending flag even when --force was passed, so a
// manual forced run doesn't leave one behind for the next scheduled run.
var force = args.Contains("--force") | UsageCache.ConsumeForce(providerId);
var creds = CredentialStore.Load(providerId);

// Skip the scrape when we're clearly not on the phone plan — home Wi-Fi,
// Ethernet, or offline. The carrier's number can't change while we're not
// tethered, so there's nothing to fetch. We leave the cache untouched (no
// failed-attempt record) so the widget keeps the last good numbers and simply
// ages to a neutral "last updated" line rather than a Reconnect nag.
//   NetworkDetector.IsTethered = cellular OR metered OR a hotspot SSID listed
//   in networks.json — the only dependable tether signal (see CLAUDE.md). If
//   your phone's SSID isn't listed and Windows didn't mark it metered, add it
//   to networks.json, or run with --force to scrape anyway.
if (!force)
{
    var net = NetworkDetector.Detect();
    if (!net.IsTethered)
    {
        var where = net.IsConnected
            ? $"on '{net.NetworkName}' (not a tethered/metered connection)"
            : "offline";
        Console.WriteLine(
            $"Not tethered — {where}. Skipping {config.DisplayName} scrape (use --force to override).");
        return 0;
    }
}

// Disposing the Playwright driver tears down whatever we started: it closes a
// browser we launched, and merely disconnects from one we attached to over CDP.
using var playwright = await Playwright.CreateAsync();

IPage page;
IPage? attachPage = null;  // the throwaway tab we open in the user's Chrome; closed when done
if (attach)
{
    // Reuse a session from the user's own Chrome, where the carrier's login
    // reCAPTCHA trusts the browser. Launch Chrome once with, e.g.:
    //   chrome.exe --remote-debugging-port=9222 --user-data-dir=%USERPROFILE%\.mobile-widget\chrome-attach
    // sign into the carrier there, then run the collector with --attach.
    // 127.0.0.1, not "localhost": Chrome's DevTools endpoint binds IPv4 only,
    // and "localhost" can resolve to IPv6 (::1) and be refused.
    const string endpoint = "http://127.0.0.1:9222";
    IBrowser attached;
    try
    {
        attached = await playwright.Chromium.ConnectOverCDPAsync(endpoint);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Could not connect to Chrome at {endpoint}.");
        Console.Error.WriteLine("Start Chrome with remote debugging first (PowerShell):");
        Console.Error.WriteLine("  & 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe' " +
            "--remote-debugging-port=9222 --user-data-dir=\"$env:USERPROFILE\\.mobile-widget\\chrome-attach\"");
        Console.Error.WriteLine($"then sign into {config.DisplayName}, and re-run with --attach.");
        Console.Error.WriteLine($"(details: {ex.Message})");
        RecordFailedAttempt(providerId, "debug Chrome not reachable on port 9222");
        return 4;
    }
    var context = attached.Contexts.FirstOrDefault() ?? await attached.NewContextAsync();
    // Use a fresh tab so we don't hijack whatever the user has open.
    page = attachPage = await context.NewPageAsync();
}
else
{
    var profileDir = Path.Combine(UsageCache.Directory, "browser", providerId);
    Directory.CreateDirectory(profileDir);
    var browser = await playwright.Chromium.LaunchPersistentContextAsync(profileDir, new()
    {
        Headless = headless,
        Channel = "chrome",
        ViewportSize = new() { Width = 1280, Height = 900 }
    });
    page = browser.Pages.FirstOrDefault() ?? await browser.NewPageAsync();
}

Console.WriteLine($"Opening {config.DisplayName} usage page...");
try
{
    await page.GotoAsync(config.UsageUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
}
catch (Exception ex)
{
    Console.WriteLine($"(navigation warning: {ex.Message})");
}

// Wait for either the usage content or a login form to settle. Give --attach a
// longer budget: it often runs against a just-launched Chrome, and the carrier's
// SPA can take well over 8s to render usage from a cold start — too short a wait
// looks like "not signed in" and skips a perfectly good session.
var loggedIn = await LoginFlow.WaitForUsageAsync(page, timeoutMs: attach ? 40000 : 8000);
if (!loggedIn)
{
    // Auto-login only applies to the automation profile. In --attach mode the
    // login happens once, by hand, in the user's own Chrome (reCAPTCHA trusts
    // it there); we never try to drive that form.
    if (!attach && creds is not null)
    {
        Console.WriteLine("Attempting auto-login...");
        await LoginFlow.TryAutoLoginAsync(page, config, creds);
        loggedIn = await LoginFlow.WaitForUsageAsync(page, timeoutMs: 30000);
    }

    if (!loggedIn)
    {
        if (attach)
        {
            // Never block on a prompt here: --attach is the unattended hourly
            // path. If the session lapsed, keep the old cache and ask for a
            // one-time manual sign-in in the debug Chrome.
            Console.Error.WriteLine(
                $"Not signed in. The {config.DisplayName} session in the debug Chrome (port 9222) " +
                "has expired — sign in once in that window; hourly scrapes resume automatically.");
            if (attachPage is not null) { try { await attachPage.CloseAsync(); } catch { } }
            RecordFailedAttempt(providerId, "session expired — not signed in");
            return 3;
        }
        if (headless)
        {
            Console.Error.WriteLine("Not signed in and could not auto-login (a visible run / 2FA may be needed).");
            RecordFailedAttempt(providerId, "not signed in (headless)");
            return 3;
        }
        Console.WriteLine();
        Console.WriteLine("Finish signing in in the browser window (handle any 2FA),");
        Console.WriteLine("then make sure your DATA USAGE is visible on screen.");
        Console.Write("Press Enter here once the usage is shown... ");
        Console.ReadLine();
    }
}

// Collect visible text from the main document AND every child frame — the
// carrier's self-serve app is often embedded in an iframe, so body text alone
// can come back blank even when usage is on screen.
var sections = new List<string>();
foreach (var frame in page.Frames)
{
    try
    {
        var frameText = await frame.Locator("body").InnerTextAsync();
        if (!string.IsNullOrWhiteSpace(frameText))
            sections.Add($"===== FRAME {frame.Url} =====\n{frameText}");
    }
    catch { /* frame detached or cross-origin — skip */ }
}
var text = string.Join("\n\n", sections);

// Save a debug dump so patterns can be tuned against the real page.
var debugDir = Path.Combine(UsageCache.Directory, "debug");
Directory.CreateDirectory(debugDir);
var dumpPath = Path.Combine(debugDir, $"{providerId}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
await File.WriteAllTextAsync(dumpPath,
    $"URL: {page.Url}\nFrames: {page.Frames.Count}\n\n{text}");

Console.WriteLine($"Landed on: {page.Url}  ({page.Frames.Count} frame(s), {sections.Count} with text)");

// Done reading the page; close our throwaway tab so attach runs don't pile up
// tabs in the user's Chrome hour after hour.
if (attachPage is not null) { try { await attachPage.CloseAsync(); } catch { } }

var parsed = UsageParser.Parse(text);
if (parsed is null)
{
    Console.WriteLine();
    Console.WriteLine("Could not find data-usage numbers on the page.");
    Console.WriteLine($"Page text was saved to:\n  {dumpPath}");
    Console.WriteLine("Share that file (or the exact wording of the usage line) to tune the patterns.");
    RecordFailedAttempt(providerId, "could not find usage numbers on page");
    return 2;
}

var record = new UsageCacheRecord
{
    Provider = providerId,
    PlanName = string.IsNullOrWhiteSpace(parsed.PlanName) ? config.DisplayName : parsed.PlanName!,
    IsUnlimited = parsed.IsUnlimited,
    TotalGb = parsed.TotalGb,
    UsedGb = parsed.UsedGb,
    CycleResetsAt = parsed.CycleResetsAt,
    FetchedAt = DateTimeOffset.Now,
    LastAttemptAt = DateTimeOffset.Now
};
UsageCache.Write(record);

Console.WriteLine();
Console.WriteLine($"Saved: {UsageCache.PathFor(providerId)}");
Console.WriteLine(parsed.IsUnlimited
    ? $"  {parsed.UsedGb:F2} GB used (unlimited plan)"
    : $"  {parsed.UsedGb:F2} GB used of {parsed.TotalGb:F2} GB");
return 0;

// Record a failed scrape attempt without discarding the last good numbers, so
// the widget can tell an actively-failing collector (expired session) apart from
// a machine that simply hasn't scraped recently (asleep/offline).
static void RecordFailedAttempt(string provider, string error)
{
    var rec = UsageCache.TryRead(provider) ?? new UsageCacheRecord { Provider = provider };
    rec.LastAttemptAt = DateTimeOffset.Now;
    rec.LastError = error;
    UsageCache.Write(rec);
}

// ---------------------------------------------------------------------------

/// <summary>Per-carrier portal settings.</summary>
internal sealed record CarrierConfig(string DisplayName, string UsageUrl)
{
    public static CarrierConfig For(string providerId) => providerId switch
    {
        "rogers" => new("Rogers", "https://www.rogers.com/consumer/profile/usage"),
        "fido" => new("Fido", "https://www.fido.ca/self-serve/wireless"),
        _ => throw new ArgumentOutOfRangeException(nameof(providerId))
    };
}

internal sealed record ParsedUsage(bool IsUnlimited, double UsedGb, double? TotalGb, DateTimeOffset? CycleResetsAt, string? PlanName);

/// <summary>
/// Extracts data usage from the Fido self-serve dashboard's rendered text.
///
/// The dashboard renders (whitespace varies):
///     38.78GB
///     Remaining data
///     0 GB
///     90.00 GB
///     Total data (plan+added)
///     90.00
///     GB
///     3 days remaining in bill cycle
///     My mobile plan
///     90GB, Talk &amp; Text - Plus
///
/// Patterns key off the stable text labels rather than positions/numbers.
/// </summary>
internal static class UsageParser
{
    public static ParsedUsage? Parse(string text)
    {
        var remaining = MatchGb(text,
            @"([\d.,]+)\s*GB\s+Remaining data",       // Fido: "38.78GB\nRemaining data"
            @"([\d.,]+)\s*GB\s+(?:remaining|left)",
            @"(?:remaining|left)[:\s]+([\d.,]+)\s*GB");

        var total = MatchGb(text,
            @"Total data \(plan\+added\)\s+([\d.,]+)", // Fido: label then "90.00"
            @"of\s+([\d.,]+)\s*GB",
            @"([\d.,]+)\s*GB\s+(?:total|plan|included|allotment|shared)");

        var used = MatchGb(text,
            @"([\d.,]+)\s*GB\s+used",
            @"used[:\s]+([\d.,]+)\s*GB");

        // Fido states remaining + total, not used — derive it.
        if (used is null && total is not null && remaining is not null)
            used = Math.Max(0, total.Value - remaining.Value);

        // A truly unlimited-data plan: no numeric remaining/total to work with.
        var isUnlimited = remaining is null && total is null &&
                          Regex.IsMatch(text, @"unlimited\s+data", RegexOptions.IgnoreCase);

        if (used is null && !isUnlimited)
            return null;

        var reset = ParseResetDate(text);
        var plan = ParsePlanName(text);
        return new ParsedUsage(isUnlimited, used ?? 0, isUnlimited ? null : total, reset, plan);
    }

    private static double? MatchGb(string text, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (m.Success &&
                double.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }
        return null;
    }

    private static DateTimeOffset? ParseResetDate(string text)
    {
        // Fido: "3 days remaining in bill cycle" -> reset at end of today + N days.
        var days = Regex.Match(text, @"(\d+)\s+days?\s+remaining in bill cycle", RegexOptions.IgnoreCase);
        if (days.Success && int.TryParse(days.Groups[1].Value, out var n))
            return DateTimeOffset.Now.Date.AddDays(n);

        // Fallback: an explicit date, e.g. "resets on July 15, 2026".
        var m = Regex.Match(text,
            @"(?:resets?|renews?|bill(?:ing)? cycle)[^\n]*?on\s+([A-Za-z]+ \d{1,2}(?:,? \d{4})?)",
            RegexOptions.IgnoreCase);
        if (m.Success &&
            DateTime.TryParse(m.Groups[1].Value, CultureInfo.CurrentCulture, DateTimeStyles.None, out var dt))
        {
            return new DateTimeOffset(dt);
        }
        return null;
    }

    private static string? ParsePlanName(string text)
    {
        // Fido: the line following "My mobile plan".
        var m = Regex.Match(text, @"My mobile plan\s*\n\s*([^\n]+)", RegexOptions.IgnoreCase);
        var name = m.Success ? m.Groups[1].Value.Trim() : null;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
