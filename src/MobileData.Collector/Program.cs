using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using MobileData.Collector;
using MobileData.Core;

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
//
// NOTE: portal URLs and text patterns are tuned for Fido; if extraction fails
// the page text is dumped to the debug folder so patterns can be adjusted.

var providerId = (args.FirstOrDefault() ?? "").Trim().ToLowerInvariant();
if (providerId is not ("rogers" or "fido"))
{
    Console.Error.WriteLine("Usage: MobileData.Collector <rogers|fido> [--set-login] [--headless]");
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
var creds = CredentialStore.Load(providerId);

var profileDir = Path.Combine(UsageCache.Directory, "browser", providerId);
Directory.CreateDirectory(profileDir);

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchPersistentContextAsync(profileDir, new()
{
    Headless = headless,
    Channel = "chrome",
    ViewportSize = new() { Width = 1280, Height = 900 }
});

var page = browser.Pages.FirstOrDefault() ?? await browser.NewPageAsync();

Console.WriteLine($"Opening {config.DisplayName} usage page...");
try
{
    await page.GotoAsync(config.UsageUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
}
catch (Exception ex)
{
    Console.WriteLine($"(navigation warning: {ex.Message})");
}

// Wait for either the usage content or a login form to settle.
var loggedIn = await LoginFlow.WaitForUsageAsync(page, timeoutMs: 8000);
if (!loggedIn)
{
    if (creds is not null)
    {
        Console.WriteLine("Attempting auto-login...");
        await LoginFlow.TryAutoLoginAsync(page, config, creds);
        loggedIn = await LoginFlow.WaitForUsageAsync(page, timeoutMs: 30000);
    }

    if (!loggedIn)
    {
        if (headless)
        {
            Console.Error.WriteLine("Not signed in and could not auto-login (a visible run / 2FA may be needed).");
            return 3;
        }
        Console.WriteLine();
        Console.WriteLine("Finish signing in in the browser window (handle any 2FA),");
        Console.WriteLine("then make sure your DATA USAGE is visible on screen.");
        Console.Write("Press Enter here once the usage is shown... ");
        Console.ReadLine();
    }
}

var text = await page.InnerTextAsync("body");

// Save a debug dump so patterns can be tuned against the real page.
var debugDir = Path.Combine(UsageCache.Directory, "debug");
Directory.CreateDirectory(debugDir);
var dumpPath = Path.Combine(debugDir, $"{providerId}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
await File.WriteAllTextAsync(dumpPath, text);

var parsed = UsageParser.Parse(text);
if (parsed is null)
{
    Console.WriteLine();
    Console.WriteLine("Could not find data-usage numbers on the page.");
    Console.WriteLine($"Page text was saved to:\n  {dumpPath}");
    Console.WriteLine("Share that file (or the exact wording of the usage line) to tune the patterns.");
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
    FetchedAt = DateTimeOffset.Now
};
UsageCache.Write(record);

Console.WriteLine();
Console.WriteLine($"Saved: {UsageCache.PathFor(providerId)}");
Console.WriteLine(parsed.IsUnlimited
    ? $"  {parsed.UsedGb:F2} GB used (unlimited plan)"
    : $"  {parsed.UsedGb:F2} GB used of {parsed.TotalGb:F2} GB");
return 0;

// ---------------------------------------------------------------------------

/// <summary>Per-carrier portal settings.</summary>
internal sealed record CarrierConfig(string DisplayName, string UsageUrl)
{
    public static CarrierConfig For(string providerId) => providerId switch
    {
        "rogers" => new("Rogers", "https://www.rogers.com/consumer/profile/usage"),
        "fido" => new("Fido", "https://www.fido.ca/pages/#/usage"),
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
